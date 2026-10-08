using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Avalonia.Media.Imaging;
using NekoPlayer.App.Controls;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class CoverCancellationTests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");

    [Fact]
    public void DefaultCoverTransportUsesDirectRequestsAndStandardTlsValidation()
    {
        var factory = typeof(OnlineAssetCache).GetMethod("CreateHttpHandler", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(factory);
        using var handler = Assert.IsType<SocketsHttpHandler>(factory!.Invoke(null, null));
        Assert.False(handler.UseProxy);
        Assert.False(handler.UseCookies);
        Assert.Null(handler.SslOptions.RemoteCertificateValidationCallback);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            handler.AutomaticDecompression);
    }

    [Theory]
    [InlineData("https://user:password@covers.example/path.jpg?signature=secret&cookie=private#token", "covers.example")]
    [InlineData("https://[invalid?signature=secret", "无效的在线封面地址")]
    public void CoverLogDescriptionContainsNoUrlCredentialsOrQuery(string source, string expected)
    {
        var describe = typeof(TrackCoverProvider).GetMethod("SafeCoverSource", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(describe);
        Assert.Equal(expected, describe!.Invoke(null, [source]));
    }

    [Fact]
    public async Task CancelAfterDecodeDisposesRealBitmapAndPreservesSharedDefault()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(Path.GetTempPath(), "NekoCover-" + Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(path, TinyPng);
        Bitmap? decoded = null;
        var provider = new TrackCoverProvider(() => new MemoryStream(TinyPng, false), decodeCover: (stream, width) =>
        {
            decoded = Bitmap.DecodeToWidth(stream, width);
            cancellation.Cancel();
            return decoded;
        });
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.GetCoverAsync(path, 80, cancellation.Token));
            Assert.NotNull(decoded);
            using var realOutput = new MemoryStream();
            Assert.NotNull(Record.Exception(() => decoded!.Save(realOutput)));
            var fallback = await provider.GetCoverAsync(null, 80);
            Assert.True(fallback.IsDefault);
            using var defaultOutput = new MemoryStream();
            fallback.Image!.Save(defaultOutput);
            Assert.True(defaultOutput.Length > 0);
        }
        finally { File.Delete(path); provider.DefaultCover?.Dispose(); }
    }

    [Fact]
    public async Task FileUriAndHttpUriBothDecodeAsRealCovers()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        using var directory = new CacheDirectory();
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(ImageResponse(TinyPng))));
        var cache = new OnlineAssetCache(directory.Path, client);
        var provider = new TrackCoverProvider(() => new MemoryStream(TinyPng, false), cache);
        var local = System.IO.Path.Combine(directory.Path, "local.png");
        File.WriteAllBytes(local, TinyPng);
        try
        {
            foreach (var source in new[] { new Uri(local).AbsoluteUri, "https://covers.example/valid.png" })
            {
                var result = await provider.GetCoverAsync(source, 80);
                Assert.False(result.IsDefault);
                result.Image?.Dispose();
            }
        }
        finally { provider.DefaultCover?.Dispose(); }
    }

    [Fact]
    public async Task TwoDecodersRunAtOnceAndCanceledQueuedCoversNeverDecode()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        using var directory = new CacheDirectory();
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var twoStarted = NewSignal();
        var active = 0;
        var maximum = 0;
        var decodes = 0;
        var countGate = new object();
        var path = System.IO.Path.Combine(directory.Path, "local.png");
        File.WriteAllBytes(path, TinyPng);
        var provider = new TrackCoverProvider(() => new MemoryStream(TinyPng, false), decodeCover: (stream, width) =>
        {
            lock (countGate)
            {
                active++;
                decodes++;
                maximum = Math.Max(maximum, active);
                if (active == 2) twoStarted.TrySetResult();
            }
            try
            {
                if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Decoder test did not release its gate.");
                return Bitmap.DecodeToWidth(stream, width);
            }
            finally { lock (countGate) active--; }
        });
        var first = provider.GetCoverAsync(path, 80);
        var second = provider.GetCoverAsync(path, 80);
        try
        {
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stale = provider.GetCoverAsync(path, 80, cancellation.Token);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stale);
            release.Set();
            var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
            foreach (var result in results) result.Image?.Dispose();
            Assert.Equal(2, maximum);
            Assert.Equal(2, decodes);
        }
        finally
        {
            release.Set();
            provider.DefaultCover?.Dispose();
        }
    }

    [Fact]
    public async Task InvalidRemoteImageFallsBackAndIsRemovedFromDiskCache()
    {
        AvaloniaTestRuntime.EnsureInitialized();
        using var directory = new CacheDirectory();
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(ImageResponse([1, 2, 3, 4]))));
        var cache = new OnlineAssetCache(directory.Path, client);
        var provider = new TrackCoverProvider(() => new MemoryStream(TinyPng, false), cache);
        try
        {
            var result = await provider.GetCoverAsync("http://covers.example/corrupt.png", 80);
            Assert.True(result.IsDefault);
            Assert.Same(provider.DefaultCover, result.Image);
            Assert.Equal(0, cache.DiskBytes);
        }
        finally { provider.DefaultCover?.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizeResponseIsRejectedBeforeBecomingCacheEntry(bool unknownLength)
    {
        using var directory = new CacheDirectory();
        using var client = new HttpClient(new StubHandler((_, _) =>
        {
            HttpContent content = unknownLength
                ? new StreamContent(new NonSeekableStream(new byte[200]))
                : new ByteArrayContent(new byte[200]);
            content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }));
        var cache = new OnlineAssetCache(directory.Path, client, diskBudgetBytes: 300, maximumAssetBytes: 100);
        await Assert.ThrowsAsync<InvalidDataException>(() => cache.OpenCoverAsync(new Uri("https://covers.example/large.png")));
        Assert.Equal(0, cache.DiskBytes);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task CancelOneWaiterDoesNotCancelAnotherWaiterForSameAsset()
    {
        using var directory = new CacheDirectory();
        using var firstCancellation = new CancellationTokenSource();
        var started = NewSignal();
        var release = NewSignal();
        var requests = 0;
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            Interlocked.Increment(ref requests);
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return ImageResponse(TinyPng);
        }));
        var cache = new OnlineAssetCache(directory.Path, client);
        var uri = new Uri("https://covers.example/shared.png");
        var first = cache.OpenCoverAsync(uri, firstCancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = cache.OpenCoverAsync(uri);
        firstCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.TrySetResult();
        await using var stream = await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TinyPng.Length, stream.Length);
        Assert.Equal(1, requests);
        await WaitUntilAsync(() => cache.PendingDownloads == 0);
    }

    [Fact]
    public async Task LastWaiterCancellationRemovesPendingOperationAndTemporaryFile()
    {
        using var directory = new CacheDirectory();
        using var cancellation = new CancellationTokenSource();
        var started = NewSignal();
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return ImageResponse(TinyPng);
        }));
        var cache = new OnlineAssetCache(directory.Path, client);
        var pending = cache.OpenCoverAsync(new Uri("https://covers.example/canceled.png"), cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await WaitUntilAsync(() => cache.PendingDownloads == 0);
        Assert.Empty(Directory.EnumerateFiles(directory.Path));
    }

    [Fact]
    public async Task DiskBudgetEvictsLeastRecentlyUsedAsset()
    {
        using var directory = new CacheDirectory();
        var requests = new Dictionary<string, int>();
        using var client = new HttpClient(new StubHandler((request, _) =>
        {
            var path = request.RequestUri!.AbsolutePath;
            lock (requests) requests[path] = requests.GetValueOrDefault(path) + 1;
            return Task.FromResult(ImageResponse(new byte[8]));
        }));
        var cache = new OnlineAssetCache(directory.Path, client, diskBudgetBytes: 16, maximumAssetBytes: 8);
        foreach (var asset in new[] { "a", "b", "a", "c", "a", "b" })
        {
            await using var stream = await cache.OpenCoverAsync(new Uri("https://covers.example/" + asset));
            Assert.True(cache.DiskBytes <= 16);
        }
        Assert.Equal(1, requests["/a"]);
        Assert.Equal(2, requests["/b"]);
        Assert.Equal(1, requests["/c"]);
        Assert.Equal(2, Directory.EnumerateFiles(directory.Path, "*.cover").Count());
    }

    [Fact]
    public async Task EntryCountAlsoBoundsTinyAssets()
    {
        using var directory = new CacheDirectory();
        using var client = new HttpClient(new StubHandler((_, _) => Task.FromResult(ImageResponse(new byte[1]))));
        var cache = new OnlineAssetCache(directory.Path, client, diskBudgetBytes: 100, maximumAssetBytes: 10, maximumEntries: 2);
        for (var i = 0; i < 5; i++)
        {
            await using var stream = await cache.OpenCoverAsync(new Uri("https://covers.example/" + i));
        }
        Assert.Equal(2, Directory.EnumerateFiles(directory.Path, "*.cover").Count());
        Assert.Equal(2, cache.DiskBytes);
    }

    [Fact]
    public async Task AtMostFourRemoteDownloadsRunAtOnce()
    {
        using var directory = new CacheDirectory();
        using var release = new SemaphoreSlim(0);
        var fourStarted = NewSignal();
        var active = 0;
        var maximum = 0;
        var countGate = new object();
        using var client = new HttpClient(new StubHandler(async (_, token) =>
        {
            lock (countGate)
            {
                active++;
                maximum = Math.Max(maximum, active);
                if (active == 4) fourStarted.TrySetResult();
            }
            try { await release.WaitAsync(token); return ImageResponse(TinyPng); }
            finally { lock (countGate) active--; }
        }));
        var cache = new OnlineAssetCache(directory.Path, client);
        var tasks = Enumerable.Range(0, 10).Select(i => cache.OpenCoverAsync(new Uri("https://covers.example/" + i))).ToArray();
        await fourStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        release.Release(10);
        var streams = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        foreach (var stream in streams) stream.Dispose();
        Assert.Equal(4, maximum);
        await WaitUntilAsync(() => cache.PendingDownloads == 0);
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static HttpResponseMessage ImageResponse(byte[] bytes)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => handle(request, cancellationToken);
    }

    private sealed class NonSeekableStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
    }

    private sealed class CacheDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NekoCoverTests-" + Guid.NewGuid().ToString("N"));
        public CacheDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
