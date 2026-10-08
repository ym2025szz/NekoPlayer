using System.Collections.Concurrent;
using System.Text.Json;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class ProviderReadinessTests : IDisposable
{
    private readonly GatewayTestPaths _paths = new();

    [Fact]
    public async Task FocusingAnAccountSelectsThatProviderWithoutSubmittingLogin()
    {
        var service = new NavigationAccounts();
        using var viewModel = new ProviderAccountsViewModel(service);
        viewModel.FocusProvider("qq");
        await viewModel.RefreshAsync();
        Assert.Equal("qq", viewModel.SelectedProviderId);
        Assert.Equal("qq", viewModel.SelectedRow?.Account.ProviderId);
        Assert.True(viewModel.SelectedRow?.IsFocused);
        Assert.False(viewModel.IsLoginVisible);
        Assert.Equal(0, service.AccountOperations);
    }

    [Fact]
    public async Task SlowPlatformRestoreDoesNotDelayOtherSourcesSearchPlaybackOrLyrics()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await vault.SetAsync("netease", "MUSIC_U=fixture");
        await vault.SetAsync("qq", "uin=fixture; qm_keyst=fixture");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new IndependentGateway(async (id, _) =>
        {
            if (id == "netease") { started.TrySetResult(); await release.Task; }
        });
        var accounts = new ProviderAccountService(gateway, vault);
        var all = accounts.EnsureReadyAsync();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var online = new OnlineMusicService(gateway, accounts);
        try
        {
            var page = await online.SearchAsync(new("song", "qq")).WaitAsync(TimeSpan.FromSeconds(2));
            var track = Assert.Single(page.Tracks);
            Assert.Equal(MusicAvailability.Full, (await online.ResolveAsync(track).WaitAsync(TimeSpan.FromSeconds(2))).Availability);
            Assert.Single(await online.GetLyricsAsync(track).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Single((await online.SearchAsync(new("song", "kuwo")).WaitAsync(TimeSpan.FromSeconds(2))).Tracks);
            Assert.False(all.IsCompleted);
        }
        finally { release.TrySetResult(); await all; }
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/auth/restore" && call.Provider == "qq"));
    }

    [Fact]
    public async Task CancelledReadinessWaiterDoesNotCancelSharedRestoreOrBlockGuestSources()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await vault.SetAsync("netease", "MUSIC_U=fixture");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateway = new IndependentGateway(async (_, token) =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var accounts = new ProviderAccountService(gateway, vault);
        using var cancelled = new CancellationTokenSource();
        var old = accounts.EnsureProviderReadyAsync("netease", cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var newer = accounts.EnsureProviderReadyAsync("netease");
        cancelled.Cancel();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => old);
            await accounts.EnsureProviderReadyAsync("kuwo").WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(newer.IsCompleted);
        }
        finally { release.TrySetResult(); await newer; }
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/auth/restore"));
    }

    [Fact]
    public async Task RefreshRetriesFailedRestoreAndKeepsUnverifiedQqCredentialDescription()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await vault.SetAsync("netease", "MUSIC_U=fixture");
        await vault.SetAsync("qq", "uin=fixture; qm_keyst=fixture");
        var attempts = 0;
        var gateway = new IndependentGateway((id, _) => id == "netease" && Interlocked.Increment(ref attempts) == 1
            ? Task.FromException(new GatewayException(GatewayFailureKind.Provider, "network_error", "网络恢复失败")) : Task.CompletedTask);
        var accounts = new ProviderAccountService(gateway, vault);
        await accounts.EnsureReadyAsync();
        await accounts.EnsureProviderReadyAsync("netease");
        Assert.Equal(1, attempts);
        var before = accounts.GetProviderRevision("netease");
        var refreshed = await accounts.GetAccountsAsync();
        Assert.True(refreshed.Single(account => account.ProviderId == "netease").LoggedIn);
        Assert.True(accounts.GetProviderRevision("netease") > before);
        Assert.Contains("尚未重新验证", refreshed.Single(account => account.ProviderId == "qq").Message);
        await accounts.GetAccountsAsync();
        Assert.Equal(2, attempts);
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/auth/restore" && call.Provider == "qq"));
    }

    [Fact]
    public async Task ExplicitRefreshRecoversAfterUnreadableVaultIsRepaired()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await File.WriteAllBytesAsync(vault.FilePath, [1, 2, 3]);
        var accounts = new ProviderAccountService(new IndependentGateway(), vault);
        await accounts.EnsureReadyAsync();
        Assert.Contains("重新登录", (await accounts.GetAccountsAsync()).Single(account => account.ProviderId == "netease").Message);
        File.Delete(vault.FilePath);
        await vault.SetAsync("netease", "MUSIC_U=fixture");
        Assert.True((await accounts.GetAccountsAsync()).Single(account => account.ProviderId == "netease").LoggedIn);
    }

    [Fact]
    public async Task CancellationAfterAuthorizationStillInvalidatesPermissionCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        var gateway = new IndependentGateway();
        var accounts = new ProviderAccountService(gateway, new WindowsAccountVault(_paths));
        var online = new OnlineMusicService(gateway, accounts);
        await online.SearchAsync(new("same", "netease"));
        var before = accounts.GetProviderRevision("netease");
        using var cancellation = new CancellationTokenSource();
        gateway.OnAuthorized = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => accounts.CheckLoginAsync("netease", "fixture-login", cancellation.Token));
        Assert.True(accounts.GetProviderRevision("netease") > before);
        await online.SearchAsync(new("same", "netease"));
        Assert.Equal(2, gateway.Calls.Count(call => call.Route == "/v1/search"));
    }

    [Fact]
    public async Task LoginAndLogoutInvalidateOnlyThatProvidersSearchCache()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await vault.SetAsync("netease", "MUSIC_U=fixture");
        await vault.SetAsync("qq", "uin=fixture; qm_keyst=fixture");
        var gateway = new IndependentGateway();
        var accounts = new ProviderAccountService(gateway, vault);
        var online = new OnlineMusicService(gateway, accounts);
        await online.SearchAsync(new("same", "netease"));
        await online.SearchAsync(new("same", "qq"));
        var qqRevision = accounts.GetProviderRevision("qq");
        await accounts.LogoutAsync("netease");
        await online.SearchAsync(new("same", "netease"));
        await online.SearchAsync(new("same", "qq"));
        Assert.Equal(qqRevision, accounts.GetProviderRevision("qq"));
        Assert.Equal(2, gateway.Calls.Count(call => call.Route == "/v1/search" && call.Provider == "netease"));
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/search" && call.Provider == "qq"));
        Assert.Equal("authorized", (await accounts.CheckLoginAsync("netease", "fixture-login")).State);
        await online.SearchAsync(new("same", "netease"));
        await online.SearchAsync(new("same", "qq"));
        Assert.Equal(3, gateway.Calls.Count(call => call.Route == "/v1/search" && call.Provider == "netease"));
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/search" && call.Provider == "qq"));
        Assert.Single(gateway.Calls.Where(call => call.Route == "/v1/auth/restore" && call.Provider == "netease"));
    }

    private sealed class IndependentGateway(Func<string, CancellationToken, Task>? restore = null) : IGatewayRuntime
    {
        private readonly ConcurrentDictionary<string, bool> _loggedIn = new();
        public Action? OnAuthorized { get; set; }
        public ConcurrentQueue<(string Route, string? Provider)> Calls { get; } = new();
        public GatewayState State => GatewayState.Ready;
        public string? LastError => null;
        public Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new Uri("http://127.0.0.1:12001/"));
        }
        public async Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var wire = JsonSerializer.SerializeToElement(payload);
            var id = wire.TryGetProperty("providerId", out var provider) ? provider.GetString()! : "";
            Calls.Enqueue((route, id));
            if (route == "/v1/auth/restore")
            {
                if (restore is not null) await restore(id, cancellationToken);
                _loggedIn[id] = true;
            }
            if (route == "/v1/auth/logout") _loggedIn[id] = false;
            if (route == "/v1/auth/check") { _loggedIn[id] = true; OnAuthorized?.Invoke(); }
            object response = route switch
            {
                "/v1/auth/status" => new { accounts = new[] { "netease", "qq", "kugou" }.Select(Account).ToArray() },
                "/v1/auth/check" => new { state = "authorized", message = "授权成功", account = Account(id), credential = "MUSIC_U=fixture-authorized" },
                "/v1/search" => new { page = 1, hasMore = false, tracks = new[] { new { providerTrackId = "fixture", title = "Song", artist = "Singer", durationSeconds = 180 } } },
                "/v1/resolve" => new { availability = "full", url = "https://invalid.example/fixture.mp3", durationSeconds = 180, canSeek = true },
                "/v1/lyrics" => new { lrc = "[00:01.00]fixture lyric" },
                _ => Account(id)
            };
            return JsonSerializer.SerializeToElement(response);
        }
        private ProviderAccount Account(string id) => new(id, id, true, _loggedIn.GetValueOrDefault(id), "fixture account", "游客模式");
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NavigationAccounts : IProviderAccountService
    {
        public int AccountOperations { get; private set; }
        public long Revision => 0;
        public Task EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderAccount>> GetAccountsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([new("netease", "网易云", true, false, "", "游客"), new("qq", "QQ 音乐", true, false, "", "游客")]);
        private Exception UnexpectedOperation() { AccountOperations++; return new InvalidOperationException("Navigation must not submit account operations."); }
        public Task<ProviderLogin> StartLoginAsync(string providerId, CancellationToken cancellationToken = default) => throw UnexpectedOperation();
        public Task<ProviderLoginResult> CheckLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default) => throw UnexpectedOperation();
        public Task CancelLoginAsync(string providerId, string loginId, CancellationToken cancellationToken = default) => throw UnexpectedOperation();
        public Task LogoutAsync(string providerId, CancellationToken cancellationToken = default) => throw UnexpectedOperation();
    }

    public void Dispose()
    {
        if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true);
    }
}
