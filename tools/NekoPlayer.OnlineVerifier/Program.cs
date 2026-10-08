using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;
using NekoPlayer.OnlineVerifier;

Console.OutputEncoding = Encoding.UTF8;
if (args.Contains("--help", StringComparer.Ordinal) || !args.Contains("--online", StringComparer.Ordinal))
{
    Console.WriteLine("Read-only, guest online verification; never opens an audio device or reads an account cookie.");
    Console.WriteLine("Usage: dotnet run --project tools/NekoPlayer.OnlineVerifier -- --online [--query 周杰伦] [--providers netease,qq,kuwo,kugou,qishui] [--no-media]");
    Console.WriteLine("Options: --timeout-seconds 25 --source-timeout-seconds 110 --ffmpeg-directory <directory>");
    Console.WriteLine("Optional known stable IDs: --track-ids netease=1357374736,qq=000C9FCy4HUcTW,kuwo=19528080");
    Console.WriteLine("Without --online no gateway is started and no network calls are made. Normal dotnet test stays offline.");
    return args.Contains("--help", StringComparer.Ordinal) ? 0 : 2;
}

VerifierOptions options;
try { options = VerifierOptions.Parse(args); }
catch (ArgumentException)
{
    Console.Error.WriteLine("Invalid verifier arguments. Run with --help; supplied values are not logged.");
    return 2;
}

var root = FindProjectRoot(AppContext.BaseDirectory);
var reportsDirectory = Path.Combine(root, "artifacts", "test-reports");
var report = new OnlineVerificationReport { QueryLength = options.Query.Length };
using var stop = new CancellationTokenSource();
ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; stop.Cancel(); };
Console.CancelKeyPress += interrupt;
using var paths = new VerifierPaths();
paths.EnsureCreated();
var runtime = new GatewayRuntime(paths);
var audited = new AuditedGatewayRuntime(runtime);
var service = new OnlineMusicService(audited);
var exitCode = 1;

try
{
    Console.WriteLine("Running five-source guest capability checks. Media output is bounded PCM in a pipe, never speakers.");
    await RunCapabilityAsync(report.Runtime, audited, options.CapabilityTimeoutSeconds, stop.Token,
        async token =>
        {
            await runtime.EnsureReadyAsync(token);
            report.Runtime.Business = "ready";
            report.Runtime.Http = "health-success-status-not-exposed";
        });
    if (report.Runtime.Status == "passed")
    {
        var results = await Task.WhenAll(options.Providers.Select(providerId => VerifyProviderAsync(providerId,
            options, service, audited, root, stop.Token)));
        report.Providers.AddRange(results);
    }
    else
    {
        foreach (var providerId in options.Providers)
        {
            var provider = new ProviderResult(providerId);
            foreach (var capability in new[] { "search", "lyrics", "resolve" })
                provider.Capabilities.Add(Skipped(capability, "runtime-unavailable"));
            provider.Media.Status = "skipped";
            provider.Media.ErrorCategory = "runtime-unavailable";
            report.Providers.Add(provider);
        }
    }

    // Zero means every requested service capability passed and every requested decode passed.
    // Partial access, empty results, missing runtime or media failures remain reviewable nonzero results.
    exitCode = report.Runtime.Status == "passed" && report.Providers.All(provider =>
        provider.Capabilities.All(x => x.Status == "passed") &&
        provider.CandidateResolutions.All(candidate => candidate.Resolution.Status == "passed") &&
        (!options.Media || provider.Media.Status is "passed" or "passed-without-seek")) ? 0 : 1;
}
catch (Exception error)
{
    report.Runtime.Status = "failed";
    report.Runtime.ErrorCategory = SafeReportValues.Category(error);
    exitCode = 1;
}
finally
{
    var timer = Stopwatch.StartNew();
    try
    {
        await runtime.StopAsync().WaitAsync(TimeSpan.FromSeconds(15));
        await runtime.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
        report.Shutdown.Status = runtime.State == GatewayState.Stopped ? "passed" : "failed";
        report.Shutdown.Business = runtime.State == GatewayState.Stopped ? "stopped" : "not-stopped";
        if (report.Shutdown.Status == "failed") exitCode = 1;
    }
    catch (Exception error)
    {
        report.Shutdown.Status = "failed";
        report.Shutdown.ErrorCategory = SafeReportValues.Category(error);
        exitCode = 1;
    }
    report.Shutdown.ElapsedMilliseconds = timer.ElapsedMilliseconds;
    report.FinishedAtUtc = DateTimeOffset.UtcNow;
    Directory.CreateDirectory(reportsDirectory);
    await File.WriteAllTextAsync(Path.Combine(reportsDirectory, "online-verification.json"),
        JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
    await File.WriteAllTextAsync(Path.Combine(reportsDirectory, "online-verification.md"), ReportMarkdown.Render(report), Encoding.UTF8);
    Console.CancelKeyPress -= interrupt;
}
Console.WriteLine($"Verification finished: {report.Providers.Count} providers; exit {exitCode}. Reports: artifacts/test-reports/online-verification.json and .md");
return exitCode;

static async Task<ProviderResult> VerifyProviderAsync(string providerId, VerifierOptions options,
    OnlineMusicService service, AuditedGatewayRuntime audited, string root, CancellationToken cancellationToken)
{
    var provider = new ProviderResult(providerId)
    {
        Experimental = service.Providers.First(info => info.Id == providerId).Experimental
    };
    using var sourceTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    sourceTimeout.CancelAfter(TimeSpan.FromSeconds(options.SourceTimeoutSeconds));
    var sourceToken = sourceTimeout.Token;
    Track? selected = null;
    IReadOnlyList<Track> searchTracks = [];
    var search = new CapabilityResult("search");
    provider.Capabilities.Add(search);
    await RunCapabilityAsync(search, audited, options.CapabilityTimeoutSeconds, sourceToken, async token =>
    {
        var page = await service.SearchAsync(new MusicSearchRequest(options.Query, providerId, 1, 5), token);
        searchTracks = page.Tracks;
        search.ResultCount = page.Tracks.Count;
        search.Business = page.Tracks.Count == 0 ? "empty-result" : "non-empty-result";
        search.NonEmptyFields["stableIds"] = page.Tracks.Count > 0 && page.Tracks.All(track =>
            !string.IsNullOrWhiteSpace(track.ProviderId) && !string.IsNullOrWhiteSpace(track.ProviderTrackId));
        search.NonEmptyFields["titles"] = page.Tracks.Count > 0 && page.Tracks.All(track => !string.IsNullOrWhiteSpace(track.Title));
        search.NonEmptyFields["artists"] = page.Tracks.Count > 0 && page.Tracks.All(track => !string.IsNullOrWhiteSpace(track.Artist));
        search.NonEmptyFields["albums"] = page.Tracks.Count > 0 && page.Tracks.All(track => !string.IsNullOrWhiteSpace(track.Album));
        search.NonEmptyFields["coverMetadata"] = page.Tracks.Any(track => !string.IsNullOrWhiteSpace(track.CoverUrl));
        selected = page.Tracks.FirstOrDefault(track => track.ProviderId == providerId && !string.IsNullOrWhiteSpace(track.ProviderTrackId));
        provider.ProviderTrackId = SafeReportValues.TrackId(selected?.ProviderTrackId);
        if (page.Tracks.Count == 0) { search.Status = "empty"; search.ErrorCategory = "no-search-results"; }
        else if (!search.NonEmptyFields["stableIds"] || !search.NonEmptyFields["titles"] || selected is null)
        { search.Status = "partial"; search.ErrorCategory = "incomplete-search-metadata"; }
    });
    Console.WriteLine($"{providerId}: search {search.Status} ({search.ElapsedMilliseconds} ms)");

    if (selected is null && !options.ExplicitTrackIds.ContainsKey(providerId))
    {
        provider.Capabilities.Add(Skipped("lyrics", "no-search-track"));
        provider.Capabilities.Add(Skipped("resolve", "no-search-track"));
        provider.Media.Status = "skipped";
        provider.Media.ErrorCategory = "no-search-track";
        return provider;
    }

    var lyricsTrack = selected ?? ExplicitTrack(providerId, options.ExplicitTrackIds[providerId]);
    provider.ProviderTrackId ??= SafeReportValues.TrackId(lyricsTrack.ProviderTrackId);
    var lyrics = new CapabilityResult("lyrics");
    provider.Capabilities.Add(lyrics);
    await RunCapabilityAsync(lyrics, audited, options.CapabilityTimeoutSeconds, sourceToken, async token =>
    {
        var lines = await service.GetLyricsAsync(lyricsTrack, token);
        lyrics.ResultCount = lines.Count;
        lyrics.NonEmptyFields["lyricText"] = lines.Any(line => !string.IsNullOrWhiteSpace(line.Text));
        lyrics.Business = lines.Count == 0 ? "empty-lyrics" : "parsed-lyrics";
        if (!lyrics.NonEmptyFields["lyricText"]) { lyrics.Status = "empty"; lyrics.ErrorCategory = "no-lyric-lines"; }
    });
    Console.WriteLine($"{providerId}: lyrics {lyrics.Status} ({lyrics.ElapsedMilliseconds} ms)");

    var candidates = searchTracks.Where(track => track.ProviderId == providerId && !string.IsNullOrWhiteSpace(track.ProviderTrackId))
        .Take(5).Select(track => (Track: track, Origin: "search-result")).ToList();
    if (options.ExplicitTrackIds.TryGetValue(providerId, out var explicitId) &&
        !candidates.Any(candidate => candidate.Track.ProviderTrackId == explicitId))
        candidates.Add((ExplicitTrack(providerId, explicitId), "explicit-id"));

    // Keep all first-screen permission evidence, including the first result's restriction.
    // Audio sources stay only in this method and never enter the report object graph.
    var resolvedCandidates = new List<(Track Track, string Origin, PlaybackResolution Resolution)>();
    foreach (var candidate in candidates)
    {
        var evidence = new CandidateResolutionResult(SafeReportValues.TrackId(candidate.Track.ProviderTrackId), candidate.Origin);
        provider.CandidateResolutions.Add(evidence);
        if (provider.Capabilities.All(capability => capability.Capability != "resolve"))
            provider.Capabilities.Add(evidence.Resolution);
        await RunCapabilityAsync(evidence.Resolution, audited, options.CapabilityTimeoutSeconds, sourceToken, async token =>
        {
            var resolution = await service.ResolveAsync(candidate.Track, token);
            RecordResolution(evidence.Resolution, resolution);
            resolvedCandidates.Add((candidate.Track, candidate.Origin, resolution));
        });
        Console.WriteLine($"{providerId}: candidate resolve {evidence.Resolution.Status} ({evidence.Resolution.ElapsedMilliseconds} ms)");
    }
    var mediaCandidate = resolvedCandidates.Where(candidate => candidate.Resolution.Source is not null)
        .OrderBy(candidate => candidate.Resolution.Availability == MusicAvailability.Full ? 0 : 1)
        .ThenBy(candidate => options.ExplicitTrackIds.TryGetValue(providerId, out var preferred) &&
            candidate.Track.ProviderTrackId == preferred ? 0 : 1).FirstOrDefault();
    provider.SelectedMediaTrackId = SafeReportValues.TrackId(mediaCandidate.Track?.ProviderTrackId);
    provider.MediaSelectionOrigin = mediaCandidate.Track is null ? null : mediaCandidate.Origin;

    if (!options.Media)
    {
        provider.Media.Status = "skipped";
        provider.Media.ErrorCategory = "media-check-disabled";
    }
    else if (mediaCandidate.Resolution?.Source is null)
    {
        provider.Media.Status = "skipped";
        provider.Media.ErrorCategory = "no-permitted-session-source";
    }
    else
    {
        var ffmpegDirectory = options.FfmpegDirectory ?? Path.Combine(root, "tools", "ffmpeg");
        provider.Media = await SilentMediaVerifier.VerifyAsync(mediaCandidate.Resolution.Source, ffmpegDirectory,
            options.CapabilityTimeoutSeconds, sourceToken);
        Console.WriteLine($"{providerId}: silent media {provider.Media.Status} ({provider.Media.ElapsedMilliseconds} ms)");
    }
    return provider;
}

static Track ExplicitTrack(string providerId, string trackId) => new()
{
    Id = OnlineTrackIdentity.Create(providerId, trackId), SourceKind = TrackSourceKind.Online,
    ProviderId = providerId, ProviderTrackId = trackId, ProviderMetadataJson = "{}"
};

static void RecordResolution(CapabilityResult resolve, PlaybackResolution resolution)
{
    resolve.ProviderDeclaredAvailability = resolution.Availability.ToString();
    resolve.HasSessionSource = resolution.Source is not null && !string.IsNullOrWhiteSpace(resolution.Source.Input);
    resolve.CanSeekDeclared = resolution.Source?.CanSeek;
    resolve.NonEmptyFields["sessionSource"] = resolve.HasSessionSource == true;
    resolve.Business = resolution.Availability switch
    {
        MusicAvailability.Full => "provider-declared-full",
        MusicAvailability.Preview => "provider-declared-preview",
        MusicAvailability.Unavailable => "provider-declared-unavailable",
        _ => "unknown-permission"
    };
    if (resolution.Availability is MusicAvailability.Unavailable or MusicAvailability.Unknown || resolve.HasSessionSource != true)
    { resolve.Status = "restricted"; resolve.ErrorCategory = "no-permitted-session-source"; }
    else if (resolution.Availability == MusicAvailability.Preview)
    { resolve.Status = "limited"; resolve.ErrorCategory = "preview-only"; }
}

static async Task RunCapabilityAsync(CapabilityResult result, AuditedGatewayRuntime runtime, int timeoutSeconds,
    CancellationToken cancellationToken, Func<CancellationToken, Task> operation)
{
    var timer = Stopwatch.StartNew();
    result.Status = "running";
    using var observation = runtime.Observe(result);
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
    try
    {
        await operation(timeout.Token).WaitAsync(timeout.Token);
        if (result.Status == "running") result.Status = "passed";
    }
    catch (Exception error)
    {
        result.Status = "failed";
        result.Business = "error";
        result.ErrorCategory = error is OperationCanceledException
            ? (cancellationToken.IsCancellationRequested ? "source-cancelled-or-timeout" : "capability-timeout")
            : SafeReportValues.Category(error);
    }
    finally { result.ElapsedMilliseconds = timer.ElapsedMilliseconds; }
}

static CapabilityResult Skipped(string capability, string reason) => new(capability) { Status = "skipped", ErrorCategory = reason };

static string FindProjectRoot(string startingDirectory)
{
    for (var directory = new DirectoryInfo(startingDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "NekoPlayer.sln"))) return directory.FullName;
    throw new DirectoryNotFoundException("Project root could not be located.");
}
