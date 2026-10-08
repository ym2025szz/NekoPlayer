using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FFMpegCore;
using NAudio.Wave;
using NekoPlayer.Audio.Playback;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using PlaybackState = NekoPlayer.Core.Enums.PlaybackState;

// A release verifier only: no profile, database, account, GUI, or audio-device access.
Console.OutputEncoding = Encoding.UTF8;
var root = FindRoot();
var iterations = args.Length == 0 ? 20 : args.Length == 2 && args[0] == "--iterations"
    ? int.Parse(args[1]) : throw new ArgumentException("Usage: --iterations N (N >= 20)");
if (iterations < 20) throw new ArgumentOutOfRangeException(nameof(iterations), "At least 20 EOF cycles are required.");
var directory = Path.Combine(root, "artifacts", "loop-verifier-v1.2.0");
Directory.CreateDirectory(directory);
var fixture = Path.Combine(directory, "generated-six-second-tone.wav");
var report = new Report { StartedAtUtc = DateTimeOffset.UtcNow, RequestedCycles = iterations };
var before = FfmpegIds();
var observed = new ConcurrentDictionary<int, byte>();
using var observationCancellation = new CancellationTokenSource();
var observer = Task.Run(async () =>
{
    while (!observationCancellation.IsCancellationRequested)
    {
        foreach (var pid in FfmpegIds().Except(before)) observed.TryAdd(pid, 0);
        try { await Task.Delay(10, observationCancellation.Token); }
        catch (OperationCanceledException) { }
    }
});
var exitCode = 1;
try
{
    var locator = new Locator(root, directory);
    report.FfmpegVersion = (await locator.ValidateAsync()).Version;
    await File.WriteAllBytesAsync(fixture, CreateWave(6.4));
    report.FixtureSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(fixture))).ToLowerInvariant();
    const long expectedPcmBytes = 307200L * 2 * sizeof(float);
    var completions = new ConcurrentQueue<Cycle>();
    var failures = new ConcurrentQueue<string>();
    var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    long previousPcm = 0;
    var maximumBuffer = 0;
    var boundViolation = false;
    await using (var audio = new FfmpegAudioPlayerService(locator, new NoSpectrum(), () => new SilentOutput()))
    {
        // Observe before the coordinator begins a new session from this completion.
        audio.SessionPlaybackCompleted += (_, value) =>
        {
            var bytes = audio.TotalPcmBytesReceived;
            completions.Enqueue(new Cycle(value.SessionId, bytes - previousPcm, audio.Position.TotalSeconds,
                audio.BufferedBytes, audio.IsDecodeActive));
            previousPcm = bytes;
            if (completions.Count >= iterations) done.TrySetResult();
        };
        audio.PlaybackFailed += (_, value) => { failures.Enqueue(value.Message); done.TrySetException(value); };
        var queue = new PlaybackQueueService { PlayMode = PlayMode.RepeatOne };
        var local = new Track { Title = "generated-A", FilePath = fixture, Duration = TimeSpan.FromSeconds(6.4) };
        var sentinel = new Track { Title = "must-never-play-B", FilePath = fixture, Duration = local.Duration };
        await using var coordinator = new PlaybackCoordinator(audio, queue, new OfflineOnline(), new NoLyrics(), new MemoryCatalog());
        var started = new ConcurrentQueue<Guid>();
        coordinator.PlaybackStarted += (_, value) => started.Enqueue(value.Track!.Id);
        var request = await coordinator.PlayAsync(local, [local, sentinel]);
        Require(request.Started, request.Message ?? "Initial local fixture did not start.");
        using var loopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(iterations * 5));
        var loopWatch = Stopwatch.StartNew();
        var nextProgress = TimeSpan.FromSeconds(5);
        while (!done.Task.IsCompleted)
        {
            if (loopTimeout.IsCancellationRequested) throw new TimeoutException($"Loop EOF timeout: completed={completions.Count}, state={audio.State}, coordinator={coordinator.Snapshot.State}, position={audio.Position.TotalSeconds:F3}, buffer={audio.BufferedBytes}/{audio.BufferCapacityBytes}, decoding={audio.IsDecodeActive}, pcm={audio.TotalPcmBytesReceived}.");
            maximumBuffer = Math.Max(maximumBuffer, audio.BufferedBytes);
            boundViolation |= audio.BufferedBytes > audio.BufferCapacityBytes;
            Require(coordinator.Snapshot.Track?.Id == local.Id && queue.Current?.Id == local.Id, "RepeatOne selected another queue item.");
            if (loopWatch.Elapsed >= nextProgress) { Console.WriteLine($"Progress: EOF={completions.Count}, state={audio.State}, coordinator={coordinator.Snapshot.State}, session={audio.SessionId}/{coordinator.Snapshot.SessionId}, message={coordinator.Snapshot.Message}, position={audio.Position.TotalSeconds:F3}, buffer={audio.BufferedBytes}, decoding={audio.IsDecodeActive}, pcm={audio.TotalPcmBytesReceived}"); nextProgress += TimeSpan.FromSeconds(5); }
            await Task.WhenAny(done.Task, Task.Delay(5));
        }
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Require(failures.IsEmpty && started.All(id => id == local.Id), "RepeatOne failed or started B before final Stop: " + string.Join("; ", failures));
        await coordinator.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var cycles = completions.Take(iterations).ToArray();
        Require(cycles.Length == iterations, "Insufficient real EOF cycles.");
        Require(cycles.Select(x => x.SessionId).Distinct().Count() == iterations, "Duplicate session completion.");
        Require(cycles.All(x => x.PcmBytes == expectedPcmBytes), "Fresh PCM length differs from the generated fixture.");
        Require(cycles.All(x => Math.Abs(x.EndPositionSeconds - 6.4) < 0.001 && x.BufferedBytes == 0 && !x.Decoding), "Completion occurred before PCM drain/decoder EOF.");
        Require(!boundViolation && maximumBuffer > 0, "PCM buffering did not remain bounded.");
        Require(failures.IsEmpty && started.All(id => id == local.Id), "RepeatOne failed or started B after final Stop: " + string.Join("; ", failures));
        report.Cycles = cycles;
        report.MaxBufferedBytes = maximumBuffer;
        report.BufferCapacityBytes = 48000 * 2 * sizeof(float) * 5;
        report.LocalRepeatOnePassed = true;
        Console.WriteLine($"PASS local RepeatOne: {cycles.Length} real FFmpeg EOF cycles, {expectedPcmBytes} fresh PCM bytes/cycle, max buffer {maximumBuffer}/{report.BufferCapacityBytes}, B never started.");
    }

    // The first online A completes. Its loop URL returns HTTP 403, then the one
    // allowed refreshed URL returns 403 too. B must remain untouched.
    await using (var server = new HttpFixture(await File.ReadAllBytesAsync(fixture)))
    await using (var audio = new FfmpegAudioPlayerService(locator, new NoSpectrum(), () => new SilentOutput()))
    {
        var online = new OnlineFixture(server.Url);
        var queue = new PlaybackQueueService { PlayMode = PlayMode.RepeatOne };
        var a = OnlineTrack("A"); var b = OnlineTrack("B");
        await using var coordinator = new PlaybackCoordinator(audio, queue, online, new NoLyrics(), new MemoryCatalog());
        var completed = 0;
        audio.SessionPlaybackCompleted += (_, _) => Interlocked.Increment(ref completed);
        var started = new ConcurrentQueue<Guid>();
        coordinator.PlaybackStarted += (_, value) => started.Enqueue(value.Track!.Id);
        Require((await coordinator.PlayAsync(a, [a, b])).Started, "Online A fixture did not start.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (coordinator.Snapshot.State != PlaybackState.Error || audio.IsDecodeActive)
            await Task.Delay(10, timeout.Token);
        await Task.Delay(250);
        Require(online.Resolved.All(id => id == a.Id) && online.Resolved.Count == 3,
            "Expected original A, loop A, and exactly one URL refresh A.");
        Require(server.SuccessRequests > 0 && server.FailureRequests == 2, "Expected two genuine HTTP decode failures.");
        Require(completed == 1 && started.Count == 1 && started.All(id => id == a.Id), "A failure advanced or completed B.");
        Require(queue.Current?.Id == a.Id && coordinator.Snapshot.Track?.Id == a.Id, "Failed loop did not retain A.");
        report.OnlineRepeatFailurePassed = true;
        report.OnlineResolveCount = online.Resolved.Count;
        report.FailedHttpDecodeCount = server.FailureRequests;
        Console.WriteLine("PASS online RepeatOne: A EOF, expired A HTTP 403, one refreshed A HTTP 403, retained A in Error, no B/no extra completion.");
    }
    await Task.Delay(100);
    Require(observed.Keys.All(id => !Alive(id)), "Verifier-observed FFmpeg process remains after disposal.");
    report.Passed = true;
    exitCode = 0;
}
catch (Exception error) { report.Error = error.ToString(); Console.Error.WriteLine(error); }
finally
{
    observationCancellation.Cancel();
    await observer;
    if (File.Exists(fixture)) File.Delete(fixture);
    report.CompletedAtUtc = DateTimeOffset.UtcNow;
    report.ObservedFfmpegProcessIds = observed.Keys.Order().ToArray();
    report.RemainingFfmpegProcessIds = observed.Keys.Where(Alive).Order().ToArray();
    var reportPath = Path.Combine(directory, "loop-verification.json");
    await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Report: {reportPath}");
}
return exitCode;

static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static Track OnlineTrack(string title) => new() { Title = title, SourceKind = TrackSourceKind.Online, ProviderId = "fixture", ProviderTrackId = title, Availability = MusicAvailability.Full };
static HashSet<int> FfmpegIds() => Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
static bool Alive(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch (ArgumentException) { return false; } }
static string FindRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "NekoPlayer.sln"))) directory = directory.Parent;
    return directory?.FullName ?? throw new DirectoryNotFoundException("NekoPlayer.sln not found.");
}
static byte[] CreateWave(double seconds)
{
    const int rate = 48000;
    var samples = (int)(seconds * rate);
    using var stream = new MemoryStream();
    using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
    writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 4);
    writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
    writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4); writer.Write((short)4); writer.Write((short)16);
    writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples * 4);
    for (var i = 0; i < samples; i++) { var value = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 2000); writer.Write(value); writer.Write(value); }
    return stream.ToArray();
}

sealed class Locator : IFfmpegLocator
{
    public Locator(string root, string temporary) { BinaryDirectory = Path.Combine(root, "tools", "ffmpeg"); GlobalFFOptions.Configure(new FFOptions { BinaryFolder = BinaryDirectory, TemporaryFilesFolder = temporary }); }
    public string BinaryDirectory { get; }
    public string FfmpegPath => Path.Combine(BinaryDirectory, "ffmpeg.exe");
    public string FfprobePath => Path.Combine(BinaryDirectory, "ffprobe.exe");
    public bool IsAvailable => File.Exists(FfmpegPath) && File.Exists(FfprobePath);
    public bool HasSharedLibraries => true;
    public string Version { get; private set; } = "not checked";
    public string StatusMessage => "bundled isolated verifier";
    public void Configure() { }
    public async Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        using var process = Process.Start(new ProcessStartInfo(FfmpegPath, "-version") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken); var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(8));
        Version = (await stdout).Split('\n')[0].Trim(); await stderr;
        if (process.ExitCode != 0) throw new InvalidOperationException("Bundled FFmpeg cannot start.");
        return new(IsAvailable, true, Version, BinaryDirectory, StatusMessage);
    }
}
sealed class NoSpectrum : ISpectrumService
{
    public IReadOnlyList<float> Bands => [];
    public bool IsEnabled { get; set; }
    public int FramesPerSecond { get; set; } = 30;
    public void PushPcm(ReadOnlySpan<byte> float32StereoPcm) { }
    public event EventHandler<IReadOnlyList<float>>? SpectrumUpdated { add { } remove { } }
}
sealed class SilentOutput : IWavePlayer, IWavePosition
{
    private readonly CancellationTokenSource _lifetime = new();
    private IWaveProvider? _provider;
    private int _state;
    private long _position;
    private bool _disposed;
    public NAudio.Wave.PlaybackState PlaybackState => (NAudio.Wave.PlaybackState)Volatile.Read(ref _state);
    public float Volume { get; set; }
    public WaveFormat OutputWaveFormat => _provider!.WaveFormat;
    public event EventHandler<StoppedEventArgs>? PlaybackStopped;
    public void Init(IWaveProvider provider) => _provider = provider;
    public long GetPosition() => Interlocked.Read(ref _position);
    public void Play() { var previous = Interlocked.Exchange(ref _state, (int)NAudio.Wave.PlaybackState.Playing); if (previous == (int)NAudio.Wave.PlaybackState.Stopped) _ = RenderAsync(); }
    public void Pause() => Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Paused);
    public void Stop() => Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Stopped);
    private async Task RenderAsync()
    {
        try
        {
            var block = new byte[_provider!.WaveFormat.AverageBytesPerSecond / 5];
            while (!_lifetime.IsCancellationRequested && PlaybackState != NAudio.Wave.PlaybackState.Stopped)
            {
                if (PlaybackState == NAudio.Wave.PlaybackState.Paused) { await Task.Delay(5, _lifetime.Token); continue; }
                var read = _provider.Read(block, 0, block.Length);
                if (read == 0) { await Task.Delay(10, _lifetime.Token); Stop(); PlaybackStopped?.Invoke(this, new StoppedEventArgs()); return; }
                await Task.Delay(5, _lifetime.Token);
                Interlocked.Add(ref _position, block.Length);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }
    public void Dispose() { if (_disposed) return; _disposed = true; Stop(); _lifetime.Cancel(); }
}
sealed class NoLyrics : ILyricsService
{
    public IReadOnlyList<LyricsLine> Parse(string content) => [];
    public Task<IReadOnlyList<LyricsLine>> LoadForTrackAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LyricsLine>>([]);
}
sealed class MemoryCatalog : ITrackCatalog
{
    public Task<IReadOnlyList<Track>> GetTracksByIdsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Track>>([]);
    public Task<Track> EnsureOnlineTrackAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult(track);
    public Task<IReadOnlyList<Track>> EnsureOnlineTracksAsync(IEnumerable<Track> tracks, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Track>>(tracks.ToArray());
}
class OfflineOnline : IOnlineMusicService
{
    public IReadOnlyList<MusicProviderInfo> Providers => [];
    public Task<MusicSearchPage> SearchAsync(MusicSearchRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public virtual Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<IReadOnlyList<LyricsLine>> GetLyricsAsync(Track track, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<LyricsLine>>([]);
}
sealed class OnlineFixture(string url) : OfflineOnline
{
    public ConcurrentQueue<Guid> Resolved { get; } = [];
    public override Task<PlaybackResolution> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        Resolved.Enqueue(track.Id);
        return Task.FromResult(new PlaybackResolution(MusicAvailability.Full,
            new AudioSource(url + (Resolved.Count == 1 ? "valid.wav" : "expired.wav"), true, TimeSpan.FromSeconds(6.4))));
    }
}
sealed class HttpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ConcurrentBag<Task> _connections = [];
    private readonly byte[] _wav;
    private readonly Task _accept;
    private int _success, _failure;
    public HttpFixture(byte[] wav) { _wav = wav; _listener.Start(); Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/"; _accept = AcceptAsync(); }
    public string Url { get; }
    public int SuccessRequests => Volatile.Read(ref _success);
    public int FailureRequests => Volatile.Read(ref _failure);
    private async Task AcceptAsync()
    {
        try { while (!_lifetime.IsCancellationRequested) _connections.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_lifetime.Token))); }
        catch (Exception) when (_lifetime.IsCancellationRequested) { }
    }
    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        try
        {
            var stream = client.GetStream(); using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, true);
            var first = await reader.ReadLineAsync(_lifetime.Token);
            string? line; do { line = await reader.ReadLineAsync(_lifetime.Token); } while (!string.IsNullOrEmpty(line));
            var valid = first?.Contains("/valid.wav", StringComparison.Ordinal) == true;
            if (valid) Interlocked.Increment(ref _success); else Interlocked.Increment(ref _failure);
            var body = valid ? _wav : [];
            var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {(valid ? "200 OK" : "403 Forbidden")}\r\nContent-Type: audio/wav\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(header, _lifetime.Token); if (body.Length > 0) await stream.WriteAsync(body, _lifetime.Token);
        }
        catch (Exception error) when (_lifetime.IsCancellationRequested || error is IOException or SocketException) { }
    }
    public async ValueTask DisposeAsync() { _lifetime.Cancel(); _listener.Stop(); await _accept; await Task.WhenAll(_connections); _lifetime.Dispose(); }
}
sealed record Cycle(long SessionId, long PcmBytes, double EndPositionSeconds, int BufferedBytes, bool Decoding);
sealed class Report
{
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset CompletedAtUtc { get; set; }
    public int RequestedCycles { get; set; }
    public string OutputFactory { get; } = "SilentOutput (memory only; no audio device)";
    public string Profile { get; } = "none";
    public string FfmpegVersion { get; set; } = "";
    public string FixtureSha256 { get; set; } = "";
    public bool LocalRepeatOnePassed { get; set; }
    public bool OnlineRepeatFailurePassed { get; set; }
    public bool Passed { get; set; }
    public Cycle[] Cycles { get; set; } = [];
    public int MaxBufferedBytes { get; set; }
    public int BufferCapacityBytes { get; set; }
    public int OnlineResolveCount { get; set; }
    public int FailedHttpDecodeCount { get; set; }
    public int[] ObservedFfmpegProcessIds { get; set; } = [];
    public int[] RemainingFfmpegProcessIds { get; set; } = [];
    public string? Error { get; set; }
}
