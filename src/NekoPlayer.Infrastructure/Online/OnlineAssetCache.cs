using System.Buffers;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace NekoPlayer.Infrastructure.Online;

/// <summary>Bounded disk cache for HTTP cover assets. Each caller owns its returned stream.</summary>
public sealed class OnlineAssetCache
{
    public const long DefaultDiskBudgetBytes = 128L * 1024 * 1024;
    public const long DefaultMaximumAssetBytes = 10L * 1024 * 1024;
    public const int DefaultConcurrentDownloads = 4;
    public const int DefaultMaximumEntries = 1024;

    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly HttpClient _httpClient;
    private readonly long _diskBudgetBytes;
    private readonly long _maximumAssetBytes;
    private readonly int _maximumEntries;
    private readonly SemaphoreSlim _downloadSlots;
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DownloadOperation> _pending = new(StringComparer.Ordinal);
    private bool _initialized;
    private long _diskBytes;
    private long _reservedBytes;
    private long _accessSequence;

    public OnlineAssetCache(string directory, HttpClient? httpClient = null,
        long diskBudgetBytes = DefaultDiskBudgetBytes, long maximumAssetBytes = DefaultMaximumAssetBytes,
        int concurrentDownloads = DefaultConcurrentDownloads, int maximumEntries = DefaultMaximumEntries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (diskBudgetBytes <= 0 || maximumAssetBytes <= 0 || maximumAssetBytes > diskBudgetBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumAssetBytes));
        if (concurrentDownloads <= 0) throw new ArgumentOutOfRangeException(nameof(concurrentDownloads));
        if (maximumEntries <= 0) throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _directory = Path.GetFullPath(directory);
        _httpClient = httpClient ?? SharedHttpClient;
        _diskBudgetBytes = diskBudgetBytes;
        _maximumAssetBytes = maximumAssetBytes;
        _maximumEntries = maximumEntries;
        var downloadLimit = (int)Math.Min(concurrentDownloads, diskBudgetBytes / maximumAssetBytes);
        _downloadSlots = new SemaphoreSlim(downloadLimit, downloadLimit);
    }

    public long DiskBytes { get { lock (_gate) { EnsureInitialized(); return _diskBytes; } } }
    public int PendingDownloads { get { lock (_gate) return _pending.Count; } }

    public async Task<Stream> OpenCoverAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        ValidateUri(uri);
        cancellationToken.ThrowIfCancellationRequested();
        var key = GetKey(uri);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DownloadOperation operation;
            lock (_gate)
            {
                EnsureInitialized();
                cancellationToken.ThrowIfCancellationRequested();
                var cached = TryOpenCached(key);
                if (cached is not null) return cached;
                if (!_pending.TryGetValue(key, out operation!) || operation.Cancellation.IsCancellationRequested)
                {
                    operation = new DownloadOperation();
                    _pending[key] = operation;
                    // Starting outside the caller's token ensures cleanup also runs if the caller cancels immediately.
                    operation.Task = Task.Run(() => DownloadAsync(uri, key, operation));
                    var started = operation;
                    _ = operation.Task.ContinueWith(completed =>
                    {
                        _ = completed.Exception; // Observe failures even when all waiting controls have gone away.
                        lock (_gate)
                        {
                            if (_pending.TryGetValue(key, out var current) && ReferenceEquals(current, started))
                                _pending.Remove(key);
                            if (started.Waiters == 0) started.Cancellation.Dispose();
                        }
                    }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                operation.Waiters++;
            }

            try
            {
                await operation.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var downloaded = TryOpenCached(key);
                    if (downloaded is not null) return downloaded;
                }
                // A very small configured budget may evict this asset before the waiter opens it.
            }
            finally
            {
                lock (_gate)
                {
                    operation.Waiters--;
                    if (operation.Waiters == 0)
                    {
                        if (operation.Task.IsCompleted) operation.Cancellation.Dispose();
                        else operation.Cancellation.Cancel();
                    }
                }
            }
        }
    }

    public void Invalidate(Uri uri)
    {
        ValidateUri(uri);
        lock (_gate)
        {
            EnsureInitialized();
            RemoveEntry(GetKey(uri));
        }
    }

    private async Task DownloadAsync(Uri uri, string key, DownloadOperation operation)
    {
        var token = operation.Cancellation.Token;
        var temporaryPath = Path.Combine(_directory, key + "." + Guid.NewGuid().ToString("N") + ".tmp");
        var acquired = false;
        var reservationHeld = false;
        try
        {
            await _downloadSlots.WaitAsync(token).ConfigureAwait(false);
            acquired = true;
            using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > _maximumAssetBytes)
                throw new InvalidDataException("在线封面超过单文件大小限制。");
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
                !mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("在线封面响应不是图片。");

            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(_directory);
                // Reserve the largest permitted file before writing, so temporary downloads also fit the disk budget.
                TrimToBudget(_diskBudgetBytes - _reservedBytes - _maximumAssetBytes, _maximumEntries);
                _reservedBytes += _maximumAssetBytes;
                reservationHeld = true;
            }

            long bytes = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false))
            await using (var output = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 32768, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = ArrayPool<byte>.Shared.Rent(32768);
                try
                {
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), token).ConfigureAwait(false);
                        if (read == 0) break;
                        bytes += read;
                        if (bytes > _maximumAssetBytes)
                            throw new InvalidDataException("在线封面超过单文件大小限制。");
                        await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                }
                finally { ArrayPool<byte>.Shared.Return(buffer); }
            }
            if (bytes == 0) throw new InvalidDataException("在线封面为空。");
            token.ThrowIfCancellationRequested();
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                RemoveEntry(key);
                TrimToBudget(_diskBudgetBytes - (_reservedBytes - _maximumAssetBytes) - bytes, _maximumEntries - 1);
                var path = CachePath(key);
                File.Move(temporaryPath, path, overwrite: true);
                _entries[key] = new CacheEntry(bytes, ++_accessSequence);
                _diskBytes += bytes;
                _reservedBytes -= _maximumAssetBytes;
                reservationHeld = false;
            }
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch (IOException) { }
            if (reservationHeld)
                lock (_gate) _reservedBytes -= _maximumAssetBytes;
            if (acquired) _downloadSlots.Release();
        }
    }

    private Stream? TryOpenCached(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return null;
        try
        {
            // Delete sharing lets the LRU evict files while consumers finish decoding their open streams.
            var stream = new FileStream(CachePath(key), FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 32768, FileOptions.SequentialScan);
            if (stream.Length != entry.Bytes || stream.Length <= 0 || stream.Length > _maximumAssetBytes)
            {
                stream.Dispose();
                RemoveEntry(key);
                return null;
            }
            entry.LastAccess = ++_accessSequence;
            try { File.SetLastAccessTimeUtc(CachePath(key), DateTime.UtcNow); } catch (IOException) { }
            return stream;
        }
        catch (FileNotFoundException) { RemoveEntry(key); return null; }
        catch (DirectoryNotFoundException) { RemoveEntry(key); return null; }
    }

    private void EnsureInitialized()
    {
        if (_initialized) return;
        Directory.CreateDirectory(_directory);
        _entries.Clear();
        _diskBytes = 0;
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.cover").OrderBy(x => x.LastAccessTimeUtc))
        {
            if (file.Length <= 0 || file.Length > _maximumAssetBytes) { file.Delete(); continue; }
            _entries[Path.GetFileNameWithoutExtension(file.Name)] = new CacheEntry(file.Length, ++_accessSequence);
            _diskBytes += file.Length;
        }
        // Incomplete downloads from a prior process never become cache hits.
        foreach (var file in new DirectoryInfo(_directory).EnumerateFiles("*.tmp"))
            try { file.Delete(); } catch (IOException) { }
        TrimToBudget(_diskBudgetBytes, _maximumEntries);
        _initialized = true;
    }

    private void TrimToBudget(long budget, int maximumEntries)
    {
        while ((_diskBytes > budget || _entries.Count > maximumEntries) && _entries.Count > 0)
        {
            var oldest = _entries.MinBy(x => x.Value.LastAccess);
            RemoveEntry(oldest.Key);
        }
    }

    private void RemoveEntry(string key)
    {
        if (!_entries.TryGetValue(key, out var entry)) return;
        try { File.Delete(CachePath(key)); } catch (DirectoryNotFoundException) { }
        _entries.Remove(key);
        _diskBytes -= entry.Bytes;
    }

    private string CachePath(string key) => Path.Combine(_directory, key + ".cover");
    private static string GetKey(Uri uri) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));
    private static SocketsHttpHandler CreateHttpHandler() => new()
    {
        // Public cover assets use this application's own direct transport, independent of environment proxies.
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli
    };

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient(CreateHttpHandler()) { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("NekoPlayer/1.0");
        return client;
    }

    private static void ValidateUri(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException("在线封面必须使用 HTTP 或 HTTPS。", nameof(uri));
    }

    private sealed class CacheEntry(long bytes, long lastAccess)
    {
        public long Bytes { get; } = bytes;
        public long LastAccess { get; set; } = lastAccess;
    }

    private sealed class DownloadOperation
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
        public int Waiters { get; set; }
    }
}
