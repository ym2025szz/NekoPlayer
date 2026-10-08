using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class GatewayRuntimeTests
{
    [Fact]
    public async Task MissingPublishedComponentsFailHonestlyWithoutHanging()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = new GatewayRuntime(fixture.Paths, new GatewayRuntimeOptions { BaseDirectory = fixture.Paths.Root });
        var failure = await Assert.ThrowsAsync<GatewayException>(() => runtime.EnsureReadyAsync());
        Assert.Equal("gateway_missing", failure.Code);
        Assert.Equal(GatewayState.Unavailable, runtime.State);
        Assert.NotNull(runtime.LastError);
        await Task.WhenAll(runtime.StopAsync(), runtime.StopAsync());
        Assert.Equal(GatewayState.Stopped, runtime.State);
    }

    [Fact]
    public async Task DisposedRuntimeRejectsRequestsAndRepeatedStopIsSafe()
    {
        using var fixture = new GatewayFixture();
        var runtime = new GatewayRuntime(fixture.Paths);
        await runtime.DisposeAsync();
        await runtime.DisposeAsync();
        await runtime.StopAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => runtime.EnsureReadyAsync());
    }

    [GatewayNodeFact]
    public async Task ConcurrentReadinessAndCancelledWaiterStartOneProcess()
    {
        using var fixture = new GatewayFixture("delay");
        await using var runtime = fixture.Runtime();
        using var cancelled = new CancellationTokenSource();
        var oldWait = runtime.EnsureReadyAsync(cancelled.Token);
        var waits = Enumerable.Range(0, 12).Select(_ => runtime.EnsureReadyAsync()).ToArray();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldWait);
        var addresses = await Task.WhenAll(waits);
        Assert.All(addresses, address => Assert.Equal(addresses[0], address));
        Assert.Equal("127.0.0.1", addresses[0].Host);
        Assert.Single(await File.ReadAllLinesAsync(Path.Combine(fixture.Paths.DataDirectory, "starts.log")));
        Assert.Equal(GatewayState.Ready, runtime.State);
        await runtime.DisposeAsync();
        fixture.AssertGatewayProcessesExited();
    }

    [GatewayNodeFact]
    public async Task RequestsUseTokenAndReturnedJsonOutlivesDocument()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        var health = await runtime.SendAsync("/health");
        Assert.True(health.GetProperty("ready").GetBoolean());
        var response = await runtime.SendAsync("/v1/search", new { query = "song" });
        Assert.Equal("song", response.GetProperty("query").GetString());
        Assert.True(health.GetProperty("ready").GetBoolean());
        var failure = await Assert.ThrowsAsync<GatewayException>(() => runtime.SendAsync("/v1/failure", new { }));
        Assert.Equal(429, failure.HttpStatusCode);
        Assert.Equal(GatewayFailureKind.RateLimited, failure.Kind);
        Assert.DoesNotContain("sensitive", failure.Message);
        Assert.DoesNotContain("https://", failure.Message);
    }

    [GatewayNodeFact]
    public async Task AuthAndMusicShareFourSlotsAndCancelledQueuedRequestDoesNotLeakCapacity()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        await runtime.EnsureReadyAsync();
        var first = Enumerable.Range(0, 4).Select(index => runtime.SendAsync(index % 2 == 0 ? "/v1/auth/hold" : "/v1/music/hold", new { })).ToArray();
        using var cancellation = new CancellationTokenSource();
        var queued = runtime.SendAsync("/v1/auth/hold", new { }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        var next = Enumerable.Range(0, 8).Select(index => runtime.SendAsync(index % 2 == 0 ? "/v1/auth/hold" : "/v1/music/hold", new { })).ToArray();
        var results = await Task.WhenAll(first.Concat(next)).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.All(results, result => Assert.InRange(result.GetProperty("peak").GetInt32(), 1, 4));
        Assert.True((await runtime.SendAsync("/health")).GetProperty("ready").GetBoolean());
    }

    [GatewayNodeFact]
    public async Task CancellingActiveRequestsReleasesAllClientSlotsForLaterRequests()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        await runtime.EnsureReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var active = Enumerable.Range(0, 4).Select(_ => runtime.SendAsync("/v1/never", new { }, cancellation.Token)).ToArray();
        cancellation.Cancel();
        foreach (var request in active) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        Assert.True((await runtime.SendAsync("/health").WaitAsync(TimeSpan.FromSeconds(2))).GetProperty("ready").GetBoolean());
    }

    [GatewayNodeFact]
    public async Task StartupTimeoutStopsItsProcess()
    {
        using var fixture = new GatewayFixture("no-ready");
        await using var runtime = fixture.Runtime(TimeSpan.FromMilliseconds(500));
        var failure = await Assert.ThrowsAsync<GatewayException>(() => runtime.EnsureReadyAsync());
        Assert.Equal(GatewayFailureKind.Timeout, failure.Kind);
        Assert.Equal(GatewayState.Unavailable, runtime.State);
        var pid = int.Parse((await File.ReadAllLinesAsync(Path.Combine(fixture.Paths.DataDirectory, "starts.log"))).Single());
        Assert.False(IsAlive(pid));
        await runtime.DisposeAsync();
        fixture.AssertGatewayProcessesExited();
    }

    [GatewayNodeFact]
    public async Task StopRacingStartupLeavesNoProcessAndAllowsALaterRestart()
    {
        using var fixture = new GatewayFixture("delay");
        await using var runtime = fixture.Runtime();
        var startup = runtime.EnsureReadyAsync();
        await fixture.WaitForStartsAsync();
        await Task.WhenAll(runtime.StopAsync(), runtime.StopAsync());
        await Assert.ThrowsAsync<GatewayException>(() => startup);
        Assert.Equal(GatewayState.Stopped, runtime.State);
        var pid = int.Parse((await File.ReadAllLinesAsync(Path.Combine(fixture.Paths.DataDirectory, "starts.log"))).Single());
        Assert.False(IsAlive(pid));
        await runtime.EnsureReadyAsync();
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(fixture.Paths.DataDirectory, "starts.log"))).Length);
    }

    [GatewayNodeFact]
    public async Task DisposingRuntimeKillsGatewayDescendants()
    {
        using var fixture = new GatewayFixture();
        var runtime = fixture.Runtime();
        var response = await runtime.SendAsync("/v1/child", new { });
        var childPid = response.GetProperty("pid").GetInt32();
        Assert.True(IsAlive(childPid));
        await runtime.DisposeAsync();
        for (var attempt = 0; attempt < 20 && IsAlive(childPid); attempt++) await Task.Delay(25);
        Assert.False(IsAlive(childPid));
    }

    [GatewayNodeFact]
    public async Task UnexpectedExitIsReportedAndNextRequestStartsANewGateway()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        await runtime.SendAsync("/v1/exit", new { });
        for (var attempt = 0; attempt < 100 && runtime.State == GatewayState.Ready; attempt++) await Task.Delay(10);
        Assert.Equal(GatewayState.Unavailable, runtime.State);
        Assert.NotNull(runtime.LastError);
        Assert.True((await runtime.SendAsync("/health")).GetProperty("ready").GetBoolean());
        Assert.Equal(2, (await File.ReadAllLinesAsync(Path.Combine(fixture.Paths.DataDirectory, "starts.log"))).Length);
        await runtime.DisposeAsync();
        fixture.AssertGatewayProcessesExited();
    }

    [GatewayNodeFact]
    public async Task ConcurrentStopAndDisposeWaitForCurrentAndRetiredProcessHandles()
    {
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        var oldChild = (await runtime.SendAsync("/v1/child", new { })).GetProperty("pid").GetInt32();
        await runtime.SendAsync("/v1/exit", new { });
        for (var attempt = 0; attempt < 100 && runtime.State == GatewayState.Ready; attempt++) await Task.Delay(10);
        await runtime.EnsureReadyAsync();
        var currentChild = (await runtime.SendAsync("/v1/child", new { })).GetProperty("pid").GetInt32();
        await Task.WhenAll(runtime.StopAsync(), runtime.DisposeAsync().AsTask(), runtime.DisposeAsync().AsTask(), runtime.StopAsync());
        fixture.AssertGatewayProcessesExited();
        Assert.False(IsAlive(oldChild));
        Assert.False(IsAlive(currentChild));
        // Directory.Delete in the fixture is immediate: a lingering cwd or pipe handle still fails this test.
    }

    [GatewayNodeFact]
    public async Task DisposeWaitsUntilNativeWorkingDirectorySharingHandleIsReleased()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new GatewayFixture();
        await using var runtime = fixture.Runtime();
        await runtime.EnsureReadyAsync();
        var instance = await runtime.SendAsync("/v1/instance");
        var workingDirectory = instance.GetProperty("workingDirectory").GetString()!;
        // A zero-access metadata handle does not participate in Windows data/delete sharing checks.
        // Hold GENERIC_READ on the exact cwd that GatewayRuntime probes, without FILE_SHARE_DELETE.
        using var directoryHandle = OpenDirectory(workingDirectory, 0x80000000, 3, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        Assert.False(directoryHandle.IsInvalid);
        using (var sharingProbe = OpenDirectory(workingDirectory, 0x00010000, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero))
        {
            var sharingError = Marshal.GetLastWin32Error();
            Assert.True(sharingProbe.IsInvalid);
            Assert.Equal(32, sharingError);
        }
        var shutdown = runtime.DisposeAsync().AsTask();
        var pid = int.Parse(File.ReadAllLines(Path.Combine(fixture.Paths.DataDirectory, "starts.log")).Single());
        for (var attempt = 0; attempt < 100 && IsAlive(pid); attempt++) await Task.Delay(10);
        Assert.False(IsAlive(pid));
        Assert.False(shutdown.IsCompleted);
        directoryHandle.Dispose();
        await shutdown;
        fixture.AssertGatewayProcessesExited();
    }

    [GatewayNodeFact]
    public async Task TwoRuntimesShareInstallationButOwnIndependentScratchAndShutdown()
    {
        using var firstFixture = new GatewayFixture();
        using var secondFixture = new GatewayFixture();
        await using var first = firstFixture.Runtime();
        await using var second = secondFixture.Runtime(sharedScriptPath: firstFixture.ScriptPath);
        var instances = await Task.WhenAll(first.SendAsync("/v1/instance"), second.SendAsync("/v1/instance"));
        var firstDirectory = instances[0].GetProperty("workingDirectory").GetString()!;
        var secondDirectory = instances[1].GetProperty("workingDirectory").GetString()!;
        Assert.NotEqual(firstDirectory, secondDirectory);
        Assert.StartsWith(Path.Combine(firstFixture.Paths.TempDirectory, "Gateway") + Path.DirectorySeparatorChar, firstDirectory);
        Assert.StartsWith(Path.Combine(secondFixture.Paths.TempDirectory, "Gateway") + Path.DirectorySeparatorChar, secondDirectory);
        Assert.Equal(firstDirectory, instances[0].GetProperty("dataDirectory").GetString());
        Assert.Equal(secondDirectory, instances[1].GetProperty("dataDirectory").GetString());
        var secondPid = instances[1].GetProperty("pid").GetInt32();
        await first.DisposeAsync();
        firstFixture.AssertGatewayProcessesExited();
        Assert.False(Directory.Exists(firstDirectory));
        Assert.True(IsAlive(secondPid));
        Assert.True(Directory.Exists(secondDirectory));
        Assert.True((await second.SendAsync("/health")).GetProperty("ready").GetBoolean());
        await second.DisposeAsync();
        secondFixture.AssertGatewayProcessesExited();
        Assert.False(IsAlive(secondPid));
        Assert.False(Directory.Exists(secondDirectory));
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenDirectory(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [Theory]
    [InlineData("https://example.test")]
    [InlineData("//example.test/path")]
    [InlineData("/\\example.test/path")]
    public async Task ExternalRoutesAreRejectedBeforeStartup(string route)
    {
        using var fixture = new GatewayFixture();
        await using var runtime = new GatewayRuntime(fixture.Paths);
        await Assert.ThrowsAsync<ArgumentException>(() => runtime.SendAsync(route));
        Assert.Equal(GatewayState.Stopped, runtime.State);
    }

    private static bool IsAlive(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private sealed class GatewayFixture : IDisposable
    {
        private readonly string _script;
        public GatewayFixture(string mode = "normal")
        {
            Paths = new GatewayTestPaths();
            _script = Path.Combine(Paths.Root, "fixture.mjs");
            File.WriteAllText(Path.Combine(Paths.Root, "mode"), mode);
            File.WriteAllText(_script, """
                import http from 'node:http';
                import fs from 'node:fs';
                import readline from 'node:readline';
                import path from 'node:path';
                import {fileURLToPath} from 'node:url';
                import {spawn} from 'node:child_process';
                const lines = readline.createInterface({input:process.stdin});
                lines.once('line', line => {
                  const config = JSON.parse(line);
                  const profileRoot = path.resolve(config.dataDirectory,'../../..');
                  fs.appendFileSync(path.join(profileRoot,'Data','starts.log'), process.pid + '\n');
                  const mode = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)),'mode'),'utf8');
                  fs.writeFileSync(path.join(config.dataDirectory,'sdk-scratch.json'), '{}');
                  if (mode === 'no-ready') { setInterval(()=>{},1000); return; }
                  let active=0, peak=0;
                  const server = http.createServer(async (req,res) => {
                    if (req.headers['x-neko-token'] !== config.token) { res.writeHead(401); res.end('{"error":"unauthenticated"}'); return; }
                    res.setHeader('Content-Type','application/json');
                    let body=''; for await (const piece of req) body += piece;
                    if(req.url.endsWith('/hold')) { active++; peak=Math.max(peak,active); await new Promise(resolve=>setTimeout(resolve,150)); active--; res.end(JSON.stringify({peak})); return; }
                    if(req.url === '/v1/never') return;
                    if(req.url === '/v1/instance') { res.end(JSON.stringify({pid:process.pid,workingDirectory:process.cwd(),dataDirectory:config.dataDirectory})); return; }
                    if(req.url === '/v1/failure') { res.writeHead(429); res.end(JSON.stringify({error:{code:'rate_limited',message:'https://sensitive.test/audio?token=sensitive cookie=sensitive'}})); return; }
                    if(req.url === '/v1/child') { const child=spawn(process.execPath,['-e','setInterval(()=>{},1000)'],{stdio:'ignore',windowsHide:true}); res.end(JSON.stringify({pid:child.pid})); return; }
                    if(req.url === '/v1/exit') { res.end('{}'); setTimeout(()=>process.exit(3),30); return; }
                    res.end(JSON.stringify(req.url==='/health' ? {ready:true} : {query:JSON.parse(body||'{}').query}));
                  });
                  const start=()=>server.listen(0,'127.0.0.1',()=>process.stdout.write(JSON.stringify({ready:true,port:server.address().port,protocolVersion:1})+'\n'));
                  if(mode==='delay') setTimeout(start,200); else start();
                });
                """);
        }
        public GatewayTestPaths Paths { get; }
        public string ScriptPath => _script;
        public GatewayRuntime Runtime(TimeSpan? timeout = null, string? sharedScriptPath = null) => new(Paths, new GatewayRuntimeOptions
        { NodeExecutablePath = GatewayNodeFactAttribute.FindNode(), GatewayScriptPath = sharedScriptPath ?? _script, StartupTimeout = timeout ?? TimeSpan.FromSeconds(3) });
        public async Task WaitForStartsAsync()
        {
            var file = Path.Combine(Paths.DataDirectory, "starts.log");
            for (var attempt = 0; attempt < 100; attempt++)
            { if (File.Exists(file) && new FileInfo(file).Length > 0) return; await Task.Delay(10); }
            throw new TimeoutException("Fixture process did not start.");
        }
        public void AssertGatewayProcessesExited()
        {
            var starts = Path.Combine(Paths.DataDirectory, "starts.log");
            if (!File.Exists(starts)) return;
            foreach (var line in File.ReadAllLines(starts))
                Assert.False(IsAlive(int.Parse(line)), $"Gateway PID {line} must exit before its working directory is deleted.");
        }
        public void Dispose()
        {
            AssertGatewayProcessesExited();
            if (Directory.Exists(Paths.Root)) Directory.Delete(Paths.Root, true);
        }
    }
}

public sealed class GatewayNodeFactAttribute : FactAttribute
{
    public GatewayNodeFactAttribute() { if (FindNode() is null) Skip = "Node.js is required for gateway process lifecycle integration tests."; }
    internal static string? FindNode()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "tools", "node", "node.exe");
            if (File.Exists(path)) return path;
        }
        var executable = OperatingSystem.IsWindows() ? "node.exe" : "node";
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path.Trim('"'), executable)).FirstOrDefault(File.Exists);
    }
}

internal sealed class GatewayTestPaths : IUserDataPaths
{
    public GatewayTestPaths()
    {
        Root = Path.Combine(Path.GetTempPath(), "NekoPlayerTests", Guid.NewGuid().ToString("N"));
        DataDirectory = Path.Combine(Root, "Data"); LogsDirectory = Path.Combine(Root, "Logs");
        CoversDirectory = Path.Combine(Root, "Covers"); LyricsDirectory = Path.Combine(Root, "Lyrics");
        ConfigDirectory = Path.Combine(Root, "Config"); TempDirectory = Path.Combine(Root, "Temp");
        DatabasePath = Path.Combine(DataDirectory, "test.db"); SettingsPath = Path.Combine(ConfigDirectory, "settings.json");
        EnsureCreated();
    }
    public string Root { get; }
    public string DataDirectory { get; }
    public string DatabasePath { get; }
    public string LogsDirectory { get; }
    public string CoversDirectory { get; }
    public string LyricsDirectory { get; }
    public string ConfigDirectory { get; }
    public string TempDirectory { get; }
    public string SettingsPath { get; }
    public void EnsureCreated()
    { foreach (var path in new[] { Root, DataDirectory, LogsDirectory, CoversDirectory, LyricsDirectory, ConfigDirectory, TempDirectory }) Directory.CreateDirectory(path); }
}
