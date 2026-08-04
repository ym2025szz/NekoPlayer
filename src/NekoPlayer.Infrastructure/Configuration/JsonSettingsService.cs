using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using Serilog;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed class JsonSettingsService(IUserDataPaths paths) : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(paths.SettingsPath)) return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(paths.SettingsPath);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "配置文件损坏或无法读取，将使用默认设置");
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.ConfigDirectory);
        var temporary = paths.SettingsPath + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, settings, Options, cancellationToken);
        File.Move(temporary, paths.SettingsPath, true);
    }
}
