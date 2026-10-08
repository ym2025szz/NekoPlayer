using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class ProviderAccountServiceTests : IDisposable
{
    private readonly GatewayTestPaths _paths = new();
    [Fact]
    public async Task RestoresSavedCredentialsOncePerGatewayConnection()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths); await vault.SetAsync("netease", "MUSIC_U=fixture");
        var gateway = new AccountGateway(); var service = new ProviderAccountService(gateway, vault);
        await service.EnsureReadyAsync(); await service.EnsureReadyAsync();
        Assert.Equal(1, gateway.Routes.Count(x => x == "/v1/auth/restore"));
        var revision = service.Revision; gateway.Address = new Uri("http://127.0.0.1:12346"); gateway.LoggedIn = false;
        await service.EnsureReadyAsync();
        Assert.Equal(2, gateway.Routes.Count(x => x == "/v1/auth/restore")); Assert.True(service.Revision > revision);
    }
    [Fact]
    public async Task AuthorizationPersistsPrivatelyAndLogoutClearsGatewayAndSavedCredential()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths); var gateway = new AccountGateway();
        var service = new ProviderAccountService(gateway, vault);
        var login = await service.StartLoginAsync("netease"); var revision = service.Revision;
        var result = await service.CheckLoginAsync("netease", login.LoginId);
        Assert.Equal("authorized", result.State); Assert.True(result.Account!.LoggedIn);
        Assert.DoesNotContain("fixture-private", JsonSerializer.Serialize(result));
        Assert.Equal("MUSIC_U=fixture-private", (await vault.ReadAsync())["netease"]);
        Assert.True(service.Revision > revision);
        await service.LogoutAsync("netease");
        Assert.Empty(await vault.ReadAsync()); Assert.False((await service.GetAccountsAsync()).Single().LoggedIn);
    }
    [Fact]
    public async Task CorruptSavedCredentialDoesNotDisableGuestModeOrExposeExceptionDetails()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths); await File.WriteAllBytesAsync(vault.FilePath, [1,2,3]);
        var service = new ProviderAccountService(new AccountGateway(), vault);
        var account = Assert.Single(await service.GetAccountsAsync());
        Assert.False(account.LoggedIn); Assert.True(account.CanLogin); Assert.Contains("重新登录", account.Message);
    }
    private sealed class AccountGateway : IGatewayRuntime
    {
        public Uri Address { get; set; } = new("http://127.0.0.1:12345");
        public bool LoggedIn { get; set; }
        public List<string> Routes { get; } = [];
        public GatewayState State => GatewayState.Ready;
        public string? LastError => null;
        public Task<Uri> EnsureReadyAsync(CancellationToken cancellationToken = default) => Task.FromResult(Address);
        public Task<JsonElement> SendAsync(string route, object? payload = null, CancellationToken cancellationToken = default)
        {
            Routes.Add(route);
            if (route is "/v1/auth/restore" or "/v1/auth/check") LoggedIn = true;
            if (route == "/v1/auth/logout") LoggedIn = false;
            var account = new { providerId = "netease", name = "网易云", canLogin = true, loggedIn = LoggedIn, displayName = LoggedIn ? "Test" : "", message = "Guest" };
            object body = route switch
            {
                "/v1/auth/status" => new { accounts = new[] { account } },
                "/v1/auth/start" => new { providerId = "netease", loginId = "fixture-id", qrImage = "data:image/png;base64,fixture", expiresAt = DateTimeOffset.UtcNow.AddMinutes(3), message = "Scan" },
                "/v1/auth/check" => new { state = "authorized", message = "Success", account, credential = "MUSIC_U=fixture-private" },
                _ => account
            };
            return Task.FromResult(JsonSerializer.SerializeToElement(body));
        }
        public Task StopAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
