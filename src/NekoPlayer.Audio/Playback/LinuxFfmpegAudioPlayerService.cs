using System.Diagnostics;
using System.Globalization;
using FFMpegCore;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using Serilog;

namespace NekoPlayer.Audio.Playback;

public sealed class LinuxFfmpegAudioPlayerService : IAudioPlayerService, IAudioPlaybackDiagnostics, IDisposable
{
    public const string AudioDeviceEnvironmentVariable = "NEKOPLAYER_LINUX_AUDIO_DEVICE";
    public const string NullAudioDevice = "null";
    private const long EstimatedBytesPerSecond = 48000L * 2 * 2;

    private readonly IFfmpegLocator _ffmpeg;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PeriodicTimer _positionTimer = new(TimeSpan.FromMilliseconds(150));
    private readonly CancellationTokenSource _lifetimeCts = new();
    private Process? _process;
    private CancellationTokenSource? _processCts;
    private Task? _monitorTask;
    private string? _filePath;
    private TimeSpan _basePosition;
    private DateTime _playStartedUtc;
    private float _volume = 0.75f;
    private float _volumeBeforeMute = 0.75f;
    private bool _isMuted;
    private bool _disposed;
    private long _completedOutputBytes;

    public LinuxFfmpegAudioPlayerService(IFfmpegLocator ffmpeg)
    {
        _ffmpeg = ffmpeg;
        _ = RunPositionTimerAsync(_lifetimeCts.Token);
    }

    public PlaybackState State { get; private set; } = PlaybackState.Idle;
    public TimeSpan Position => ClampPosition(State == PlaybackState.Playing ? _basePosition + (DateTime.UtcNow - _playStartedUtc) : _basePosition);
    public TimeSpan Duration { get; private set; }
    public int BufferedBytes => 0;
    public int BufferCapacityBytes => 0;
    public long TotalPcmBytesReceived => Interlocked.Read(ref _completedOutputBytes) + (State == PlaybackState.Playing ? Math.Max(0, (long)((DateTime.UtcNow - _playStartedUtc).TotalSeconds * EstimatedBytesPerSecond)) : 0);
    public bool IsOutputInitialized => _process is { HasExited: false };
    public bool IsDecodeActive => _process is { HasExited: false };
    public string? CurrentFilePath => _filePath;

    public float Volume
    {
        get => _volume;
        set
        {
            var next = Math.Clamp(value, 0f, 1f);
            if (Math.Abs(_volume - next) < 0.001f) return;
            _volume = next;
            if (!_isMuted) _volumeBeforeMute = next;
            if (State == PlaybackState.Playing) _ = RestartForOutputSettingChangeAsync();
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
            if (State == PlaybackState.Playing) _ = RestartForOutputSettingChangeAsync();
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
            await StopProcessAsync();
            _filePath = Path.GetFullPath(filePath);
            var analysis = await FFProbe.AnalyseAsync(_filePath, cancellationToken: cancellationToken);
            Duration = analysis.Duration;
            _basePosition = ClampPosition(startPosition ?? TimeSpan.Zero);
            SetState(PlaybackState.Paused);
            PositionChanged?.Invoke(this, _basePosition);
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
            if (_filePath is null || State == PlaybackState.Playing) return;
            await StartProcessAsync(_basePosition, cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State != PlaybackState.Playing) return;
            CaptureElapsedOutput();
            await StopProcessAsync();
            SetState(PlaybackState.Paused);
            PositionChanged?.Invoke(this, _basePosition);
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == PlaybackState.Playing) CaptureElapsedOutput();
            await StopProcessAsync();
            _basePosition = TimeSpan.Zero;
            SetState(PlaybackState.Stopped);
            PositionChanged?.Invoke(this, TimeSpan.Zero);
        }
        finally { _gate.Release(); }
    }

    public async Task UnloadAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (State == PlaybackState.Playing) CaptureElapsedOutput();
            await StopProcessAsync();
            _filePath = null;
            Duration = TimeSpan.Zero;
            _basePosition = TimeSpan.Zero;
            SetState(PlaybackState.Stopped);
            PositionChanged?.Invoke(this, TimeSpan.Zero);
        }
        finally { _gate.Release(); }
    }

    public async Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_filePath is null) return;
            var resume = State == PlaybackState.Playing;
            if (resume) CaptureElapsedOutput();
            SetState(PlaybackState.Seeking);
            await StopProcessAsync();
            _basePosition = ClampPosition(position);
            if (resume) await StartProcessAsync(_basePosition, cancellationToken);
            else
            {
                SetState(PlaybackState.Paused);
                PositionChanged?.Invoke(this, _basePosition);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SetState(PlaybackState.Error);
            PlaybackFailed?.Invoke(this, ex);
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task StartProcessAsync(TimeSpan startPosition, CancellationToken cancellationToken)
    {
        await StopProcessAsync();
        var device = Environment.GetEnvironmentVariable(AudioDeviceEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(device)) device = "default";

        var startInfo = CreateProcessStartInfo(_ffmpeg.FfmpegPath, _filePath!, startPosition, _isMuted ? 0f : _volume, device);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("无法启动 Linux FFmpeg 音频输出进程。");

        _process = process;
        _processCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCts.Token, cancellationToken);
        _basePosition = startPosition;
        _playStartedUtc = DateTime.UtcNow;
        SetState(PlaybackState.Playing);
        _monitorTask = MonitorProcessAsync(process, _processCts.Token);
        await Task.Delay(120, cancellationToken);
        if (process.HasExited) throw new InvalidOperationException("Linux FFmpeg 音频输出进程启动后立即退出，请检查音频设备和 FFmpeg 构建。 ");
    }

    public static ProcessStartInfo CreateProcessStartInfo(string ffmpegPath, string filePath, TimeSpan startPosition, float volume, string audioDevice)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = false
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-re" }) startInfo.ArgumentList.Add(argument);
        if (startPosition > TimeSpan.Zero)
        {
            startInfo.ArgumentList.Add("-ss");
            startInfo.ArgumentList.Add(startPosition.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture));
        }
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add(Path.GetFullPath(filePath));
        foreach (var argument in new[] { "-vn", "-sn", "-dn", "-af", $"volume={Math.Clamp(volume, 0f, 1f).ToString("0.###", CultureInfo.InvariantCulture)}" }) startInfo.ArgumentList.Add(argument);
        if (string.Equals(audioDevice, NullAudioDevice, StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("null");
            startInfo.ArgumentList.Add("-");
        }
        else
        {
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("alsa");
            startInfo.ArgumentList.Add(audioDevice);
        }
        return startInfo;
    }

    private async Task MonitorProcessAsync(Process process, CancellationToken token)
    {
        try
        {
            var errorTask = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            var error = await errorTask;
            if (token.IsCancellationRequested) return;

            await _gate.WaitAsync(token);
            try
            {
                if (!ReferenceEquals(_process, process)) return;
                var exitCode = process.ExitCode;
                CaptureElapsedOutput();
                _basePosition = Duration;
                DisposeCurrentProcess();
                if (exitCode == 0)
                {
                    SetState(PlaybackState.Stopped);
                    PositionChanged?.Invoke(this, Duration);
                    PlaybackCompleted?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    var ex = new InvalidOperationException($"Linux FFmpeg 音频输出异常退出（{exitCode}）：{error.Trim()}");
                    SetState(PlaybackState.Error);
                    PlaybackFailed?.Invoke(this, ex);
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Linux FFmpeg playback monitor failed");
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        var cts = _processCts;
        _process = null;
        _processCts = null;
        _monitorTask = null;
        if (process is null)
        {
            cts?.Dispose();
            return;
        }

        cts?.Cancel();
        try
        {
            if (!process.HasExited)
            {
                try
                {
                    await process.StandardInput.WriteLineAsync("q");
                    await process.StandardInput.FlushAsync();
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException)
                {
                    if (!process.HasExited) process.Kill(true);
                }
            }
        }
        finally
        {
            process.Dispose();
            cts?.Dispose();
        }
    }

    private async Task RestartForOutputSettingChangeAsync()
    {
        try
        {
            await _gate.WaitAsync();
            try
            {
                if (State != PlaybackState.Playing || _filePath is null) return;
                CaptureElapsedOutput();
                await StopProcessAsync();
                await StartProcessAsync(_basePosition, _lifetimeCts.Token);
            }
            finally { _gate.Release(); }
        }
        catch (Exception ex) { Log.Warning(ex, "Linux audio output setting restart failed"); }
    }

    private void CaptureElapsedOutput()
    {
        if (State != PlaybackState.Playing) return;
        var elapsed = DateTime.UtcNow - _playStartedUtc;
        _basePosition = ClampPosition(_basePosition + elapsed);
        Interlocked.Add(ref _completedOutputBytes, Math.Max(0, (long)(elapsed.TotalSeconds * EstimatedBytesPerSecond)));
        _playStartedUtc = DateTime.UtcNow;
    }

    private void DisposeCurrentProcess()
    {
        _process?.Dispose();
        _process = null;
        _processCts?.Dispose();
        _processCts = null;
        _monitorTask = null;
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
    private void SetState(PlaybackState state) { if (State == state) return; State = state; StateChanged?.Invoke(this, state); }
    private void ThrowIfDisposed() { ObjectDisposedException.ThrowIf(_disposed, this); }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetimeCts.Cancel();
        await _gate.WaitAsync();
        try { await StopProcessAsync(); }
        finally { _gate.Release(); }
        _positionTimer.Dispose();
        _lifetimeCts.Dispose();
        _gate.Dispose();
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}
