using System.Text.Json;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Configuration;

namespace NekoPlayer.Tests;

public sealed class SettingsConcurrencyTests : IDisposable
{
    private readonly GatewayTestPaths _paths = new();

    [Fact]
    public async Task ConcurrentSavesAcrossInstancesRemainValidAndLeaveNoTemporaryFiles()
    {
        var services = new[] { new JsonSettingsService(_paths), new JsonSettingsService(_paths) };
        var saves = Enumerable.Range(0, 40).Select(i => services[i % 2].SaveAsync(new AppSettings
        { Theme = "theme" + i, QueueIndex = i, QueueTrackIds = [Guid.NewGuid()], SearchHistory = [new("query" + i, "qq", DateTime.UtcNow)] })).ToArray();
        await Task.WhenAll(saves);
        var result = await services[0].LoadAsync();
        Assert.Equal("theme" + result.QueueIndex, result.Theme);
        Assert.Equal("query" + result.QueueIndex, result.SearchHistory.Single().Query);
        Assert.Equal(39, result.QueueIndex);
        Assert.Single(result.QueueTrackIds);
        Assert.Empty(Directory.GetFiles(_paths.ConfigDirectory, "*.tmp"));
        using var main = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsPath));
        using var backup = JsonDocument.Parse(await File.ReadAllTextAsync(_paths.SettingsPath + ".bak"));
        Assert.Equal(JsonValueKind.Object, main.RootElement.ValueKind);
        Assert.Equal(JsonValueKind.Object, backup.RootElement.ValueKind);
    }

    [Fact]
    public async Task QueuedSaveUsesSnapshotCapturedBeforeCallerMutation()
    {
        var service = new JsonSettingsService(_paths);
        var id = Guid.NewGuid();
        var settings = new AppSettings { QueueTrackIds = [id], Theme = "snapshot", SearchHistory = [new("before", "qq", DateTime.UtcNow)] };
        var saving = service.SaveAsync(settings);
        settings.Theme = "later";
        settings.QueueTrackIds.Clear();
        settings.SearchHistory.Clear();
        await saving;
        var loaded = await service.LoadAsync();
        Assert.Equal("snapshot", loaded.Theme);
        Assert.Equal(id, Assert.Single(loaded.QueueTrackIds));
        Assert.Equal("before", Assert.Single(loaded.SearchHistory).Query);
    }

    [Fact]
    public async Task CorruptedPrimaryLoadsReadableBackupAndNextSavePreservesIt()
    {
        var service = new JsonSettingsService(_paths);
        await service.SaveAsync(new AppSettings { Theme = "known-good" });
        await service.SaveAsync(new AppSettings { Theme = "newer" });
        await File.WriteAllTextAsync(_paths.SettingsPath, "{corrupt");
        Assert.Equal("known-good", (await service.LoadAsync()).Theme);
        await service.SaveAsync(new AppSettings { Theme = "recovered" });
        Assert.Equal("recovered", (await service.LoadAsync()).Theme);
        var backup = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(_paths.SettingsPath + ".bak"));
        Assert.Equal("known-good", backup!.Theme);
    }

    [Fact]
    public async Task CancelledSavePreservesPreviousSettings()
    {
        var service = new JsonSettingsService(_paths);
        await service.SaveAsync(new AppSettings { Theme = "before" });
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(new AppSettings { Theme = "after" }, cancelled.Token));
        Assert.Equal("before", (await service.LoadAsync()).Theme);
        Assert.Empty(Directory.GetFiles(_paths.ConfigDirectory, "*.tmp"));
    }

    public void Dispose() { if (Directory.Exists(_paths.Root)) Directory.Delete(_paths.Root, true); }
}
