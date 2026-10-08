using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Serilog;

namespace NekoPlayer.App.Services;

public enum InstanceCommand { Activate, ShutdownForUpdate }
public enum InstanceExitCode { Success = 0, Timeout = 2, IpcError = 3, InvalidArguments = 4 }

/// <summary>
/// One instance per current Windows user and normalized data root. The pipe accepts two
/// fixed commands only and cannot open files, launch programs, or execute arbitrary text.
/// </summary>
public sealed class SingleInstanceCoordinator : IDisposable
{
    private const string ActivateCommand = "activate";
    private const string ShutdownCommand = "shutdown-for-update";
    private const int MaximumMessageLength = 64;
    private readonly Mutex _mutex;
    private readonly ManualResetEventSlim _releaseMutex = new();
    private readonly Thread _mutexThread;
    private readonly CancellationTokenSource _serverCancellation = new();
    private Task? _serverTask;
    private Func<InstanceCommand, Task<bool>>? _commandHandler;
    private bool _disposed;

    public SingleInstanceCoordinator(string dataRoot)
    {
        InstanceKey = BuildInstanceKey(dataRoot, CurrentUserKey());
        PipeName = $"NekoPlayer-v120-{InstanceKey}";
        _mutex = new Mutex(false, $"Local\\NekoPlayer-v120-{InstanceKey}");
        using var ready = new ManualResetEventSlim();
        Exception? startupError = null;
        // A dedicated owner thread permits safe release after async work and prevents
        // another coordinator in this process from recursively taking the same mutex.
        _mutexThread = new Thread(() =>
        {
            try
            {
                try { IsPrimary = _mutex.WaitOne(0); }
                catch (AbandonedMutexException) { IsPrimary = true; }
            }
            catch (Exception ex) { startupError = ex; }
            finally { ready.Set(); }
            if (!IsPrimary) return;
            try { _releaseMutex.Wait(); }
            finally { _mutex.ReleaseMutex(); }
        }) { IsBackground = true, Name = "NekoPlayer single-instance ownership" };
        _mutexThread.Start();
        ready.Wait();
        if (startupError is not null)
        {
            _mutex.Dispose();
            _releaseMutex.Dispose();
            _serverCancellation.Dispose();
            throw new InvalidOperationException("无法建立播放器实例锁。", startupError);
        }
    }

    public bool IsPrimary { get; private set; }
    public string InstanceKey { get; }
    public string PipeName { get; }

    public static string BuildInstanceKey(string dataRoot, string userKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(userKey);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
        if (OperatingSystem.IsWindows()) root = root.Replace('/', '\\').ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{userKey}\n{root}"))).ToLowerInvariant();
    }

    public static bool TryParseCommand(string? message, out InstanceCommand command)
    {
        command = default;
        if (message == ActivateCommand) return true;
        if (message != ShutdownCommand) return false;
        command = InstanceCommand.ShutdownForUpdate;
        return true;
    }

    public void StartServer(Func<InstanceCommand, Task<bool>> handler)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsPrimary) throw new InvalidOperationException("只有主实例可以接收命令。");
        if (_serverTask is not null) throw new InvalidOperationException("实例命令接收器已经启动。");
        _commandHandler = handler ?? throw new ArgumentNullException(nameof(handler));
        _serverTask = Task.Run(() => ServeAsync(_serverCancellation.Token));
    }

    public async Task<InstanceExitCode> ActivateExistingAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsPrimary) return InstanceExitCode.Success;
        return await SendCommandAsync(ActivateCommand, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InstanceExitCode> ExitExistingForUpdateAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsPrimary) return InstanceExitCode.Success;
        using var commandCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var command = SendCommandAsync(ShutdownCommand, timeout, commandCancellation.Token);
        var exited = WaitForInstanceExitAsync(timeout, cancellationToken);
        // Only releasing the process lifetime mutex proves that save and host disposal
        // finished. The IPC connection can close while successful shutdown is in flight.
        // Observe the mutex concurrently so an instance already closing needs no pipe reply.
        if (await Task.WhenAny(command, exited).ConfigureAwait(false) == exited)
        {
            var hasExited = await exited.ConfigureAwait(false);
            commandCancellation.Cancel();
            var cancelledCommandResult = await command.ConfigureAwait(false);
            return hasExited ? InstanceExitCode.Success
                : cancelledCommandResult == InstanceExitCode.IpcError ? InstanceExitCode.IpcError : InstanceExitCode.Timeout;
        }
        var result = await command.ConfigureAwait(false);
        if (await exited.ConfigureAwait(false)) return InstanceExitCode.Success;
        return result == InstanceExitCode.IpcError ? result : InstanceExitCode.Timeout;
    }

    public Task<bool> WaitForInstanceExitAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsPrimary) return Task.FromResult(true);
        return Task.Run(() =>
        {
            var acquired = false;
            try
            {
                var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
                acquired = WaitHandle.WaitAny([_mutex, cancellationToken.WaitHandle], milliseconds) == 0;
                return acquired;
            }
            catch (AbandonedMutexException) { acquired = true; return true; }
            finally { if (acquired) _mutex.ReleaseMutex(); }
        }, CancellationToken.None);
    }

    private async Task<InstanceExitCode> SendCommandAsync(string command, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.Zero);
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
            await writer.WriteLineAsync(command.AsMemory(), deadline.Token).ConfigureAwait(false);
            var response = await ReadMessageAsync(reader, deadline.Token).ConfigureAwait(false);
            return response == "ok" ? InstanceExitCode.Success : InstanceExitCode.IpcError;
        }
        catch (OperationCanceledException) { return InstanceExitCode.Timeout; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(ex, "向现有播放器实例发送命令失败");
            return InstanceExitCode.IpcError;
        }
    }

    private async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var connectionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                connectionTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                using var reader = new StreamReader(pipe, Encoding.UTF8, false, 128, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 128, leaveOpen: true) { AutoFlush = true };
                var message = await ReadMessageAsync(reader, connectionTimeout.Token).ConfigureAwait(false);
                var accepted = TryParseCommand(message, out var command) && await _commandHandler!(command).ConfigureAwait(false);
                await writer.WriteLineAsync((accepted ? "ok" : "rejected").AsMemory(), connectionTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Log.Warning(ex, "实例命令接收失败，继续等待下一条命令"); }
        }
    }

    private static async Task<string?> ReadMessageAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var message = new StringBuilder(MaximumMessageLength);
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            if (character[0] == '\n') return message.ToString();
            if (character[0] == '\r')
                return await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) == 1 && character[0] == '\n'
                    ? message.ToString() : null;
            if (message.Length >= MaximumMessageLength) return null;
            message.Append(character[0]);
        }
        return null;
    }

    private static string CurrentUserKey()
    {
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? identity.Name;
        }
        return $"{Environment.UserDomainName}\\{Environment.UserName}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _serverCancellation.Cancel();
        _releaseMutex.Set();
        _mutexThread.Join();
        _mutex.Dispose();
        _releaseMutex.Dispose();
        if (_serverTask is null || _serverTask.IsCompleted) _serverCancellation.Dispose();
        else _ = _serverTask.ContinueWith(_ => _serverCancellation.Dispose(), TaskScheduler.Default);
    }
}
