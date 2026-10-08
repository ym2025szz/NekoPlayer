using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Online;
using NekoPlayer.Infrastructure.Repositories;

namespace NekoPlayer.Tests;

/// <summary>Offline contract integration: actual mapper/catalog/coordinator, explicit in-memory gateway and silent audio doubles.</summary>
public sealed class OnlineIntegrationContractTests
{
    [Fact]
    public async Task SearchMappingThroughCatalogDeduplicatesOnlyWithinTheSameProvider()
    {
        await using var database = await ContractDatabase.CreateAsync();
        var runtime = new FixtureGateway((route, payload) =>
        {
            Assert.Equal("/v1/search", route);
            return SearchFixture(payload.GetProperty("providerId").GetString()!, duplicate: true);
        });
        var service = new OnlineMusicService(runtime);

        var first = await service.SearchAsync(new("same artist", "netease"));
        var second = await service.SearchAsync(new("same artist", "qq"));
        var netease = Assert.Single(first.Tracks);
        var qq = Assert.Single(second.Tracks);
        var saved = await database.Catalog.EnsureOnlineTracksAsync([netease, netease, qq]);

        Assert.Equal(saved[0].Id, saved[1].Id);
        Assert.NotEqual(saved[0].Id, saved[2].Id);
        Assert.Equal(OnlineTrackIdentity.Create("netease", "same-id"), saved[0].Id);
        Assert.Equal(OnlineTrackIdentity.Create("qq", "same-id"), saved[2].Id);
        await using var db = database.Factory.CreateDbContext();
        Assert.Equal(2, await db.Tracks.CountAsync());
        Assert.All(await db.Tracks.ToListAsync(), track => Assert.Equal(string.Empty, track.FilePath));
    }

    [Fact]
    public async Task ResolvedSessionUrlsAndCredentialsNeverEnterMappedOrPersistedTrackJson()
    {
        const string secret = "transient-sentinel-credential";
        await using var database = await ContractDatabase.CreateAsync();
        var runtime = new FixtureGateway((route, _) => route switch
        {
            "/v1/search" => SearchFixture("netease", metadata: JsonSerializer.Serialize(new
            {
                albumId = "stable-album", url = "https://invalid.example/audio?token=" + secret,
                token = secret, cookie = secret, headers = new { Authorization = secret },
                nested = new[] { new { songId = "stable-song", playUrl = "https://invalid.example/" + secret } }
            })),
            "/v1/resolve" => ResolutionFixture("full", "https://invalid.example/audio?signature=" + secret,
                headers: new Dictionary<string, string> { ["Cookie"] = secret }),
            _ => throw new InvalidOperationException("Unexpected offline fixture route.")
        });
        var service = new OnlineMusicService(runtime);
        var track = Assert.Single((await service.SearchAsync(new("fixture", "netease"))).Tracks);
        var session = await service.ResolveAsync(track);
        var saved = await database.Catalog.EnsureOnlineTrackAsync(track);

        Assert.Equal(MusicAvailability.Full, session.Availability);
        Assert.Contains(secret, session.Source!.Input);
        Assert.Equal(secret, session.Source.Headers!["Cookie"]);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(track));
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(saved));
        await using var db = database.Factory.CreateDbContext();
        var persisted = await db.Tracks.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(persisted));
        using var metadata = JsonDocument.Parse(persisted.ProviderMetadataJson);
        Assert.Equal("stable-album", metadata.RootElement.GetProperty("albumId").GetString());
        Assert.Equal(string.Empty, persisted.FilePath);

        // A second search comes from the real service cache, whose snapshot must also be metadata-only.
        var cached = Assert.Single((await service.SearchAsync(new("fixture", "netease"))).Tracks);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(cached));
        Assert.Single(runtime.Calls.Where(call => call.Route == "/v1/search"));
    }

    [Fact]
    public async Task ProviderFailureIsNotAnEmptyResultAndDoesNotPoisonAnotherProviderOrTheCache()
    {
        var failures = 0;
        var runtime = new FixtureGateway((route, payload) =>
        {
            Assert.Equal("/v1/search", route);
            if (payload.GetProperty("providerId").GetString() == "netease" && Interlocked.Increment(ref failures) == 1)
                return JsonSerializer.SerializeToElement(new { error = new { code = "provider_denied", message = "Fixture source refused request." } });
            return JsonSerializer.SerializeToElement(new { page = 1, tracks = Array.Empty<object>(), hasMore = false });
        });
        var service = new OnlineMusicService(runtime);

        var error = await Assert.ThrowsAsync<GatewayException>(() => service.SearchAsync(new("same query", "netease")));
        Assert.Equal(GatewayFailureKind.Provider, error.Kind);
        Assert.Equal("provider_denied", error.Code);
        var otherProvider = await service.SearchAsync(new("same query", "qq"));
        var retried = await service.SearchAsync(new("same query", "netease"));

        Assert.Empty(otherProvider.Tracks);
        Assert.Empty(retried.Tracks);
        Assert.Equal(3, runtime.Calls.Count);
    }

    [Fact]
    public async Task InvalidSearchShapeRaisesProtocolFailureInsteadOfReportingEmptySuccess()
    {
        var runtime = new FixtureGateway((_, _) => JsonSerializer.SerializeToElement(new { result = Array.Empty<object>() }));
        var service = new OnlineMusicService(runtime);

        var error = await Assert.ThrowsAsync<GatewayException>(() => service.SearchAsync(new("fixture", "kuwo")));

        Assert.Equal(GatewayFailureKind.Protocol, error.Kind);
        Assert.Equal("search_shape", error.Code);
    }

    [Fact]
    public async Task PermissionResolutionUsesFreshSessionSourceAndRetainsPreviewBoundaries()
    {
        var calls = 0;
        var runtime = new FixtureGateway((route, _) =>
        {
            Assert.Equal("/v1/resolve", route);
            return ResolutionFixture("preview", "https://invalid.example/session-" + Interlocked.Increment(ref calls));
        });
        var service = new OnlineMusicService(runtime);
        var track = OnlineTrack("qq");

        var first = await service.ResolveAsync(track);
        var second = await service.ResolveAsync(track);

        Assert.Equal(MusicAvailability.Preview, first.Availability);
        Assert.True(first.Source!.IsPreview);
        Assert.Equal(TimeSpan.FromSeconds(30), first.Source.Duration);
        Assert.Equal(TimeSpan.FromSeconds(45), first.Source.PreviewStart);
        Assert.False(first.Source.CanSeek);
        Assert.NotEqual(first.Source.Input, second.Source!.Input);
        Assert.Equal(2, calls);
        Assert.Equal(string.Empty, track.FilePath);
        Assert.Equal(MusicAvailability.Unknown, track.Availability);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("unavailable")]
    public async Task UnconfirmedPermissionDoesNotBecomePlayableEvenWhenWireResponseContainsAUrl(string availability)
    {
        var runtime = new FixtureGateway((_, _) => ResolutionFixture(availability, "https://invalid.example/forbidden-audio"));
        var service = new OnlineMusicService(runtime);

        var result = await service.ResolveAsync(OnlineTrack("kugou"));

        Assert.Null(result.Source);
        Assert.NotEqual(MusicAvailability.Full, result.Availability);
    }

    [Fact]
    public async Task MissingNodeRuntimeDoesNotBlockTheLocalPlaybackPath()
    {
        using var paths = new ContractPaths();
        paths.EnsureCreated();
        var localPath = Path.Combine(paths.TempDirectory, "local-fixture.wav");
        // Existence-only fixture: the explicitly silent audio double never decodes or opens a device.
        await File.WriteAllBytesAsync(localPath, [0]);
        await using var runtime = new GatewayRuntime(paths, new GatewayRuntimeOptions
        {
            BaseDirectory = paths.Root,
            NodeExecutablePath = Path.Combine(paths.Root, "deliberately-missing-node.exe"),
            GatewayScriptPath = Path.Combine(paths.Root, "deliberately-missing-gateway.mjs"),
            StartupTimeout = TimeSpan.FromMilliseconds(100)
        });
        var unavailable = await Assert.ThrowsAsync<GatewayException>(() => runtime.EnsureReadyAsync());
        Assert.Equal(GatewayFailureKind.Unavailable, unavailable.Kind);
        Assert.Equal(GatewayState.Unavailable, runtime.State);
        await using var audio = new SilentContractAudio();
        var local = new Track { FilePath = localPath, Title = "Local fixture", Duration = TimeSpan.FromSeconds(60) };
        await using var coordinator = new PlaybackCoordinator(audio, new PlaybackQueueService(),
            new OnlineMusicService(runtime), new EmptyLocalLyrics(), new RejectOnlineCatalog());

        var result = await coordinator.PlayAsync(local, [local]);

        Assert.True(result.Started, result.Message);
        Assert.Equal(localPath, audio.LoadedSource!.Input);
        Assert.False(audio.LoadedSource.IsRemote);
        Assert.Equal(PlaybackState.Playing, coordinator.Snapshot.State);
        Assert.Equal(GatewayState.Unavailable, runtime.State);
        Assert.Equal(1, audio.PlayCalls);
    }

    private static JsonElement SearchFixture(string providerId, bool duplicate = false, string metadata = "{}")
    {
        var track = new
        {
            providerTrackId = "same-id", title = "Fixture title", artist = "Fixture artist", album = "Fixture album",
            durationSeconds = 180, coverUrl = "https://invalid.example/stable-cover.jpg", versionLabel = "Original",
            availability = "unknown", providerMetadataJson = metadata
        };
        return JsonSerializer.SerializeToElement(new { providerId, page = 1, tracks = duplicate ? new[] { track, track } : new[] { track }, hasMore = false });
    }

    private static JsonElement ResolutionFixture(string availability, string url, Dictionary<string, string>? headers = null) =>
        JsonSerializer.SerializeToElement(new
        {
            availability, url, headers = headers ?? new Dictionary<string, string>(), durationSeconds = 180,
            previewDurationSeconds = 30, previewStartSeconds = 45, canSeek = availability == "full",
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
        });

    private static Track OnlineTrack(string providerId) => new()
    {
        Id = OnlineTrackIdentity.Create(providerId, "same-id"), SourceKind = TrackSourceKind.Online,
        ProviderId = providerId, ProviderTrackId = "same-id", Title = "Fixture title", ProviderMetadataJson = "{}"
    };

    private sealed class FixtureGateway(Func<string, JsonElement, JsonElement> respond) : IGatewayRuntime
    {
        public ConcurrentQueue<(string Route, JsonElement Payload)> Calls { get; } = new();
        public GatewayState State => GatewayState.Ready;
        public string? LastError => null;
        public Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new Uri("http://127.0.0.1:1/"));
        public Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wirePayload = JsonSerializer.SerializeToElement(payload);
            Calls.Enqueue((route, wirePayload));
            return Task.FromResult(respond(route, wirePayload));
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ContractDatabase(SqliteConnection connection, ContractDbFactory factory) : IAsyncDisposable
    {
        public ContractDbFactory Factory { get; } = factory;
        public OnlineTrackCatalog Catalog { get; } = new(factory);
        public static async Task<ContractDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:;Pooling=False");
            await connection.OpenAsync();
            var factory = new ContractDbFactory(new DbContextOptionsBuilder<NekoPlayerDbContext>().UseSqlite(connection).Options);
            await using var db = factory.CreateDbContext();
            await DatabaseSchemaUpgrade.InitializeAsync(db);
            return new(connection, factory);
        }
        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }

    private sealed class ContractDbFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
    {
        public NekoPlayerDbContext CreateDbContext() => new(options);
    }

    private sealed class RejectOnlineCatalog : ITrackCatalog
    {
        public Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Local playback must not request the online catalog.");
        public Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Local playback must not request the online catalog.");
        public Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Local playback must not request the online catalog.");
    }

    private sealed class EmptyLocalLyrics : ILyricsService
    {
        public IReadOnlyList<LyricsLine> Parse(string content) => [];
        public Task<IReadOnlyList<LyricsLine>> LoadForTrackAsync(Track track, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LyricsLine>>([]);
    }

    private sealed class SilentContractAudio : ISessionAudioPlayerService
    {
        public PlaybackState State { get; private set; }
        public TimeSpan Position { get; private set; }
        public TimeSpan Duration { get; private set; }
        public float Volume { get; set; }
        public bool IsMuted { get; set; } = true;
        public long SessionId { get; private set; }
        public bool CanSeek { get; private set; }
        public AudioSource? LoadedSource { get; private set; }
        public int PlayCalls { get; private set; }
        public Task LoadAsync(string filePath, TimeSpan? startPosition = null, CancellationToken cancellationToken = default) =>
            LoadAsync(AudioSource.Local(filePath), startPosition, cancellationToken);
        public Task LoadAsync(AudioSource source, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadedSource = source;
            SessionId = source.SessionId;
            CanSeek = source.CanSeek;
            Duration = source.Duration ?? TimeSpan.FromSeconds(60);
            Position = startPosition ?? TimeSpan.Zero;
            SetState(PlaybackState.Stopped);
            return Task.CompletedTask;
        }
        public Task PlayAsync(CancellationToken cancellationToken = default) { PlayCalls++; SetState(PlaybackState.Playing); return Task.CompletedTask; }
        public Task PauseAsync() { SetState(PlaybackState.Paused); return Task.CompletedTask; }
        public Task StopAsync() { SetState(PlaybackState.Stopped); return Task.CompletedTask; }
        public Task UnloadAsync() { LoadedSource = null; SetState(PlaybackState.Idle); return Task.CompletedTask; }
        public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default) { Position = position; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private void SetState(PlaybackState state)
        {
            State = state;
            SessionStateChanged?.Invoke(this, new(SessionId, state));
            StateChanged?.Invoke(this, state);
        }
        public event EventHandler<PlaybackState>? StateChanged;
        public event EventHandler<TimeSpan>? PositionChanged { add { } remove { } }
        public event EventHandler? PlaybackCompleted { add { } remove { } }
        public event EventHandler<Exception>? PlaybackFailed { add { } remove { } }
        public event EventHandler<AudioSessionState>? SessionStateChanged;
        public event EventHandler<AudioSessionPosition>? SessionPositionChanged { add { } remove { } }
        public event EventHandler<AudioSessionFailure>? SessionPlaybackFailed { add { } remove { } }
        public event EventHandler<AudioSessionCompletion>? SessionPlaybackCompleted { add { } remove { } }
    }

    private sealed class ContractPaths : IUserDataPaths, IDisposable
    {
        private readonly string _parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NekoPlayerOnlineContractTests"));
        public ContractPaths() { Root = Path.GetFullPath(Path.Combine(_parent, Guid.NewGuid().ToString("N"))); }
        public string Root { get; }
        public string DataDirectory => Path.Combine(Root, "Data");
        public string DatabasePath => Path.Combine(DataDirectory, "contract.db");
        public string LogsDirectory => Path.Combine(Root, "Logs");
        public string CoversDirectory => Path.Combine(Root, "Covers");
        public string LyricsDirectory => Path.Combine(Root, "Lyrics");
        public string ConfigDirectory => Path.Combine(Root, "Config");
        public string TempDirectory => Path.Combine(Root, "Temp");
        public string SettingsPath => Path.Combine(ConfigDirectory, "settings.json");
        public void EnsureCreated()
        {
            foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory })
                Directory.CreateDirectory(path);
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(_parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Test cleanup path left its temporary root.");
            if (Directory.Exists(resolved)) Directory.Delete(resolved, recursive: true);
        }
    }
}
