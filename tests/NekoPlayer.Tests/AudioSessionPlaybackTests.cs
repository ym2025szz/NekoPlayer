using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using FFMpegCore;
using NAudio.Wave;
using NekoPlayer.Audio.Playback;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using PlaybackState = NekoPlayer.Core.Enums.PlaybackState;

namespace NekoPlayer.Tests;

// No test in this class opens NAudio hardware or uses the user's music files.
[Collection("Silent audio integration")]
public sealed class AudioSessionPlaybackTests
{
    [Fact]
    public async Task HttpFailureRemainsErrorAndNeverPublishesCompletion()
    {
        var locator = new AudioTestLocator();
        await using var server = new AudioHttpFixture((_, _) => Task.FromResult(new AudioHttpReply(403, [])));
        await using var player = new FfmpegAudioPlayerService(locator, new NoSpectrum(), () => new SilentOutput());
        var states = new ConcurrentQueue<AudioSessionState>();
        var failures = new ConcurrentQueue<AudioSessionFailure>();
        var completions = 0;
        player.SessionStateChanged += (_, value) => states.Enqueue(value);
        player.SessionPlaybackFailed += (_, value) => failures.Enqueue(value);
        player.SessionPlaybackCompleted += (_, _) => Interlocked.Increment(ref completions);
        await Assert.ThrowsAnyAsync<Exception>(() => player.LoadAsync(new AudioSource(server.Url + "expired?token=private", true, SessionId: 71)));
        await Task.Delay(200);
        Assert.Equal(PlaybackState.Error, player.State);
        Assert.Equal(71, Assert.Single(failures).SessionId);
        Assert.DoesNotContain("private", Assert.Single(failures).Error.ToString());
        Assert.DoesNotContain(states, value => value.State is PlaybackState.Playing or PlaybackState.Paused);
        Assert.Equal(0, completions);
        await Assert.ThrowsAnyAsync<Exception>(() => player.PlayAsync());
        Assert.Equal(PlaybackState.Error, player.State);
    }

    [Fact]
    public async Task HeaderProtectedPreviewDrainsDeviceTailBeforeCompletion()
    {
        var wav = CreateWave(TimeSpan.FromMilliseconds(530));
        var headerObserved = false;
        await using var server = new AudioHttpFixture((request, _) =>
        {
            headerObserved = request.Contains("X-Audio-Test: expected", StringComparison.OrdinalIgnoreCase);
            return Task.FromResult(new AudioHttpReply(headerObserved ? 200 : 403, headerObserved ? wav : []));
        });
        SilentOutput? output = null;
        var decoderLines = new ConcurrentQueue<string>();
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(),
            () => output = new SilentOutput(), line => decoderLines.Enqueue(line));
        var completed = new TaskCompletionSource<AudioSessionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SessionPlaybackCompleted += (_, value) => completed.TrySetResult(value);
        try
        {
            await player.LoadAsync(new AudioSource(server.Url + "preview", true, TimeSpan.FromMilliseconds(530),
                new Dictionary<string, string> { ["X-Audio-Test"] = "expected" }, IsPreview: true,
                PreviewStart: TimeSpan.FromSeconds(90), SessionId: 72));
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(string.Join('\n', decoderLines.TakeLast(30)), error);
        }
        Assert.True(headerObserved);
        Assert.InRange(player.Position.TotalSeconds, 0, 0.01);
        await player.PlayAsync();
        await WaitUntilAsync(() => output!.WaitingForTail);
        Assert.Equal(0, player.BufferedBytes);
        Assert.False(completed.Task.IsCompleted);
        Assert.Equal(NAudio.Wave.PlaybackState.Playing, output!.PlaybackState);
        output.ReleaseTail();
        var completion = await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new AudioSessionCompletion(72, true), completion);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.InRange(player.Position.TotalSeconds, 0.52, 0.54);
    }

    [Fact]
    public async Task StarvationFreezesMediaClockAndResumesWithoutCompletion()
    {
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var wav = CreateWave(TimeSpan.FromSeconds(8));
        await using var server = new AudioHttpFixture((_, _) => Task.FromResult(new AudioHttpReply(200, wav,
            PauseAfterBytes: 44 + 48000 * 4 * 3, Resume: resume.Task)));
        SilentOutput? output = null;
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(), () => output = new SilentOutput(autoReleaseTail: true));
        var completions = 0;
        player.PlaybackCompleted += (_, _) => Interlocked.Increment(ref completions);
        await player.LoadAsync(new AudioSource(server.Url + "delayed.wav", true, TimeSpan.FromSeconds(8), CanSeek: false, SessionId: 73));
        await player.PlayAsync();
        await WaitUntilAsync(() => player.State == PlaybackState.Buffering);
        var frozen = player.Position;
        await Task.Delay(250);
        Assert.Equal(frozen, player.Position);
        Assert.Equal(0, completions);
        resume.TrySetResult();
        await WaitUntilAsync(() => player.State == PlaybackState.Playing && player.Position > frozen);
        Assert.Equal(0, completions);
        Assert.True(player.BufferedBytes <= player.BufferCapacityBytes);
        await player.StopAsync();
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task CancellingAStalledHttpLoadReapsItsFfmpegProcess()
    {
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new AudioHttpFixture(async (_, token) =>
        {
            requestReceived.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
            return new AudioHttpReply(200, []);
        });
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(), () => new SilentOutput());
        var before = GetFfmpegIds();
        using var cancellation = new CancellationTokenSource();
        var load = player.LoadAsync(new AudioSource(server.Url + "stall", true, SessionId: 74), cancellationToken: cancellation.Token);
        await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var observed = GetFfmpegIds().Except(before).ToArray();
        Assert.NotEmpty(observed);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(5)));
        await player.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => observed.All(id => !IsAlive(id)));
        Assert.False(player.IsDecodeActive);
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(25, 5)]
    public async Task PreviewLongInputStopsAtThirtySecondsIncludingSeekRemainder(int seekSeconds, int expectedPcmSeconds)
    {
        var wav = CreateWave(TimeSpan.FromSeconds(60));
        await using var server = new AudioHttpFixture((_, _) => Task.FromResult(new AudioHttpReply(200, wav)));
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(),
            () => new SilentOutput(autoReleaseTail: true, renderDelayMilliseconds: 2, renderBufferMilliseconds: 200));
        var complete = new TaskCompletionSource<AudioSessionCompletion>(TaskCreationOptions.RunContinuationsAsynchronously);
        player.SessionPlaybackCompleted += (_, value) => complete.TrySetResult(value);
        await player.LoadAsync(new AudioSource(server.Url + "full-sixty-seconds.wav", true, TimeSpan.FromSeconds(30),
            IsPreview: true, PreviewStart: TimeSpan.FromSeconds(90), SessionId: 77));
        Assert.Equal(TimeSpan.FromSeconds(30), player.Duration);
        long discardedRunBytes = 0;
        if (seekSeconds > 0)
        {
            await player.StopAsync();
            discardedRunBytes = player.TotalPcmBytesReceived;
            await player.SeekAsync(TimeSpan.FromSeconds(seekSeconds));
        }
        await player.PlayAsync();
        AudioSessionCompletion completion;
        try { completion = await complete.Task.WaitAsync(TimeSpan.FromSeconds(8)); }
        catch (TimeoutException error)
        {
            throw new TimeoutException($"Preview drain timed out: state={player.State}, position={player.Position.TotalSeconds:F3}, buffered={player.BufferedBytes}, decoding={player.IsDecodeActive}, pcm={player.TotalPcmBytesReceived - discardedRunBytes}.", error);
        }
        Assert.True(completion.IsPreview);
        Assert.InRange(player.Position.TotalSeconds, 29.99, 30.01);
        var activeRunBytes = player.TotalPcmBytesReceived - discardedRunBytes;
        Assert.Equal(expectedPcmSeconds * 48000L * 2 * sizeof(float), activeRunBytes);
    }

    [Fact]
    public async Task UnknownPreviewDurationCannotFallBackToAFullTrack()
    {
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(), () => new SilentOutput());
        await Assert.ThrowsAsync<InvalidOperationException>(() => player.LoadAsync(new AudioSource(
            "http://127.0.0.1:1/full-track.wav", true, IsPreview: true, SessionId: 78)));
        Assert.Equal(PlaybackState.Error, player.State);
        Assert.Equal(TimeSpan.Zero, player.Duration);
        Assert.False(player.IsDecodeActive);
    }

    [Fact]
    public async Task ZeroPcmNeverCompletesOrChangesFailureToPaused()
    {
        await using var server = new AudioHttpFixture((_, _) => Task.FromResult(new AudioHttpReply(200, CreateWave(TimeSpan.Zero))));
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(), () => new SilentOutput());
        var completed = false;
        player.PlaybackCompleted += (_, _) => completed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => player.LoadAsync(new AudioSource(server.Url + "empty.wav", true, SessionId: 75)));
        await Task.Delay(100);
        Assert.Equal(PlaybackState.Error, player.State);
        Assert.False(completed);
    }

    [Fact]
    public async Task CancelledPlayingSeekKeepsIntentForFinalSeek()
    {
        var path = Path.Combine(Path.GetTempPath(), "NekoSilentAudio-" + Guid.NewGuid().ToString("N") + ".wav");
        await File.WriteAllBytesAsync(path, CreateWave(TimeSpan.FromSeconds(20)));
        using var cancellation = new CancellationTokenSource();
        var outputNumber = 0;
        await using var player = new FfmpegAudioPlayerService(new AudioTestLocator(), new NoSpectrum(), () =>
            new SilentOutput(onInit: Interlocked.Increment(ref outputNumber) == 2 ? cancellation.Cancel : null));
        try
        {
            await player.LoadAsync(AudioSource.Local(path, 76));
            await player.PlayAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => player.SeekAsync(TimeSpan.FromSeconds(5), cancellation.Token));
            await player.SeekAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.InRange(player.Position.TotalSeconds, 8, 10);
            await player.StopAsync();
            Assert.Equal(path, player.CurrentFilePath);
            await player.PlayAsync();
            Assert.Equal(PlaybackState.Playing, player.State);
            Assert.InRange(player.Position.TotalSeconds, 0, 2);
        }
        finally { await player.DisposeAsync(); File.Delete(path); }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }
    private static byte[] CreateWave(TimeSpan duration)
    {
        const int rate = 48000, channels = 2, bytesPerSample = 2;
        var samples = (int)(duration.TotalSeconds * rate);
        var dataBytes = samples * channels * bytesPerSample;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + dataBytes);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
        writer.Write((short)1); writer.Write((short)channels); writer.Write(rate);
        writer.Write(rate * channels * bytesPerSample); writer.Write((short)(channels * bytesPerSample)); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(dataBytes);
        for (var i = 0; i < samples; i++)
        {
            // Generated PCM, rendered only into SilentOutput, never into an audio device.
            var value = (short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 2000);
            writer.Write(value); writer.Write(value);
        }
        return stream.ToArray();
    }
    private static HashSet<int> GetFfmpegIds() => Process.GetProcessesByName("ffmpeg").Select(p => { using (p) return p.Id; }).ToHashSet();
    private static bool IsAlive(int id) { try { using var p = Process.GetProcessById(id); return !p.HasExited; } catch (ArgumentException) { return false; } }

    private sealed class AudioTestLocator : IFfmpegLocator
    {
        public AudioTestLocator()
        {
            var root = new DirectoryInfo(AppContext.BaseDirectory);
            while (root is not null && !File.Exists(Path.Combine(root.FullName, "NekoPlayer.sln"))) root = root.Parent;
            BinaryDirectory = Path.Combine(root?.FullName ?? throw new DirectoryNotFoundException(), "tools", "ffmpeg");
            Assert.True(File.Exists(FfmpegPath), "Silent integration tests require bundled tools/ffmpeg.");
            Configure();
        }
        public string BinaryDirectory { get; }
        public string FfmpegPath => Path.Combine(BinaryDirectory, "ffmpeg.exe");
        public string FfprobePath => Path.Combine(BinaryDirectory, "ffprobe.exe");
        public bool IsAvailable => true;
        public bool HasSharedLibraries => true;
        public string Version => "silent integration";
        public string StatusMessage => "available";
        public void Configure() => GlobalFFOptions.Configure(new FFOptions { BinaryFolder = BinaryDirectory, TemporaryFilesFolder = Path.GetTempPath() });
        public Task<FfmpegValidationResult> ValidateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new FfmpegValidationResult(true, true, Version, BinaryDirectory, StatusMessage));
    }
    private sealed class NoSpectrum : ISpectrumService
    {
        public IReadOnlyList<float> Bands => [];
        public bool IsEnabled { get; set; }
        public int FramesPerSecond { get; set; } = 30;
        public void PushPcm(ReadOnlySpan<byte> float32StereoPcm) { }
        public event EventHandler<IReadOnlyList<float>>? SpectrumUpdated { add { } remove { } }
    }

    private sealed class SilentOutput(bool autoReleaseTail = false, Action? onInit = null,
        int renderDelayMilliseconds = 10, int renderBufferMilliseconds = 40) : IWavePlayer, IWavePosition
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly TaskCompletionSource _tail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IWaveProvider? _provider;
        private long _position;
        private int _state;
        private bool _disposed;
        public bool WaitingForTail { get; private set; }
        public NAudio.Wave.PlaybackState PlaybackState => (NAudio.Wave.PlaybackState)Volatile.Read(ref _state);
        public float Volume { get; set; }
        public WaveFormat OutputWaveFormat => _provider!.WaveFormat;
        public event EventHandler<StoppedEventArgs>? PlaybackStopped;
        public void Init(IWaveProvider waveProvider) { _provider = waveProvider; onInit?.Invoke(); }
        public long GetPosition() => Interlocked.Read(ref _position);
        public void ReleaseTail() => _tail.TrySetResult();
        public void Play()
        {
            var previous = PlaybackState;
            Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Playing);
            if (previous == NAudio.Wave.PlaybackState.Stopped) _ = RenderAsync();
        }
        public void Pause() => Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Paused);
        public void Stop() { Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Stopped); _tail.TrySetResult(); }
        private async Task RenderAsync()
        {
            try
            {
                // Larger accelerated-test blocks avoid relying on sub-16ms Windows timer
                // resolution while the normal fake retains WaveOut's 40ms buffer shape.
                var block = new byte[_provider!.WaveFormat.AverageBytesPerSecond * renderBufferMilliseconds / 1000];
                while (!_lifetime.IsCancellationRequested && PlaybackState != NAudio.Wave.PlaybackState.Stopped)
                {
                    if (PlaybackState == NAudio.Wave.PlaybackState.Paused) { await Task.Delay(10, _lifetime.Token); continue; }
                    var read = _provider.Read(block, 0, block.Length);
                    if (read == 0)
                    {
                        WaitingForTail = true;
                        if (autoReleaseTail) await Task.Delay(30, _lifetime.Token);
                        else await _tail.Task.WaitAsync(_lifetime.Token);
                        Volatile.Write(ref _state, (int)NAudio.Wave.PlaybackState.Stopped);
                        WaitingForTail = false;
                        PlaybackStopped?.Invoke(this, new StoppedEventArgs());
                        return;
                    }
                    await Task.Delay(renderDelayMilliseconds, _lifetime.Token);
                    Interlocked.Add(ref _position, block.Length); // Same final short-read padding as WaveOut.
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Stop(); _lifetime.Cancel();
        }
    }

    private sealed record AudioHttpReply(int Status, byte[] Body, int PauseAfterBytes = 0, Task? Resume = null);
    private sealed class AudioHttpFixture : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Func<string, CancellationToken, Task<AudioHttpReply>> _respond;
        private readonly ConcurrentBag<Task> _connections = [];
        private readonly Task _accept;
        public AudioHttpFixture(Func<string, CancellationToken, Task<AudioHttpReply>> respond)
        {
            _respond = respond; _listener.Start();
            Url = $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
            _accept = AcceptAsync();
        }
        public string Url { get; }
        private async Task AcceptAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                    _connections.Add(HandleAsync(await _listener.AcceptTcpClientAsync(_lifetime.Token)));
            }
            catch (Exception) when (_lifetime.IsCancellationRequested) { }
        }
        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    using var request = new MemoryStream();
                    var single = new byte[1];
                    while (request.Length < 16384)
                    {
                        if (await stream.ReadAsync(single, _lifetime.Token) == 0) return;
                        request.WriteByte(single[0]);
                        if (request.Length >= 4 && Encoding.ASCII.GetString(request.GetBuffer(), (int)request.Length - 4, 4) == "\r\n\r\n") break;
                    }
                    var requestText = Encoding.ASCII.GetString(request.ToArray());
                    var reply = await _respond(requestText, _lifetime.Token);
                    var offset = 0;
                    var status = reply.Status;
                    var rangeLine = requestText.Split("\r\n").FirstOrDefault(line => line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase));
                    if (status == 200 && rangeLine is not null && int.TryParse(rangeLine[13..].Split('-')[0], out var start) && start < reply.Body.Length)
                    {
                        offset = Math.Max(0, start);
                        status = 206;
                    }
                    var rangeHeader = status == 206 ? $"Content-Range: bytes {offset}-{reply.Body.Length - 1}/{reply.Body.Length}\r\n" : string.Empty;
                    var headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\nContent-Type: audio/wav\r\nContent-Length: {reply.Body.Length - offset}\r\nAccept-Ranges: bytes\r\n{rangeHeader}Connection: close\r\n\r\n");
                    await stream.WriteAsync(headers, _lifetime.Token);
                    if (reply.PauseAfterBytes > 0 && reply.Resume is not null)
                    {
                        await stream.WriteAsync(reply.Body.AsMemory(0, reply.PauseAfterBytes), _lifetime.Token);
                        await stream.FlushAsync(_lifetime.Token);
                        await reply.Resume.WaitAsync(_lifetime.Token);
                        await stream.WriteAsync(reply.Body.AsMemory(reply.PauseAfterBytes), _lifetime.Token);
                    }
                    else await stream.WriteAsync(reply.Body.AsMemory(offset), _lifetime.Token);
                }
                catch (Exception) when (_lifetime.IsCancellationRequested) { }
                catch (IOException) { }
                catch (SocketException) { }
            }
        }
        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel(); _listener.Stop();
            await _accept;
            await Task.WhenAll(_connections);
            _lifetime.Dispose();
        }
    }
}

[CollectionDefinition("Silent audio integration", DisableParallelization = true)]
public sealed class SilentAudioIntegrationCollection;
