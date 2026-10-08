using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using Serilog;

namespace NekoPlayer.Infrastructure.Online;

public sealed class GatewayRuntimeOptions
{
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;
    public string? NodeExecutablePath { get; init; }
    public string? GatewayScriptPath { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(10);
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Owns one authenticated, loopback-only gateway. Caller cancellation never cancels shared startup.</summary>
public sealed class GatewayRuntime : IGatewayRuntime
{
    private const int MaximumResponseBytes = 16 * 1024 * 1024;
    private readonly IUserDataPaths _paths;
    private readonly GatewayRuntimeOptions _options;
    private readonly object _sync = new();
    // Auth and music requests share the gateway's four upstream slots.
    private readonly SemaphoreSlim _requests = new(4, 4);
    private readonly HashSet<Connection> _ownedConnections = [];
    private Task<Connection>? _startup;
    private CancellationTokenSource? _startupCancellation;
    private Task? _stopTask;
    private Connection? _connection;
    private GatewayState _state;
    private string? _lastError;
    private long _generation;
    private bool _disposed;

    public GatewayRuntime(IUserDataPaths paths) : this(paths, new GatewayRuntimeOptions()) { }
    public GatewayRuntime(IUserDataPaths paths, GatewayRuntimeOptions options)
    {
        _paths = paths;
        _options = options;
    }

    public GatewayState State { get { lock (_sync) return _state; } }
    public string? LastError { get { lock (_sync) return _lastError; } }

    public async Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await GetConnectionAsync(cancellationToken).ConfigureAwait(false)).Address;
    }

    private async Task<Connection> GetConnectionAsync(CancellationToken cancellationToken)
    {
        Task<Connection> startup;
        Connection? stale = null;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopTask is not null)
                throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_stopping", "在线音乐服务正在停止");
            if (_connection is { } ready)
            {
                if (!ready.Process.HasExited) return ready;
                stale = ready;
                _connection = null;
            }
            if (_startup is null || _startup.IsCompleted)
            {
                _startupCancellation?.Dispose();
                _startupCancellation = new CancellationTokenSource();
                _state = GatewayState.Starting;
                _lastError = null;
                _startup = StartGatewayAsync(++_generation, _startupCancellation.Token);
            }
            startup = _startup;
        }
        if (stale is not null) await DisposeOwnedConnectionAsync(stale).ConfigureAwait(false);
        // A cancelled UI query only leaves its wait; another query still receives the same startup.
        return await startup.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(route) || !route.StartsWith('/') || route.StartsWith("//", StringComparison.Ordinal) ||
            route.Contains('\\') || !Uri.TryCreate(route, UriKind.Relative, out _))
            throw new ArgumentException("Gateway routes must be relative paths beginning with '/'.", nameof(route));
        var connection = await GetConnectionAsync(cancellationToken).ConfigureAwait(false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connection.Cancellation.Token);
        timeout.CancelAfter(_options.RequestTimeout);
        var acquired = false;
        try
        {
            await _requests.WaitAsync(timeout.Token).ConfigureAwait(false);
            acquired = true;
            return await SendCoreAsync(connection, route, payload, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException ex) when (!connection.Cancellation.IsCancellationRequested)
        {
            throw new GatewayException(GatewayFailureKind.Timeout, "gateway_timeout", "在线音乐请求超时，请稍后重试", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_unavailable", "在线音乐服务暂时不可用，本地播放不受影响", ex);
        }
        finally { if (acquired) _requests.Release(); }
    }

    private async Task<Connection> StartGatewayAsync(long generation, CancellationToken cancellationToken)
    {
        // Leave the ownership lock before process or filesystem work.
        await Task.Yield();
        Connection? connection = null;
        Process? process = null;
        WindowsGatewayJob? job = null;
        string? runDirectory = null;
        var gatewayDirectory = Path.GetFullPath(Path.Combine(_paths.TempDirectory, "Gateway"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            var (node, script) = LocateGateway();
            script = Path.GetFullPath(script);
            runDirectory = Path.Combine(gatewayDirectory, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runDirectory);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var startInfo = new ProcessStartInfo(node)
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
                // SDK scratch files and cwd handles belong to this connection, never to the installation.
                WorkingDirectory = runDirectory
            };
            startInfo.ArgumentList.Add(script);
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            if (!process.Start()) throw new IOException("Unable to start gateway process.");
            job = WindowsGatewayJob.Attach(process);
            connection = new Connection(process, job, token, runDirectory, gatewayDirectory);
            runDirectory = null;
            process = null;
            job = null;
            connection.StandardErrorDrain = DrainAsync(connection.Process.StandardError, connection, retainDiagnostics: true);
            var ownedConnection = connection;
            connection.Process.Exited += (_, _) => OnProcessExited(generation, ownedConnection);
            lock (_sync) _ownedConnections.Add(connection);
            await connection.Process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            { token, dataDirectory = connection.RunDirectory, port = 0 }).AsMemory(), timeout.Token).ConfigureAwait(false);
            await connection.Process.StandardInput.FlushAsync(timeout.Token).ConfigureAwait(false);
            var line = await ReadHandshakeAsync(connection.Process.StandardOutput, timeout.Token).ConfigureAwait(false);
            using (var handshake = JsonDocument.Parse(line))
            {
                var root = handshake.RootElement;
                if (!root.TryGetProperty("ready", out var ready) || ready.ValueKind != JsonValueKind.True ||
                    !root.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var protocol) || protocol != 1 ||
                    !root.TryGetProperty("port", out var portValue) || portValue.ValueKind != JsonValueKind.Number || !portValue.TryGetInt32(out var port) || port is <= 0 or > 65535)
                    throw new GatewayException(GatewayFailureKind.Protocol, "gateway_handshake", "在线音乐服务协议不兼容");
                connection.Address = new Uri($"http://127.0.0.1:{port}/");
            }
            connection.StandardOutputDrain = DrainAsync(connection.Process.StandardOutput, connection, retainDiagnostics: false);
            await SendCoreAsync(connection, "/health", null, timeout.Token).ConfigureAwait(false);
            lock (_sync)
            {
                if (_disposed || generation != _generation || cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);
                if (connection.Process.HasExited)
                    throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_exited", "在线音乐服务已退出");
                _connection = connection;
                _state = GatewayState.Ready;
                _lastError = null;
            }
            return connection;
        }
        catch (Exception ex)
        {
            if (connection is not null) await DisposeOwnedConnectionAsync(connection).ConfigureAwait(false);
            if (process is not null)
            {
                try
                {
                    if (job is not null) await job.TerminateAndWaitAsync().ConfigureAwait(false);
                    else if (!process.HasExited) process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                }
                finally { job?.Dispose(); job = null; process.Dispose(); }
            }
            job?.Dispose();
            if (runDirectory is not null) await RemoveOwnedRunDirectoryAsync(runDirectory, gatewayDirectory).ConfigureAwait(false);
            var failure = ex as GatewayException ?? new GatewayException(
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested ? GatewayFailureKind.Timeout : GatewayFailureKind.Unavailable,
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested ? "gateway_start_timeout" : "gateway_start_failed",
                ex is OperationCanceledException && !cancellationToken.IsCancellationRequested ? "在线音乐服务启动超时，本地播放不受影响" : "在线音乐服务无法启动，本地播放不受影响", ex);
            lock (_sync)
            {
                if (generation == _generation)
                {
                    _state = GatewayState.Unavailable;
                    _lastError = failure.Message;
                }
            }
            Log.Warning("在线音乐网关不可用：{Code}", failure.Code);
            throw failure;
        }
    }

    private void OnProcessExited(long generation, Connection connection)
    {
        lock (_sync)
        {
            if (generation == _generation && ReferenceEquals(_connection, connection))
            {
                _connection = null;
                _state = GatewayState.Unavailable;
                _lastError = "在线音乐服务已退出，请重试；本地播放不受影响";
            }
        }
        _ = ObserveExitCleanupAsync(connection);
    }

    private async Task ObserveExitCleanupAsync(Connection connection)
    {
        try { await DisposeOwnedConnectionAsync(connection).ConfigureAwait(false); }
        catch (Exception ex) when (ex is GatewayException or System.ComponentModel.Win32Exception or IOException or TimeoutException)
        { Log.Warning("在线音乐网关退出清理失败"); }
    }

    private async Task DisposeOwnedConnectionAsync(Connection connection)
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        // A failed cleanup stays owned so Stop cannot silently omit it and claim successful shutdown.
        lock (_sync) _ownedConnections.Remove(connection);
    }

    private static async Task RemoveOwnedRunDirectoryAsync(string runDirectory, string gatewayDirectory)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(gatewayDirectory)) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(runDirectory);
        if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("Gateway run directory must remain within the owned temporary gateway directory.");
        await WindowsGatewayJob.WaitForWorkingDirectoryReleaseAsync(target).ConfigureAwait(false);
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }

    public Task StopAsync()
    {
        lock (_sync)
        {
            if (_stopTask is not null) return _stopTask;
            if (_connection is null && _startup is null && _ownedConnections.Count == 0)
            {
                _state = GatewayState.Stopped;
                return Task.CompletedTask;
            }
            _state = GatewayState.Stopping;
            ++_generation;
            var startup = _startup;
            var connection = _connection;
            var startupCancellation = _startupCancellation;
            startupCancellation?.Cancel();
            foreach (var owned in _ownedConnections) owned.Cancellation.Cancel();
            _startup = null;
            _connection = null;
            _startupCancellation = null;
            return _stopTask = StopCoreAsync(startup, connection, startupCancellation);
        }
    }

    private async Task StopCoreAsync(Task<Connection>? startup, Connection? connection, CancellationTokenSource? startupCancellation)
    {
        await Task.Yield();
        try
        {
            if (startup is not null)
            {
                try { connection = await startup.ConfigureAwait(false); } catch (GatewayException) { }
            }
            Connection[] owned;
            lock (_sync)
            {
                owned = _ownedConnections.ToArray();
                if (connection is not null && !owned.Contains(connection)) owned = owned.Append(connection).ToArray();
            }
            await Task.WhenAll(owned.Select(DisposeOwnedConnectionAsync)).ConfigureAwait(false);
        }
        finally
        {
            startupCancellation?.Dispose();
            lock (_sync) { _state = GatewayState.Stopped; _stopTask = null; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_sync) _disposed = true;
        await StopAsync().ConfigureAwait(false);
    }

    private (string Node, string Script) LocateGateway()
    {
        if (_options.GatewayScriptPath is { } explicitScript && _options.NodeExecutablePath is { } explicitNode)
        {
            if (!File.Exists(explicitScript) || !File.Exists(explicitNode))
                throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_missing", "在线音乐组件缺失，请重新安装");
            return (Path.GetFullPath(explicitNode), Path.GetFullPath(explicitScript));
        }
        var shippedScript = Path.Combine(_options.BaseDirectory, "music-gateway", "server.mjs");
        if (File.Exists(shippedScript))
        {
            var shippedNode = Path.Combine(_options.BaseDirectory, "music-gateway", "node", "node.exe");
            if (!File.Exists(shippedNode))
                throw new GatewayException(GatewayFailureKind.Unavailable, "node_missing", "在线音乐运行组件缺失，请重新安装");
            return (shippedNode, shippedScript);
        }
        for (var directory = new DirectoryInfo(_options.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var script = Path.Combine(directory.FullName, "tools", "music-gateway", "server.mjs");
            if (!File.Exists(script)) continue;
            var node = Path.Combine(directory.FullName, "tools", "node", "node.exe");
            if (File.Exists(node)) return (node, script);
            // PATH fallback is restricted to a development checkout containing the gateway source.
            var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var candidate = Path.Combine(path.Trim('"'), executable);
                if (File.Exists(candidate)) return (candidate, script);
            }
            throw new GatewayException(GatewayFailureKind.Unavailable, "node_missing", "开发环境未找到 Node.js");
        }
        throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_missing", "在线音乐组件缺失，请重新安装");
    }

    private static async Task<JsonElement> SendCoreAsync(Connection connection, string route, object? payload, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(payload is null ? HttpMethod.Get : HttpMethod.Post, new Uri(connection.Address, route));
        request.Headers.Add("X-Neko-Token", connection.Token);
        if (payload is not null) request.Content = JsonContent.Create(payload);
        using var response = await connection.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new GatewayException(GatewayFailureKind.Protocol, "response_too_large", "在线音乐服务返回的数据过大");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaximumResponseBytes)
                throw new GatewayException(GatewayFailureKind.Protocol, "response_too_large", "在线音乐服务返回的数据过大");
            buffer.Write(chunk, 0, count);
        }
        JsonDocument document;
        try { document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length)); }
        catch (JsonException ex)
        {
            throw new GatewayException(GatewayFailureKind.Protocol, "gateway_json", "在线音乐服务返回的数据无法识别", ex);
        }
        using (document)
        {
            var root = document.RootElement;
            if (!response.IsSuccessStatusCode || root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out _))
            {
                var code = "provider_failed";
                var message = "该音乐来源暂时不可用，请稍后重试";
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
                {
                    if (error.ValueKind == JsonValueKind.Object)
                    {
                        if (error.TryGetProperty("code", out var codeValue) && codeValue.ValueKind == JsonValueKind.String) code = codeValue.GetString() ?? code;
                        if (error.TryGetProperty("message", out var messageValue) && messageValue.ValueKind == JsonValueKind.String) message = messageValue.GetString() ?? message;
                    }
                    else if (error.ValueKind == JsonValueKind.String) message = error.GetString() ?? message;
                    if (root.TryGetProperty("code", out var topCode) && topCode.ValueKind == JsonValueKind.String) code = topCode.GetString() ?? code;
                }
                var kind = response.StatusCode == HttpStatusCode.TooManyRequests ? GatewayFailureKind.RateLimited : GatewayFailureKind.Provider;
                throw new GatewayException(kind, SafeDiagnostic(code, connection.Token), SafeDiagnostic(message, connection.Token), httpStatusCode: (int)response.StatusCode);
            }
            return root.Clone();
        }
    }

    private static async Task<string> ReadHandshakeAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), cancellationToken).ConfigureAwait(false) > 0)
        {
            if (character[0] == '\n') return result.ToString().TrimEnd('\r');
            if (result.Length >= 16384)
                throw new GatewayException(GatewayFailureKind.Protocol, "gateway_handshake", "在线音乐服务启动响应异常");
            result.Append(character[0]);
        }
        throw new GatewayException(GatewayFailureKind.Unavailable, "gateway_exited", "在线音乐服务在启动时退出");
    }

    private static async Task DrainAsync(StreamReader reader, Connection connection, bool retainDiagnostics)
    {
        var chunk = new char[1024];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(chunk.AsMemory(), connection.Cancellation.Token).ConfigureAwait(false)) > 0)
            {
                if (!retainDiagnostics) continue;
                // Only a bounded redacted tail is retained; raw child output never reaches application logs.
                var safe = SafeDiagnostic(new string(chunk, 0, count), connection.Token);
                lock (connection.DiagnosticLock)
                {
                    connection.DiagnosticTail += safe;
                    if (connection.DiagnosticTail.Length > 2048) connection.DiagnosticTail = connection.DiagnosticTail[^2048..];
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static string SafeDiagnostic(string value, string token)
    {
        value = value.Replace(token, "[redacted]", StringComparison.Ordinal);
        value = Regex.Replace(value, @"https?://[^\s""'<>]+", "[url]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        value = Regex.Replace(value, @"\b(token|authorization|cookie|password)\b\s*[:=]\s*[^\r\n,;]+", "$1=[redacted]", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return value.Length > 1024 ? value[..1024] : value;
    }

    private sealed class Connection : IAsyncDisposable
    {
        private readonly object _disposeLock = new();
        private Task? _disposeTask;
        public Connection(Process process, WindowsGatewayJob? job, string token, string runDirectory, string gatewayDirectory)
        {
            Process = process; Job = job; Token = token;
            RunDirectory = runDirectory; GatewayDirectory = gatewayDirectory;
            Client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        }
        public Process Process { get; }
        public WindowsGatewayJob? Job { get; }
        public string Token { get; }
        public string RunDirectory { get; }
        private string GatewayDirectory { get; }
        public Uri Address { get; set; } = null!;
        public HttpClient Client { get; }
        public CancellationTokenSource Cancellation { get; } = new();
        public Task? StandardErrorDrain { get; set; }
        public Task? StandardOutputDrain { get; set; }
        public object DiagnosticLock { get; } = new();
        public string DiagnosticTail { get; set; } = "";

        public ValueTask DisposeAsync()
        {
            lock (_disposeLock) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
        }

        private async Task DisposeCoreAsync()
        {
            // Every concurrent disposer awaits this same completion, including Exited and Stop callers.
            await Task.Yield();
            Cancellation.Cancel();
            Client.Dispose();
            try
            {
                try { Process.StandardInput.Dispose(); } catch (IOException) { }
                if (Job is not null) await Job.TerminateAndWaitAsync().ConfigureAwait(false);
                else if (!Process.HasExited) Process.Kill(entireProcessTree: true);
                await Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                // Read loops are cancelled, then fully awaited before Process closes all redirected pipe handles.
                await Task.WhenAll(StandardErrorDrain ?? Task.CompletedTask, StandardOutputDrain ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            finally
            {
                Job?.Dispose();
                Process.Dispose();
            }
            await RemoveOwnedRunDirectoryAsync(RunDirectory, GatewayDirectory).ConfigureAwait(false);
            // Cancellation remains readable by in-flight SendAsync tasks until they finish.
        }
    }
}
