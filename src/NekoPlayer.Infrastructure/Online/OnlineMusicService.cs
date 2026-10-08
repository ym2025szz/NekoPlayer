using System.Text.Json;
using System.Text.Json.Nodes;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Infrastructure.Online;

/// <summary>Provider-neutral metadata searches; playback addresses are resolved afresh for each session.</summary>
public sealed class OnlineMusicService : IOnlineMusicService
{
    private const int MaximumCacheEntries = 100;
    private const long MaximumCacheBytes = 10 * 1024 * 1024;
    private static readonly TimeSpan SearchCacheLifetime = TimeSpan.FromMinutes(5);
    private static readonly IReadOnlyList<MusicProviderInfo> ProviderList = Array.AsReadOnly(new[]
    {
        new MusicProviderInfo("netease", "网易云"), new MusicProviderInfo("qq", "QQ 音乐"),
        new MusicProviderInfo("kuwo", "酷我"), new MusicProviderInfo("kugou", "酷狗"),
        new MusicProviderInfo("qishui", "汽水", Experimental: true, Message: "实验性来源，接口可用性需实际请求确认")
    });
    private readonly Dictionary<string, GatewayMusicProvider> _providers;
    private readonly SemaphoreSlim _globalRequests = new(5, 5);
    private readonly object _cacheLock = new();
    private readonly Dictionary<SearchKey, CacheEntry> _cache = [];
    private readonly Dictionary<SearchKey, SearchOperation> _inFlight = [];
    private long _cacheBytes;
    private readonly IProviderAccountService? _accounts;

    public OnlineMusicService(IGatewayRuntime runtime, IProviderAccountService? accounts = null)
    {
        _accounts = accounts;
        _providers = ProviderList.ToDictionary(info => info.Id,
            info => new GatewayMusicProvider(info, runtime, _globalRequests), StringComparer.Ordinal);
    }

    public IReadOnlyList<MusicProviderInfo> Providers => ProviderList;

    public async Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var provider = GetProvider(request.ProviderId);
        var query = request.Query?.Trim() ?? "";
        if (query.Length == 0) return new MusicSearchPage(provider.Info.Id, Math.Max(1, request.Page), [], false);
        if (query.Length > 512) throw new ArgumentException("搜索内容过长", nameof(request));
        if (_accounts is not null) await _accounts.EnsureProviderReadyAsync(provider.Info.Id, cancellationToken).ConfigureAwait(false);
        var key = new SearchKey(provider.Info.Id, query, Math.Clamp(request.Page, 1, 10000), Math.Clamp(request.PageSize, 1, 100), _accounts?.GetProviderRevision(provider.Info.Id) ?? 0);
        SearchOperation pending;
        lock (_cacheLock)
        {
            RemoveExpired(DateTimeOffset.UtcNow);
            if (_cache.TryGetValue(key, out var cached))
            {
                cached.LastUsed = DateTimeOffset.UtcNow;
                return MapCached(cached, key);
            }
            if (!_inFlight.TryGetValue(key, out pending!))
            {
                pending = new SearchOperation();
                pending.Task = SearchAndCacheAsync(provider, key, pending);
                _inFlight.Add(key, pending);
                _ = pending.Task.ContinueWith(task => { _ = task.Exception; pending.Cancellation.Dispose(); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            pending.Waiters++;
        }
        // A producer survives while another equal request needs it. Abandoned queries relinquish the source semaphore.
        try
        {
            var result = await pending.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return MapCached(result, key);
        }
        finally
        {
            lock (_cacheLock)
            {
                pending.Waiters--;
                if (pending.Waiters == 0 && !pending.Task.IsCompleted)
                {
                    if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, pending)) _inFlight.Remove(key);
                    try { pending.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                }
            }
        }
    }

    private async Task<CacheEntry> SearchAndCacheAsync(GatewayMusicProvider provider, SearchKey key, SearchOperation operation)
    {
        await Task.Yield();
        try
        {
            var rawResponse = await provider.SearchMetadataAsync(key.Query, key.Page, key.PageSize, operation.Cancellation.Token).ConfigureAwait(false);
            operation.Cancellation.Token.ThrowIfCancellationRequested();
            // Validate before caching so invalid/error responses never become apparently successful empty results.
            var page = GatewayMusicProvider.MapSearch(rawResponse, key.ProviderId, key.Page);
            // Rebuild the wire snapshot from stable metadata only; no unrecognised response fields or playback URLs enter the cache.
            var snapshot = JsonSerializer.SerializeToUtf8Bytes(new
            {
                page = page.Page, hasMore = page.HasMore, warning = page.Warning,
                tracks = page.Tracks.Select(track => new
                {
                    providerTrackId = track.ProviderTrackId, title = track.Title, artist = track.Artist, album = track.Album,
                    durationSeconds = track.Duration.TotalSeconds, coverUrl = track.CoverUrl, versionLabel = track.VersionLabel,
                    availability = track.Availability.ToString().ToLowerInvariant(), restrictionReason = track.RestrictionReason,
                    providerMetadataJson = track.ProviderMetadataJson
                }).ToArray()
            });
            var now = DateTimeOffset.UtcNow;
            var entry = new CacheEntry(snapshot, now);
            lock (_cacheLock)
            {
                RemoveExpired(now);
                if (entry.Bytes <= MaximumCacheBytes)
                {
                    while (_cache.Count >= MaximumCacheEntries || _cacheBytes + entry.Bytes > MaximumCacheBytes)
                    {
                        var oldest = _cache.MinBy(pair => pair.Value.LastUsed);
                        _cache.Remove(oldest.Key);
                        _cacheBytes -= oldest.Value.Bytes;
                    }
                    _cache[key] = entry;
                    _cacheBytes += entry.Bytes;
                }
            }
            return entry;
        }
        finally
        {
            lock (_cacheLock)
                if (_inFlight.TryGetValue(key, out var current) && ReferenceEquals(current, operation)) _inFlight.Remove(key);
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var pair in _cache.Where(pair => now - pair.Value.Created >= SearchCacheLifetime).ToArray())
        {
            _cache.Remove(pair.Key);
            _cacheBytes -= pair.Value.Bytes;
        }
    }

    private static MusicSearchPage MapCached(CacheEntry entry, SearchKey key)
    {
        using var document = JsonDocument.Parse(entry.Data);
        return GatewayMusicProvider.MapSearch(document.RootElement, key.ProviderId, key.Page);
    }

    public async Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var provider = GetProviderForTrack(track);
        if (_accounts is not null) await _accounts.EnsureProviderReadyAsync(provider.Info.Id, cancellationToken).ConfigureAwait(false);
        return await provider.ResolveAsync(track, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var provider = GetProviderForTrack(track);
        if (_accounts is not null) await _accounts.EnsureProviderReadyAsync(provider.Info.Id, cancellationToken).ConfigureAwait(false);
        return await provider.GetLyricsAsync(track, cancellationToken).ConfigureAwait(false);
    }

    private GatewayMusicProvider GetProviderForTrack(Track track)
    {
        if (!track.IsOnline || string.IsNullOrWhiteSpace(track.ProviderTrackId))
            throw new ArgumentException("曲目缺少在线来源标识", nameof(track));
        return GetProvider(track.ProviderId);
    }

    private GatewayMusicProvider GetProvider(string? id)
    {
        if (id is not null && _providers.TryGetValue(id.Trim().ToLowerInvariant(), out var provider)) return provider;
        throw new GatewayException(GatewayFailureKind.Provider, "provider_unsupported", "不支持该音乐来源");
    }

    private readonly record struct SearchKey(string ProviderId, string Query, int Page, int PageSize, long AccountRevision);
    private sealed class SearchOperation
    {
        public Task<CacheEntry> Task { get; set; } = null!;
        public CancellationTokenSource Cancellation { get; } = new();
        public int Waiters { get; set; }
    }
    private sealed class CacheEntry(byte[] data, DateTimeOffset created)
    {
        public byte[] Data { get; } = data;
        public long Bytes => Data.LongLength;
        public DateTimeOffset Created { get; } = created;
        public DateTimeOffset LastUsed { get; set; } = created;
    }
}

internal sealed class GatewayMusicProvider(MusicProviderInfo info, IGatewayRuntime runtime, SemaphoreSlim globalRequests) : IMusicProvider
{
    private readonly SemaphoreSlim _sourceRequests = new(1, 1);
    private readonly LrcParser _lyricsParser = new();
    public MusicProviderInfo Info { get; } = info;

    public async Task<MusicSearchPage> SearchAsync(string query, int page, int pageSize, CancellationToken cancellationToken = default) =>
        MapSearch(await SearchMetadataAsync(query, page, pageSize, cancellationToken).ConfigureAwait(false), Info.Id, page);

    internal Task<JsonElement> SearchMetadataAsync(string query, int page, int pageSize, CancellationToken cancellationToken) =>
        SendAsync("/v1/search", new { providerId = Info.Id, query, page, pageSize }, cancellationToken);

    public async Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("/v1/resolve", CreateTrackPayload(track), cancellationToken).ConfigureAwait(false);
        RequireObject(response);
        var availability = ParseAvailability(String(response, "availability"));
        var reason = String(response, "reason");
        if (availability is MusicAvailability.Unknown or MusicAvailability.Unavailable)
            return new PlaybackResolution(availability, null, reason ?? "当前播放权限尚未确认");
        var url = String(response, "url");
        if (!IsHttpUrl(url))
            return new PlaybackResolution(MusicAvailability.Unavailable, null, reason ?? "该来源未提供可播放地址");
        DateTimeOffset? expiresAt = null;
        if (DateTimeOffset.TryParse(String(response, "expiresAt"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var expires)) expiresAt = expires;
        if (expiresAt <= DateTimeOffset.UtcNow)
            return new PlaybackResolution(MusicAvailability.Unavailable, null, "播放地址已过期，请重试");
        var preview = availability == MusicAvailability.Preview;
        var duration = Seconds(response, preview ? "previewDurationSeconds" : "durationSeconds") ?? Seconds(response, "durationSeconds");
        if (preview && (duration is null || duration <= TimeSpan.Zero))
            return new PlaybackResolution(MusicAvailability.Unavailable, null, reason ?? "该来源未提供有效的试听时长");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response.TryGetProperty("headers", out var wireHeaders) && wireHeaders.ValueKind == JsonValueKind.Object)
        {
            foreach (var header in wireHeaders.EnumerateObject())
            {
                if (headers.Count >= 32 || header.Value.ValueKind != JsonValueKind.String) break;
                var value = header.Value.GetString() ?? "";
                if (header.Name.Length > 128 || value.Length > 4096 || header.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
                    value.Contains('\r') || value.Contains('\n')) continue;
                headers[header.Name] = value;
            }
        }
        var canSeek = response.TryGetProperty("canSeek", out var seek) && seek.ValueKind == JsonValueKind.True;
        var source = new AudioSource(url!, IsRemote: true, Duration: duration, Headers: headers, CanSeek: canSeek,
            IsPreview: preview, PreviewStart: preview ? Seconds(response, "previewStartSeconds", allowZero: true) : null,
            ProviderId: Info.Id, ProviderTrackId: track.ProviderTrackId, ExpiresAt: expiresAt);
        return new PlaybackResolution(availability, source, reason);
    }

    public async Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync("/v1/lyrics", CreateTrackPayload(track), cancellationToken).ConfigureAwait(false);
        RequireObject(response);
        if (!response.TryGetProperty("lrc", out var lrc) || lrc.ValueKind != JsonValueKind.String)
            throw new GatewayException(GatewayFailureKind.Protocol, "lyrics_shape", "音乐来源的歌词格式不正确");
        return _lyricsParser.Parse(lrc.GetString() ?? "");
    }

    private object CreateTrackPayload(Track track) => new
    {
        providerId = Info.Id, providerTrackId = track.ProviderTrackId,
        metadata = ParseMetadata(track.ProviderMetadataJson)
    };

    private async Task<JsonElement> SendAsync(string route, object payload, CancellationToken cancellationToken)
    {
        await _sourceRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await globalRequests.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return await runtime.SendAsync(route, payload, cancellationToken).ConfigureAwait(false); }
            finally { globalRequests.Release(); }
        }
        finally { _sourceRequests.Release(); }
    }

    internal static MusicSearchPage MapSearch(JsonElement response, string providerId, int requestedPage)
    {
        RequireObject(response);
        if (!response.TryGetProperty("tracks", out var tracks) || tracks.ValueKind != JsonValueKind.Array)
            throw new GatewayException(GatewayFailureKind.Protocol, "search_shape", "音乐来源的搜索结果格式不正确");
        var mapped = new List<Track>();
        var identities = new HashSet<Guid>();
        foreach (var item in tracks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var trackId = String(item, "providerTrackId");
            if (string.IsNullOrWhiteSpace(trackId) || trackId.Length > 1024) continue;
            var identity = OnlineTrackIdentity.Create(providerId, trackId);
            if (!identities.Add(identity)) continue;
            mapped.Add(new Track
            {
                Id = identity, SourceKind = TrackSourceKind.Online, ProviderId = providerId, ProviderTrackId = trackId,
                Title = String(item, "title") ?? "未命名歌曲", Artist = String(item, "artist") ?? "未知歌手",
                Album = String(item, "album") ?? "", Duration = Seconds(item, "durationSeconds") ?? TimeSpan.Zero,
                CoverUrl = IsHttpUrl(String(item, "coverUrl")) ? String(item, "coverUrl") : null,
                VersionLabel = String(item, "versionLabel") ?? "", Availability = ParseAvailability(String(item, "availability")),
                RestrictionReason = String(item, "restrictionReason"), ProviderMetadataJson = NormalizeMetadata(String(item, "providerMetadataJson"))
            });
        }
        if (tracks.GetArrayLength() > 0 && mapped.Count == 0)
            throw new GatewayException(GatewayFailureKind.Protocol, "search_items", "音乐来源未返回有效曲目标识");
        var page = response.TryGetProperty("page", out var pageValue) && pageValue.ValueKind == JsonValueKind.Number &&
            pageValue.TryGetInt32(out var parsed) && parsed > 0 ? parsed : requestedPage;
        return new MusicSearchPage(providerId, page, mapped.AsReadOnly(),
            response.TryGetProperty("hasMore", out var hasMore) && hasMore.ValueKind == JsonValueKind.True, String(response, "warning"));
    }

    private static void RequireObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new GatewayException(GatewayFailureKind.Protocol, "response_shape", "在线音乐响应格式不正确");
        if (value.TryGetProperty("error", out var error))
        {
            var code = error.ValueKind == JsonValueKind.Object ? String(error, "code") : String(value, "code");
            // The runtime normally handles this; fake/alternate runtimes still must not turn failures into empty success.
            throw new GatewayException(GatewayFailureKind.Provider, code ?? "provider_failed", "该音乐来源暂时不可用，请稍后重试");
        }
    }

    private static MusicAvailability ParseAvailability(string? value) => value?.ToLowerInvariant() switch
    { "full" => MusicAvailability.Full, "preview" => MusicAvailability.Preview, "unavailable" => MusicAvailability.Unavailable, _ => MusicAvailability.Unknown };

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static TimeSpan? Seconds(JsonElement element, string property, bool allowZero = false)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var seconds) ||
            !double.IsFinite(seconds) || seconds < 0 || !allowZero && seconds == 0 || seconds > 7 * 24 * 60 * 60) return null;
        return TimeSpan.FromSeconds(seconds);
    }

    private static bool IsHttpUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo);

    private static JsonElement ParseMetadata(string? metadata)
    {
        using var document = JsonDocument.Parse(NormalizeMetadata(metadata));
        return document.RootElement.Clone();
    }

    private static string NormalizeMetadata(string? metadata)
    {
        if (string.IsNullOrWhiteSpace(metadata) || metadata.Length > 65536) return "{}";
        try
        {
            var root = JsonNode.Parse(metadata);
            if (root is not JsonObject obj) return "{}";
            RemoveTransientFields(obj);
            return obj.ToJsonString();
        }
        catch (JsonException) { return "{}"; }
    }

    private static void RemoveTransientFields(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                var name = pair.Key.ToLowerInvariant();
                if (name is "url" or "playurl" or "audiourl" or "streamurl" or "headers" or "expiresat" or "token" or "cookie" or "authorization")
                    obj.Remove(pair.Key);
                else if (pair.Value is not null) RemoveTransientFields(pair.Value);
            }
        }
        else if (node is JsonArray array)
            foreach (var item in array) if (item is not null) RemoveTransientFields(item);
    }
}
