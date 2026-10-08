using System.Text;
using System.IO.Pipes;
using FFMpegCore;
using FFMpegCore.Enums;
using NAudio;
using NAudio.Wave;
using NekoPlayer.Audio.Decoding;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using Serilog;
using PlaybackState = NekoPlayer.Core.Enums.PlaybackState;
using OutputPlaybackState = NAudio.Wave.PlaybackState;

namespace NekoPlayer.Audio.Playback;

public sealed class FfmpegAudioPlayerService : ISessionAudioPlayerService, IAudioPlaybackDiagnostics, IDisposable
{
    private static readonly WaveFormat OutputFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    private static readonly int StartupBufferBytes = OutputFormat.AverageBytesPerSecond * 150 / 1000;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ISpectrumService _spectrum;
    private readonly Func<IWavePlayer> _outputFactory;
    private readonly Action<string>? _decoderDiagnostic;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PeriodicTimer _positionTimer = new(TimeSpan.FromMilliseconds(50));
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly Task _positionTask;
    private DecoderRun? _run;
    private AudioSource? _source;
    private TimeSpan _basePosition;
    private float _volume = 0.75f;
    private float _volumeBeforeMute = 0.75f;
    private bool _isMuted;
    // Independent from temporary Seeking/Buffering states, including cancelled seeks.
    private bool _wantsPlayback;
    private bool _disposed;
    private long _legacySessionCounter;
    private long _totalPcmBytesReceived;
    private long _lastFailureSession = long.MinValue;
    private Task? _disposeTask;

    public FfmpegAudioPlayerService(IFfmpegLocator ffmpeg, ISpectrumService spectrum)
        : this(ffmpeg, spectrum, () => new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 }) { }

    /// <summary>Allows deterministic verification without opening an audio device.</summary>
    public FfmpegAudioPlayerService(IFfmpegLocator ffmpeg, ISpectrumService spectrum, Func<IWavePlayer> outputFactory,
        Action<string>? decoderDiagnostic = null)
    {
        _ffmpeg = ffmpeg;
        _spectrum = spectrum;
        _outputFactory = outputFactory;
        _decoderDiagnostic = decoderDiagnostic;
        _positionTask = RunPositionTimerAsync(_lifetimeCts.Token);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public long SessionId { get; private set; }
    public bool CanSeek => _source is { CanSeek: true } && Duration > TimeSpan.Zero;
    public TimeSpan Position
    {
        get
        {
            var run = _run;
            if (run is null || State == PlaybackState.Error) return ClampPosition(_basePosition);
            try
            {
                var bytes = run.Output is IWavePosition position ? position.GetPosition() : 0;
                return ClampPosition(run.StartPosition + TimeSpan.FromSeconds(
                    run.Provider.GetMediaBytesPlayed(bytes) / (double)OutputFormat.AverageBytesPerSecond));
            }
            catch (Exception ex) when (ex is ObjectDisposedException or MmException) { return ClampPosition(_basePosition); }
        }
    }
    public TimeSpan Duration { get; private set; }
    public int BufferedBytes => _run?.Provider.BufferedBytes ?? 0;
    public int BufferCapacityBytes => _run?.Provider.BufferLength ?? 0;
    public long TotalPcmBytesReceived => Interlocked.Read(ref _totalPcmBytesReceived);
    public bool IsOutputInitialized => _run is not null;
    public bool IsDecodeActive => _run?.DecodeTask is { IsCompleted: false };
    public string? CurrentFilePath => _source?.Input;
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
    public event EventHandler<AudioSessionState>? SessionStateChanged;
    public event EventHandler<AudioSessionPosition>? SessionPositionChanged;
    public event EventHandler<AudioSessionFailure>? SessionPlaybackFailed;
    public event EventHandler<AudioSessionCompletion>? SessionPlaybackCompleted;

    public Task LoadAsync(string filePath, TimeSpan? startPosition = null, CancellationToken cancellationToken = default) =>
        LoadAsync(AudioSource.Local(filePath, Interlocked.Increment(ref _legacySessionCounter)), startPosition, cancellationToken);

    public async Task LoadAsync(AudioSource source, TimeSpan? startPosition = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        // FFProbe can retain cancellation callbacks after its process has exited.
        // Detach this operation from the playback intent once loading is complete.
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var operationToken = operation.Token;
        await _gate.WaitAsync(operationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            await StopDecoderAsync().ConfigureAwait(false);
            _source = source;
            SessionId = source.SessionId;
            _lastFailureSession = long.MinValue;
            _wantsPlayback = false;
            _basePosition = TimeSpan.Zero;
            Duration = source.Duration is { } hint && hint > TimeSpan.Zero ? hint : TimeSpan.Zero;
            SetState(PlaybackState.Loading);
            if (!_ffmpeg.IsAvailable) throw new InvalidOperationException(_ffmpeg.StatusMessage);
            ValidateSource(source);
            if (!source.IsRemote)
            {
                var analysis = await FFProbe.AnalyseAsync(source.Input, cancellationToken: operationToken).ConfigureAwait(false);
                if (analysis.PrimaryAudioStream is null) throw new InvalidOperationException("文件中没有可播放的音频。");
                Duration = source.IsPreview && source.Duration is { } previewDuration
                    ? (analysis.Duration < previewDuration ? analysis.Duration : previewDuration)
                    : analysis.Duration;
            }
            // PreviewStart is a lyric offset; preview URLs start at their playable origin.
            _basePosition = CanSeek ? ClampPosition(startPosition ?? TimeSpan.Zero) : TimeSpan.Zero;
            await StartDecoderAsync(_basePosition, operationToken).ConfigureAwait(false);
            ThrowIfRunFailed();
            SetState(PlaybackState.Paused);
            PublishPosition();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            await StopDecoderAsync().ConfigureAwait(false);
            SetState(PlaybackState.Stopped);
            // FFProbe/FFmpeg may report a killed process as a nonzero-exit exception.
            // Cancellation is a transport action, so it must not publish PlaybackFailed.
            throw new OperationCanceledException("音频加载已取消。", ex, cancellationToken);
        }
        catch (Exception ex) { ReportFailure(ex); throw; }
        finally { _gate.Release(); }
    }

    public async Task PlayAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_source is null) return;
            ThrowIfRunFailed();
            if (State == PlaybackState.Error) throw new InvalidOperationException("当前音频加载失败，请重新加载。");
            _wantsPlayback = true;
            if (_run is null || _run.Completed)
            {
                if (_run?.Completed == true) _basePosition = TimeSpan.Zero;
                await StartDecoderAsync(_basePosition, cancellationToken).ConfigureAwait(false);
            }
            ThrowIfRunFailed();
            UpdatePlayback();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            await StopDecoderAsync().ConfigureAwait(false);
            SetState(PlaybackState.Stopped);
            throw new OperationCanceledException("播放请求已取消。", ex, cancellationToken);
        }
        catch (Exception ex) { ReportFailure(ex); throw; }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            _wantsPlayback = false;
            _run?.Output.Pause();
            if (_source is not null && State != PlaybackState.Error) SetState(PlaybackState.Paused);
            PublishPosition();
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); await StopCoreAsync(false).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { ThrowIfDisposed(); await StopCoreAsync(true).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_source is null || !CanSeek) return;
            ThrowIfRunFailed();
            var target = ClampPosition(position);
            SetState(PlaybackState.Seeking);
            await StopDecoderAsync().ConfigureAwait(false);
            _basePosition = target;
            await StartDecoderAsync(target, cancellationToken).ConfigureAwait(false);
            ThrowIfRunFailed();
            UpdatePlayback();
            PublishPosition();
        }
        catch (Exception ex) when (cancellationToken.IsCancellationRequested)
        {
            await StopDecoderAsync().ConfigureAwait(false);
            // A newer seek owns recovery; retain user intent after this temporary run ends.
            SetState(_wantsPlayback ? PlaybackState.Seeking : PlaybackState.Paused);
            throw new OperationCanceledException("跳转请求已取消。", ex, cancellationToken);
        }
        catch (Exception ex) { ReportFailure(ex); throw; }
        finally { _gate.Release(); }
    }

    private async Task StartDecoderAsync(TimeSpan startPosition, CancellationToken operationToken)
    {
        await StopDecoderAsync().ConfigureAwait(false);
        operationToken.ThrowIfCancellationRequested();
        var source = _source ?? throw new InvalidOperationException("没有已加载的音频。");
        var run = new DecoderRun(source, startPosition, OutputFormat, _outputFactory(), _lifetimeCts.Token);
        _run = run;
        try
        {
            run.Output.Init(run.Provider);
            run.Output.Volume = _isMuted ? 0 : _volume;
            run.Output.PlaybackStopped += (_, args) =>
            {
                if (args.Exception is not null) run.Fail(new InvalidOperationException("音频输出设备播放失败。", args.Exception));
            };
            // Operation cancellation applies only until readiness. It cannot cancel playback
            // after a successful seek/load returns to a caller that later replaces its token.
            using var registration = operationToken.Register(() => run.Cancellation.Cancel());
            run.ConsumerTask = ConsumePcmAsync(run);
            run.DecodeTask = DecodeAsync(run);
            await run.Ready.Task.WaitAsync(TimeSpan.FromSeconds(20), operationToken).ConfigureAwait(false);
            operationToken.ThrowIfCancellationRequested();
            ThrowIfRunFailed();
        }
        catch
        {
            await StopDecoderAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task DecodeAsync(DecoderRun run)
    {
        var token = run.Cancellation.Token;
        try
        {
            Action<FFMpegArgumentOptions> configureInput = options =>
            {
                if (run.Source.IsRemote)
                {
                    // FFOptions has no child-environment hook. FFmpeg's explicit empty
                    // http_proxy overrides inherited HTTP_PROXY for both HTTP and TLS,
                    // so media connects directly without changing the host environment.
                    options.WithCustomArgument("-http_proxy \"\" -rw_timeout 15000000 -analyzeduration 0 -probesize 32768");
                    // Plain HTTP has no tls_verify AVOption and rejects it as unused.
                    if (new Uri(run.Source.Input).Scheme == Uri.UriSchemeHttps)
                        options.WithCustomArgument("-tls_verify 1");
                    var headers = BuildHeaders(run.Source.Headers);
                    if (headers.Length > 0) options.WithCustomArgument("-headers " + QuoteArgument(headers));
                }
                if (run.StartPosition > TimeSpan.Zero) options.Seek(run.StartPosition);
            };
            var arguments = run.Source.IsRemote
                ? FFMpegArguments.FromUrlInput(new Uri(run.Source.Input), configureInput)
                : FFMpegArguments.FromFileInput(run.Source.Input, true, configureInput);
            // Own the named pipe ourselves: FFMpegCore's OutputToPipe cancels its copy and
            // closes the pipe as soon as the process exits, before slow final PCM is drained.
            run.CopyTask = CopyPcmAsync(run);
            var processor = arguments.OutputToFile(run.OutputPipePath, true, options =>
            {
                options.DisableChannel(Channel.Video).ForceFormat("f32le").WithAudioCodec("pcm_f32le")
                    .WithAudioSamplingRate(48000).WithCustomArgument("-ac 2 -map 0:a:0");
                if (run.Source.IsPreview && run.Source.Duration is { } previewDuration)
                {
                    var remaining = previewDuration - run.StartPosition;
                    if (remaining <= TimeSpan.Zero) throw new InvalidOperationException("已到试听片段末尾。");
                    // Enforce the provider's playable duration even if its URL returns a full song.
                    options.WithDuration(remaining);
                }
            });
            if (_decoderDiagnostic is not null)
            {
                PublishDecoderDiagnostic(run.Source, processor.Arguments);
                processor.NotifyOnError(line => PublishDecoderDiagnostic(run.Source, line));
            }
            // timeout=0 sends q and immediately kills this process if its network/pipe blocks.
            processor.CancellableThrough(out var forceCancel, 0).CancellableThrough(token, 0);
            run.ForceCancel = forceCancel;
            var success = await processor.ProcessAsynchronously().ConfigureAwait(false);
            if (!success) throw new InvalidOperationException("FFmpeg 解码异常退出。");
            token.ThrowIfCancellationRequested();
            await run.CopyTask.ConfigureAwait(false);
            await run.ConsumerTask.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (Interlocked.Read(ref run.PcmBytesReceived) == 0)
                throw new InvalidOperationException("音频源没有返回可播放的数据。");
            run.Provider.Complete();
            run.IsEof = true;
            run.Ready.TrySetResult();
        }
        catch (Exception ex) when (token.IsCancellationRequested)
        {
            run.Pipe.Complete();
            run.Ready.TrySetCanceled(token);
            Log.Debug("FFmpeg decoder cancelled for session {SessionId}: {Type}", run.Source.SessionId, ex.GetType().Name);
        }
        catch (Exception ex)
        {
            var error = SanitizeDecodeError(run.Source, ex);
            run.Fail(error);
            CancelDecoder(run);
            run.Pipe.Complete(error);
            Log.Warning("Audio decode failed for session {SessionId}: {Message}", run.Source.SessionId, error.Message);
        }
        finally
        {
            run.Pipe.Complete();
            try { await run.CopyTask.ConfigureAwait(false); }
            catch (Exception) when (token.IsCancellationRequested || run.Error is not null) { }
            try { await run.ConsumerTask.ConfigureAwait(false); }
            catch (Exception) when (token.IsCancellationRequested || run.Error is not null) { }
        }
    }

    private async Task CopyPcmAsync(DecoderRun run)
    {
        try
        {
            await run.OutputPipe.WaitForConnectionAsync(run.Cancellation.Token).ConfigureAwait(false);
            await run.OutputPipe.CopyToAsync(run.Pipe, 32 * 1024, run.Cancellation.Token).ConfigureAwait(false);
            run.Pipe.Complete();
        }
        catch (Exception ex) when (!run.Cancellation.IsCancellationRequested)
        {
            run.Fail(SanitizeDecodeError(run.Source, ex));
            CancelDecoder(run);
            run.Pipe.Complete(run.Error);
            throw;
        }
        finally { run.Pipe.Complete(); }
    }

    private async Task ConsumePcmAsync(DecoderRun run)
    {
        var token = run.Cancellation.Token;
        try
        {
            await foreach (var block in run.Pipe.Reader.ReadAllAsync(token).ConfigureAwait(false))
            {
                while (!run.Provider.TryAddSamples(block)) await Task.Delay(15, token).ConfigureAwait(false);
                Interlocked.Add(ref _totalPcmBytesReceived, block.Length);
                Interlocked.Add(ref run.PcmBytesReceived, block.Length);
                _spectrum.PushPcm(block);
                if (run.Provider.BufferedBytes >= StartupBufferBytes) run.Ready.TrySetResult();
            }
        }
        catch (Exception ex) when (!token.IsCancellationRequested)
        {
            run.Fail(SanitizeDecodeError(run.Source, ex));
            run.Cancellation.Cancel();
            throw;
        }
    }

    private async Task StopCoreAsync(bool clearFile)
    {
        _wantsPlayback = false;
        await StopDecoderAsync().ConfigureAwait(false);
        _basePosition = TimeSpan.Zero;
        if (clearFile) { _source = null; Duration = TimeSpan.Zero; }
        SetState(PlaybackState.Stopped);
        PublishPosition();
    }

    private async Task StopDecoderAsync()
    {
        var run = _run;
        if (run is null) return;
        CancelDecoder(run);
        run.Pipe.Complete();
        try
        {
            try { run.Output.Stop(); }
            catch (Exception ex) { Log.Debug("Audio output stop: {Type}", ex.GetType().Name); }
            try { await run.DecodeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
            catch (TimeoutException)
            {
                // Keep ownership until exit. The retry also covers cancellation racing with
                // the library subscribing its process kill handler during process startup.
                run.ForceCancel?.Invoke();
                await run.DecodeTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            await run.ConsumerTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TimeoutException)
        {
            Log.Debug("Decoder stopped for session {SessionId}: {Type}", run.Source.SessionId, ex.GetType().Name);
            if (!run.DecodeTask.IsCompleted || !run.ConsumerTask.IsCompleted || !run.CopyTask.IsCompleted)
                throw new TimeoutException("旧 FFmpeg 解码任务仍在退出，无法开始新音频。", ex);
        }
        finally
        {
            run.Provider.ClearBuffer();
            if (run.DecodeTask.IsCompleted && run.ConsumerTask.IsCompleted && run.CopyTask.IsCompleted)
            {
                if (ReferenceEquals(_run, run)) _run = null;
                run.Dispose();
            }
            else _ = ReleaseAfterExitAsync(run);
        }
    }

    private async Task ReleaseAfterExitAsync(DecoderRun run)
    {
        try
        {
            while (!run.DecodeTask.IsCompleted)
            {
                try { run.ForceCancel?.Invoke(); }
                catch (Exception ex) { Log.Debug("Decoder forced cancellation: {Type}", ex.GetType().Name); }
                await Task.WhenAny(run.DecodeTask, Task.Delay(250)).ConfigureAwait(false);
            }
            try { await Task.WhenAll(run.DecodeTask, run.ConsumerTask, run.CopyTask).ConfigureAwait(false); }
            catch (Exception) { }
        }
        finally
        {
            Interlocked.CompareExchange(ref _run, null, run);
            run.Dispose();
        }
    }

    private async Task RunPositionTimerAsync(CancellationToken token)
    {
        try
        {
            while (await _positionTimer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (!await _gate.WaitAsync(0, token).ConfigureAwait(false)) continue;
                try
                {
                    if (_disposed) continue;
                    if (_run?.Error is { } error) { ReportFailure(error); continue; }
                    UpdatePlayback();
                    if (State is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering)
                        PublishPosition();
                }
                catch (Exception ex) { ReportFailure(ex); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }

    private void UpdatePlayback()
    {
        var run = _run;
        if (run is null || State == PlaybackState.Error) return;
        if (run.Error is { } error) { ReportFailure(error); return; }
        if (!_wantsPlayback) { if (!run.Completed) SetState(PlaybackState.Paused); return; }
        var outputState = run.Output.PlaybackState;
        if (run.IsEof && run.Provider.BufferedBytes == 0 && outputState == OutputPlaybackState.Stopped)
        {
            if (run.Completed || run.PcmBytesReceived == 0 || run.Provider.SubmittedMediaBytes == 0) return;
            // Natural WaveOut stop happens after all final hardware buffers have drained.
            run.Completed = true;
            _wantsPlayback = false;
            _basePosition = run.StartPosition + TimeSpan.FromSeconds(run.PcmBytesReceived / (double)OutputFormat.AverageBytesPerSecond);
            Duration = _basePosition;
            SetState(PlaybackState.Stopped);
            PublishPosition();
            PlaybackCompleted?.Invoke(this, EventArgs.Empty);
            SessionPlaybackCompleted?.Invoke(this, new AudioSessionCompletion(SessionId, run.Source.IsPreview));
            return;
        }
        if (outputState != OutputPlaybackState.Playing)
        {
            if (run.Provider.BufferedBytes >= StartupBufferBytes || (run.IsEof && run.Provider.BufferedBytes > 0) || outputState == OutputPlaybackState.Paused)
            {
                run.Output.Play();
                SetState(PlaybackState.Playing);
            }
            else SetState(PlaybackState.Buffering);
        }
        else SetState(PlaybackState.Playing);
    }

    private void ThrowIfRunFailed() { if (_run?.Error is { } error) throw error; }
    private void ReportFailure(Exception error)
    {
        _wantsPlayback = false;
        _basePosition = Position;
        if (_run is { } run)
        {
            CancelDecoder(run);
            run.Pipe.Complete();
        }
        try { _run?.Output.Stop(); }
        catch (Exception) { }
        SetState(PlaybackState.Error);
        if (_lastFailureSession == SessionId) return;
        _lastFailureSession = SessionId;
        PlaybackFailed?.Invoke(this, error);
        SessionPlaybackFailed?.Invoke(this, new AudioSessionFailure(SessionId, error));
    }
    private void SetState(PlaybackState state)
    {
        if ((state is PlaybackState.Playing or PlaybackState.Paused) && _run?.Error is { } error)
        {
            ReportFailure(error);
            return;
        }
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
        SessionStateChanged?.Invoke(this, new AudioSessionState(SessionId, state));
    }
    private void PublishPosition()
    {
        var position = Position;
        PositionChanged?.Invoke(this, position);
        SessionPositionChanged?.Invoke(this, new AudioSessionPosition(SessionId, position));
    }
    private TimeSpan ClampPosition(TimeSpan value) => value < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && value > Duration ? Duration : value;
    private void ApplyVolume()
    {
        try { if (_run is { } run) run.Output.Volume = _isMuted ? 0 : _volume; }
        catch (ObjectDisposedException) { }
    }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }

    private static void CancelDecoder(DecoderRun run)
    {
        try { run.Cancellation.Cancel(); }
        catch (Exception ex)
        {
            // Cancellation callbacks can race with process exit; still execute the explicit
            // hard cancellation and preserve the run until its tasks finish.
            Log.Debug("Decoder cancellation callback: {Type}", ex.GetType().Name);
            try { run.ForceCancel?.Invoke(); }
            catch (Exception) { }
        }
    }

    private static void ValidateSource(AudioSource source)
    {
        if (source.IsPreview && (!source.Duration.HasValue || source.Duration.Value <= TimeSpan.Zero))
            throw new InvalidOperationException("试听片段长度未知，请重新获取播放地址。");
        if (source.IsRemote)
        {
            if (!Uri.TryCreate(source.Input, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ArgumentException("在线音频地址必须使用 HTTP 或 HTTPS。", nameof(source));
            _ = BuildHeaders(source.Headers);
        }
        else if (!File.Exists(source.Input)) throw new FileNotFoundException("音乐文件不存在。", source.Input);
    }
    private static string BuildHeaders(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return string.Empty;
        var builder = new StringBuilder();
        foreach (var (name, value) in headers)
        {
            if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch))) || value.Any(ch => ch is '\r' or '\n' or '\0'))
                throw new ArgumentException("在线音频请求头格式无效。");
            builder.Append(name).Append(": ").Append(value).Append("\r\n");
        }
        return builder.ToString();
    }
    private static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { backslashes++; continue; }
            result.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(character);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }
    private static Exception SanitizeDecodeError(AudioSource source, Exception error)
    {
        if (!source.IsRemote) return error;
        // FFmpeg exceptions can contain signed URLs and request headers. Expose only a
        // recognized HTTP status hint, never persist their original text or inner exception.
        var message = error.ToString();
        var status = new[] { "401", "403", "404", "410", "429", "500", "502", "503" }
            .FirstOrDefault(code => message.Contains(" " + code, StringComparison.Ordinal));
        return new InvalidOperationException(status is null
            ? "在线音频解码失败，请检查网络或重新获取播放地址。"
            : $"在线音频链接无法访问（HTTP {status}），请重新获取播放地址。");
    }

    private void PublishDecoderDiagnostic(AudioSource source, string text)
    {
        text = text.Replace(source.Input, "<audio-source>", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, @"https?://[^\s\""']+", "<media-url>");
        if (source.Headers is not null)
            foreach (var value in source.Headers.Values.Where(value => value.Length > 0))
                text = text.Replace(value, "<header-value>", StringComparison.Ordinal);
        try { _decoderDiagnostic?.Invoke(text.Length > 2048 ? text[..2048] : text); }
        catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeCts) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        _disposed = true;
        try { _lifetimeCts.Cancel(); }
        catch (Exception ex)
        {
            Log.Debug("Audio lifetime cancellation callback: {Type}", ex.GetType().Name);
            if (_run is { } run) CancelDecoder(run);
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync(true).ConfigureAwait(false); }
        finally
        {
            try { _run?.Output.Dispose(); }
            finally
            {
                _gate.Release();
                try { await _positionTask.ConfigureAwait(false); }
                finally { _positionTimer.Dispose(); _lifetimeCts.Dispose(); }
            }
        }
        // Keep the gate valid for public requests that were already queued before disposal.
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class DecoderRun : IDisposable
    {
        private int _disposed;
        private Exception? _error;
        public DecoderRun(AudioSource source, TimeSpan startPosition, WaveFormat format, IWavePlayer output, CancellationToken lifetime)
        {
            Source = source;
            StartPosition = startPosition;
            Output = output;
            Provider = new StreamingPcmProvider(format, format.AverageBytesPerSecond * 5);
            Cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Pipe = new BoundedPcmStream(Cancellation.Token);
            var pipeName = "NekoPlayerAudio-" + Guid.NewGuid().ToString("N");
            OutputPipePath = @"\\.\pipe\" + pipeName;
            OutputPipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }
        public AudioSource Source { get; }
        public TimeSpan StartPosition { get; }
        public IWavePlayer Output { get; }
        public StreamingPcmProvider Provider { get; }
        public CancellationTokenSource Cancellation { get; }
        public BoundedPcmStream Pipe { get; }
        public string OutputPipePath { get; }
        public NamedPipeServerStream OutputPipe { get; }
        public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task DecodeTask { get; set; } = Task.CompletedTask;
        public Task ConsumerTask { get; set; } = Task.CompletedTask;
        public Task CopyTask { get; set; } = Task.CompletedTask;
        public Action? ForceCancel { get; set; }
        public long PcmBytesReceived;
        public volatile bool IsEof;
        public bool Completed;
        public Exception? Error => Volatile.Read(ref _error);
        public void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref _error, error, null);
            Ready.TrySetException(Error!);
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Output.Dispose(); }
            finally { Pipe.Dispose(); OutputPipe.Dispose(); Cancellation.Dispose(); }
        }
    }
}
