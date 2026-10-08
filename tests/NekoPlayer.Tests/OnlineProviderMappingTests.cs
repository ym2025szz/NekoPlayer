using System.Collections.Concurrent;
using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class OnlineProviderMappingTests
{
    [Fact]
    public async Task SearchMapsStableIdentityAndUnknownWithoutAssumingPlaybackRights()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Json("""
            {"page":2,"hasMore":true,"warning":"受限","tracks":[
            {"providerTrackId":"123","title":"歌曲","artist":"歌手","album":"专辑","durationSeconds":180,
            "coverUrl":"https://example.test/cover.jpg","versionLabel":"Live","availability":"unexpected",
            "providerMetadataJson":"{\"hash\":\"x\",\"url\":\"https://secret.test/audio\",\"headers\":{\"Cookie\":\"secret\"}}"}]}
            """)));
        var service = new OnlineMusicService(runtime);
        var result = await service.SearchAsync(new MusicSearchRequest(" song ", "netease", 2));
        var track = Assert.Single(result.Tracks);
        Assert.Equal(OnlineTrackIdentity.Create("netease", "123"), track.Id);
        Assert.Equal(TrackSourceKind.Online, track.SourceKind);
        Assert.Equal(MusicAvailability.Unknown, track.Availability);
        Assert.Equal(TimeSpan.FromSeconds(180), track.Duration);
        Assert.Equal("Live", track.VersionLabel);
        Assert.Equal("{\"hash\":\"x\"}", track.ProviderMetadataJson);
        Assert.True(result.HasMore);
        Assert.Equal(2, result.Page);
        Assert.Equal("受限", result.Warning);
        Assert.Empty(track.FilePath);
    }

    [Fact]
    public async Task SearchCacheReturnsIndependentTracksAndNeverCachesResolvedUrls()
    {
        var runtime = new FakeRuntime((route, _) => Task.FromResult(route == "/v1/search"
            ? SearchJson() : Json("""{"availability":"full","url":"https://example.test/audio","durationSeconds":180,"canSeek":true}""")));
        var service = new OnlineMusicService(runtime);
        var first = await service.SearchAsync(new MusicSearchRequest("song", "qq"));
        first.Tracks[0].Title = "changed";
        first.Tracks[0].IsFavorite = true;
        var second = await service.SearchAsync(new MusicSearchRequest("song", "qq"));
        Assert.Equal("Song", second.Tracks[0].Title);
        Assert.False(second.Tracks[0].IsFavorite);
        Assert.Equal(1, runtime.Calls.Count(call => call.Route == "/v1/search"));
        await service.ResolveAsync(second.Tracks[0]);
        await service.ResolveAsync(second.Tracks[0]);
        Assert.Equal(2, runtime.Calls.Count(call => call.Route == "/v1/resolve"));
        Assert.Equal("{}", second.Tracks[0].ProviderMetadataJson);
    }

    [Fact]
    public async Task CancelledSearchWaiterDoesNotCancelEqualNewSearch()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var complete = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtime = new FakeRuntime((_, token) => { Assert.False(token.IsCancellationRequested); started.TrySetResult(); return complete.Task; });
        var service = new OnlineMusicService(runtime);
        using var oldCancellation = new CancellationTokenSource();
        var old = service.SearchAsync(new MusicSearchRequest("song", "qq"), oldCancellation.Token);
        await started.Task;
        var newer = service.SearchAsync(new MusicSearchRequest("song", "qq"));
        oldCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
        complete.TrySetResult(SearchJson());
        Assert.Single((await newer).Tracks);
        Assert.Single(runtime.Calls);
    }

    [Fact]
    public async Task AbandonedSearchReleasesProviderAndEqualLaterRequestStartsFresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempt = 0;
        var runtime = new FakeRuntime(async (_, token) =>
        {
            if (Interlocked.Increment(ref attempt) == 1)
            {
                started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return SearchJson();
        });
        var service = new OnlineMusicService(runtime);
        using var cancellation = new CancellationTokenSource();
        var abandoned = service.SearchAsync(new("song", "qq"), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => abandoned);
        var later = await service.SearchAsync(new("song", "qq")).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Single(later.Tracks);
        Assert.Equal(2, runtime.Calls.Count);
    }

    [Fact]
    public async Task ProviderErrorAndMalformedSearchAreNotCachedAsEmptySuccess()
    {
        var count = 0;
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Interlocked.Increment(ref count) switch
        { 1 => Json("""{"error":{"code":"provider_timeout","message":"failure"}}"""), 2 => Json("{}"), _ => SearchJson() }));
        var service = new OnlineMusicService(runtime);
        Assert.Equal(GatewayFailureKind.Provider, (await Assert.ThrowsAsync<GatewayException>(() => service.SearchAsync(new("song", "qq")))).Kind);
        Assert.Equal(GatewayFailureKind.Protocol, (await Assert.ThrowsAsync<GatewayException>(() => service.SearchAsync(new("song", "qq")))).Kind);
        Assert.Single((await service.SearchAsync(new("song", "qq"))).Tracks);
        Assert.Equal(3, runtime.Calls.Count);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    [InlineData("future")]
    public async Task ResolveNeverTreatsAnUnconfirmedUrlAsFull(string availability)
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
        { availability, url = "https://example.test/audio", durationSeconds = 180, canSeek = true })));
        var result = await new OnlineMusicService(runtime).ResolveAsync(OnlineTrack());
        Assert.Null(result.Source);
        Assert.NotEqual(MusicAvailability.Full, result.Availability);
    }

    [Fact]
    public async Task PreviewUsesActualPlayableDurationAndParsedMetadata()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Json("""
            {"availability":"preview","url":"https://example.test/preview","durationSeconds":180,
            "previewDurationSeconds":30,"previewStartSeconds":60,"canSeek":false,"reason":"仅试听"}
            """)));
        var track = OnlineTrack();
        track.ProviderMetadataJson = "{\"hash\":\"track-hash\"}";
        var result = await new OnlineMusicService(runtime).ResolveAsync(track);
        Assert.Equal(MusicAvailability.Preview, result.Availability);
        Assert.True(result.Source!.IsPreview);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Source.Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), result.Source.PreviewStart);
        Assert.False(result.Source.CanSeek);
        Assert.Equal("track-hash", runtime.Calls.Single().Payload.GetProperty("metadata").GetProperty("hash").GetString());
    }

    [Fact]
    public async Task MissingPreviewDurationAndExpiredUrlsAreUnavailable()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Json("""{"availability":"preview","url":"https://example.test/preview"}""")));
        Assert.Null((await new OnlineMusicService(runtime).ResolveAsync(OnlineTrack())).Source);
        var expired = new FakeRuntime((_, _) => Task.FromResult(Json("""
            {"availability":"full","url":"https://example.test/audio","expiresAt":"2000-01-01T00:00:00Z","durationSeconds":180}
            """)));
        Assert.Null((await new OnlineMusicService(expired).ResolveAsync(OnlineTrack())).Source);
    }

    [Fact]
    public async Task LyricsWireIsParsedAndInvalidMetadataBecomesObject()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(Json("""{"lrc":"[00:01.50]Hello\n[00:02]World"}""")));
        var track = OnlineTrack();
        track.ProviderMetadataJson = "[not-an-object]";
        var lines = await new OnlineMusicService(runtime).GetLyricsAsync(track);
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), lines[0].Timestamp);
        Assert.Equal("{}", runtime.Calls.Single().Payload.GetProperty("metadata").GetRawText());
    }

    [Fact]
    public async Task RequestsForOneProviderAreSerialized()
    {
        var active = 0;
        var peak = 0;
        var runtime = new FakeRuntime(async (_, _) =>
        {
            var current = Interlocked.Increment(ref active);
            peak = Math.Max(peak, current);
            await Task.Delay(10);
            Interlocked.Decrement(ref active);
            return SearchJson();
        });
        var service = new OnlineMusicService(runtime);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => service.SearchAsync(new("song" + i, "qq"))));
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task CacheEvictsBeyondOneHundredKeys()
    {
        var runtime = new FakeRuntime((_, _) => Task.FromResult(SearchJson()));
        var service = new OnlineMusicService(runtime);
        for (var i = 0; i <= 100; i++) await service.SearchAsync(new("song" + i, "qq"));
        await service.SearchAsync(new("song0", "qq"));
        Assert.Equal(102, runtime.Calls.Count);
    }

    private static Track OnlineTrack() => new() { SourceKind = TrackSourceKind.Online, ProviderId = "qq", ProviderTrackId = "1", Duration = TimeSpan.FromMinutes(3) };
    private static JsonElement SearchJson() => Json("""{"tracks":[{"providerTrackId":"1","title":"Song","availability":"unknown"}],"page":1,"hasMore":false}""");
    private static JsonElement Json(string value) { using var document = JsonDocument.Parse(value); return document.RootElement.Clone(); }

    private sealed class FakeRuntime(Func<string, CancellationToken, Task<JsonElement>> send) : IGatewayRuntime
    {
        public ConcurrentQueue<(string Route, JsonElement Payload)> Calls { get; } = new();
        public GatewayState State => GatewayState.Ready;
        public string? LastError => null;
        public Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(new Uri("http://127.0.0.1:1234"));
        public Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
        { Calls.Enqueue((route, JsonSerializer.SerializeToElement(payload))); return send(route, cancellationToken); }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
