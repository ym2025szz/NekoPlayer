using FFMpegCore;
using FFMpegCore.Enums;
using FFMpegCore.Pipes;
using NAudio.Wave;
using NekoPlayer.Audio.Decoding;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using Serilog;
using PlaybackState = NekoPlayer.Core.Enums.PlaybackState;

namespace NekoPlayer.Audio.Playback;

public sealed class FfmpegAudioPlayerService : IAudioPlayerService, IAudioPlaybackDiagnostics, IDisposable
{
    private static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ISpectrumService _spectrum;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PeriodicTimer _positionTimer = new(TimeSpan.FromMilliseconds(150));
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _decodeCts;
    private Task? _decodeTask;
    private Task? _consumerTask;
    private WaveOutEvent? _waveOut;
    private BufferedWaveProvider? _provider;
    private string? _filePath;
    private TimeSpan _basePosition;
    private float _volume = 0.75f;
    private float _volumeBeforeMute = 0.75f;
    private bool _isMuted;
    private bool _disposed;
    private long _totalPcmBytesReceived;

    public FfmpegAudioPlayerService(IFfmpegLocator ffmpeg, ISpectrumService spectrum)
    {
        _ffmpeg = ffmpeg;
        _spectrum = spectrum;
        _ = RunPositionTimerAsync(_lifetimeCts.Token);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public TimeSpan Position => ClampPosition(_basePosition + GetOutputElapsed());
    public TimeSpan Duration { get; private set; }
    public int BufferedBytes => _provider?.BufferedBytes ?? 0;
    public int BufferCapacityBytes => _provider?.BufferLength ?? 0;
    public long TotalPcmBytesReceived => Interlocked.Read(ref _totalPcmBytesReceived);
    public bool IsOutputInitialized => _waveOut is not null && _provider is not null;
    public bool IsDecodeActive => _decodeTask is { IsCompleted: false };
    public string? CurrentFilePath => _filePath;
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            if (!_isMuted) { _volumeBeforeMute = _volume; ApplyVolume(); }
        }
    }
    public bool IsMuted
    {
        get => _isMuted;
        set
        {
            if (_isMuted == value) return;
            _isMuted = value;
            if (value) _volumeBeforeMute = _volume;
            else _volume = _volumeBeforeMute;
            ApplyVolume();
        }
    }

    public event EventHandler<PlaybackState>? StateChanged;
    public event EventHandler<TimeSpan>? PositionChanged;
    public event EventHandler? PlaybackCompleted;
    public event EventHandler<Exception>? PlaybackFailed;

    public async Task LoadAsync(string filePath, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (!_ffmpeg.IsAvailable) throw new InvalidOperationException(_ffmpeg.StatusMessage);
            if (!File.Exists(filePath)) throw new FileNotFoundException("音乐文件不存在。", filePath);
            SetState(PlaybackState.Loading);
            await StopCoreAsync(false);
            _filePath = filePath;
            var analysis = await FFProbe.AnalyseAsync(filePath, cancellationToken: cancellationToken);
            Duration = analysis.Duration;
            _basePosition = ClampPosition(startPosition ?? TimeSpan.Zero);
            await StartDecoderAsync(_basePosition, cancellationToken);
            SetState(PlaybackState.Paused);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(PlaybackState.Error);
            PlaybackFailed?.Invoke(this, ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (_filePath is null) return;
            if (_decodeTask is null || _decodeTask.IsCompleted) await StartDecoderAsync(Position, cancellationToken);
            _waveOut?.Play();
            SetState(PlaybackState.Playing);
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != PlaybackState.Playing) return;
            _waveOut?.Pause();
            SetState(PlaybackState.Paused);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(false); }
        finally { _gate.Release(); }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(true); }
        finally { _gate.Release(); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_filePath is null) return;
            var resume = State == PlaybackState.Playing;
            var target = ClampPosition(position);
            Log.Information("Seek 到 {Position}", target);
            SetState(PlaybackState.Seeking);
            _waveOut?.Stop();
            await StopDecoderAsync();
            _provider?.ClearBuffer();
            _basePosition = target;
            await StartDecoderAsync(target, cancellationToken);
            if (resume)
            {
                _waveOut?.Play();
                SetState(PlaybackState.Playing);
            }
            else SetState(PlaybackState.Paused);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Error);
            PlaybackFailed?.Invoke(this, ex);
            Log.Error(ex, "Seek 失败：{FilePath}", _filePath);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartDecoderAsync(TimeSpan startPosition, CancellationToken outerToken)
    {
        await StopDecoderAsync();
        _provider = new BufferedWaveProvider(OutputFormat)
        {
            BufferLength = OutputFormat.AverageBytesPerSecond * 5,
            DiscardOnBufferOverflow = false,
            ReadFully = true
        };
        _waveOut?.Dispose();
        _waveOut = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3, Volume = _isMuted ? 0 : _volume };
        try { _waveOut.Init(_provider); }
        catch (Exception ex)
        {
            _waveOut.Dispose();
            _waveOut = null;
            throw new InvalidOperationException("无法初始化 Windows 默认音频输出设备。", ex);
        }

        _decodeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, outerToken);
        var token = _decodeCts.Token;
        var pipe = new BoundedPcmStream(token);
        var consumer = ConsumePcmAsync(pipe, token);
        _consumerTask = consumer;
        _decodeTask = DecodeAsync(pipe, consumer, startPosition, token);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (_provider.BufferedDuration < TimeSpan.FromMilliseconds(150) && !_decodeTask.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(25, token);
    }

    private async Task DecodeAsync(BoundedPcmStream pipe, Task consumerTask, TimeSpan startPosition, CancellationToken token)
    {
        try
        {
            Log.Information("开始 FFmpeg 解码：{FilePath}", _filePath);
            var arguments = FFMpegArguments
                .FromFileInput(_filePath!, true, options =>
                {
                    if (startPosition > TimeSpan.Zero) options.Seek(startPosition);
                })
                .OutputToPipe(new StreamPipeSink(pipe), options => options
                    .DisableChannel(Channel.Video)
                    .ForceFormat("f32le")
                    .WithAudioCodec("pcm_f32le")
                    .WithAudioSamplingRate(48000)
                    .WithCustomArgument("-ac 2"));
            var success = await arguments.ProcessAsynchronously();
            if (!success && !token.IsCancellationRequested) throw new InvalidOperationException("FFmpeg 解码异常退出。");
            pipe.Complete();
            await consumerTask;

            while (!token.IsCancellationRequested && _provider is { BufferedBytes: > 0 })
                await Task.Delay(30, token);
            if (!token.IsCancellationRequested)
            {
                _waveOut?.Stop();
                _basePosition = Duration;
                SetState(PlaybackState.Stopped);
                PlaybackCompleted?.Invoke(this, EventArgs.Empty);
            }
            Log.Information("FFmpeg 解码结束：{FilePath}", _filePath);
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            pipe.Complete();
            Log.Debug(ex, "FFmpeg decode cancelled");
        }
        catch (Exception ex)
        {
            pipe.Complete(ex);
            SetState(PlaybackState.Error);
            Log.Error(ex, "FFmpeg 解码失败：{FilePath}", _filePath);
            PlaybackFailed?.Invoke(this, ex);
        }
    }

    private async Task ConsumePcmAsync(BoundedPcmStream pipe, CancellationToken token)
    {
        await foreach (var block in pipe.Reader.ReadAllAsync(token))
        {
            while (_provider is not null && _provider.BufferedBytes + block.Length > _provider.BufferLength)
                await Task.Delay(15, token);
            Interlocked.Add(ref _totalPcmBytesReceived, block.Length);
            _spectrum.PushPcm(block);
            _provider?.AddSamples(block, 0, block.Length);
        }
    }

    private async Task StopCoreAsync(bool clearFile)
    {
        _waveOut?.Stop();
        await StopDecoderAsync();
        _provider?.ClearBuffer();
        _basePosition = TimeSpan.Zero;
        if (clearFile) { _filePath = null; Duration = TimeSpan.Zero; }
        SetState(PlaybackState.Stopped);
        PositionChanged?.Invoke(this, TimeSpan.Zero);
    }

    private async Task StopDecoderAsync()
    {
        var cts = Interlocked.Exchange(ref _decodeCts, null);
        var decode = Interlocked.Exchange(ref _decodeTask, null);
        var consumer = Interlocked.Exchange(ref _consumerTask, null);
        if (cts is null) return;
        cts.Cancel();
        try
        {
            var tasks = new[] { decode, consumer }.Where(x => x is not null).Cast<Task>().ToArray();
            if (tasks.Length > 0) await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            Log.Error(ex, "Old FFmpeg decode task did not stop within 5 seconds");
            throw new TimeoutException("旧 FFmpeg 解码任务未能在 5 秒内结束。", ex);
        }
        catch (Exception ex) when (cts.IsCancellationRequested)
        {
            Log.Debug(ex, "Old FFmpeg decode task ended during cancellation");
        }
        finally { cts.Dispose(); }
    }

    private async Task RunPositionTimerAsync(CancellationToken token)
    {
        try
        {
            while (await _positionTimer.WaitForNextTickAsync(token))
                if (State is PlaybackState.Playing or PlaybackState.Paused) PositionChanged?.Invoke(this, Position);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private TimeSpan ClampPosition(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && value > Duration ? Duration : value;
    private TimeSpan GetOutputElapsed()
    {
        try
        {
            var bytes = _waveOut?.GetPosition() ?? 0;
            return TimeSpan.FromSeconds(bytes / (double)OutputFormat.AverageBytesPerSecond);
        }
        catch (ObjectDisposedException) { return TimeSpan.Zero; }
    }
    private void ApplyVolume() { if (_waveOut is not null) _waveOut.Volume = _isMuted ? 0 : _volume; }
    private void SetState(PlaybackState state) { if (State == state) return; State = state; StateChanged?.Invoke(this, state); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        await _gate.WaitAsync();
        try { await StopCoreAsync(true); }
        finally { _gate.Release(); }
        _waveOut?.Dispose();
        _positionTimer.Dispose();
        _lifetimeCts.Dispose();
        _gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
