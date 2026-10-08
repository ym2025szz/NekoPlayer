using System.Text;
using NekoPlayer.Infrastructure.Online;

namespace NekoPlayer.Tests;

public sealed class WindowsAccountVaultTests : IDisposable
{
    private readonly GatewayTestPaths _paths = new();
    [Fact]
    public async Task CredentialsRoundTripEncryptedAndLogoutForgetsOnlyOneProvider()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths);
        await vault.SetAsync("netease", "MUSIC_U=fixture-sensitive-session");
        await vault.SetAsync("qq", "uin=123; qm_keyst=fixture-qq-session");
        var bytes = await File.ReadAllBytesAsync(vault.FilePath);
        Assert.DoesNotContain("fixture-sensitive-session", Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain("MUSIC_U", Encoding.UTF8.GetString(bytes));
        Assert.Equal("MUSIC_U=fixture-sensitive-session", (await new WindowsAccountVault(_paths).ReadAsync())["netease"]);
        await vault.SetAsync("netease", null);
        var remaining = await vault.ReadAsync();
        Assert.False(remaining.ContainsKey("netease")); Assert.Single(remaining);
        Assert.Equal("uin=123; qm_keyst=fixture-qq-session", remaining["qq"]);
        Assert.False(File.Exists(_paths.SettingsPath)); Assert.Empty(Directory.GetFiles(_paths.ConfigDirectory, "*.tmp"));
    }
    [Fact]
    public async Task ConcurrentVaultInstancesDoNotLoseOtherProviderCredentials()
    {
        if (!OperatingSystem.IsWindows()) return;
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => new WindowsAccountVault(_paths).SetAsync("provider" + i, "fixture" + i)));
        Assert.Equal(12, (await new WindowsAccountVault(_paths).ReadAsync()).Count);
    }
    [Fact]
    public async Task CorruptVaultIsPreservedAndCannotBeOverwrittenSilently()
    {
        if (!OperatingSystem.IsWindows()) return;
        var vault = new WindowsAccountVault(_paths); byte[] corrupt = [1, 2, 3, 4];
        await File.WriteAllBytesAsync(vault.FilePath, corrupt);
        await Assert.ThrowsAnyAsync<Exception>(() => vault.SetAsync("netease", "new-credential"));
        Assert.Equal(corrupt, await File.ReadAllBytesAsync(vault.FilePath));
    }
    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
