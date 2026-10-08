using System.Collections.Concurrent;
using System.Text.Json;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using Serilog;

namespace NekoPlayer.Infrastructure.Configuration;

public sealed class JsonSettingsService(IUserDataPaths paths) : ISettingsService
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly SemaphoreSlim _fileLock = FileLocks.GetOrAdd(Path.GetFullPath(paths.SettingsPath), _ => new SemaphoreSlim(1, 1));

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var primary = await ReadAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false);
            if (primary is not null) return primary;
            var backup = await ReadAsync(paths.SettingsPath + ".bak", cancellationToken).ConfigureAwait(false);
            if (backup is not null)
            {
                Log.Warning("主配置无法读取，已从备份恢复设置");
                return backup;
            }
            return new AppSettings();
        }
        finally { _fileLock.Release(); }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        // Capture bytes before the first await. Later UI mutations cannot change a queued save's snapshot.
        var snapshot = JsonSerializer.SerializeToUtf8Bytes(settings, Options);
        await _fileLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var temporary = paths.SettingsPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(paths.SettingsPath))!);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 8192, options: FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(snapshot, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(paths.SettingsPath))
            {
                // Keep the previous readable backup when replacing a corrupted primary.
                var validPrimary = await ReadAsync(paths.SettingsPath, cancellationToken).ConfigureAwait(false) is not null;
                File.Replace(temporary, paths.SettingsPath, validPrimary ? paths.SettingsPath + ".bak" : null, ignoreMetadataErrors: true);
            }
            else File.Move(temporary, paths.SettingsPath);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { Log.Warning("无法清理配置临时文件"); }
            _fileLock.Release();
        }
    }

    private static async Task<AppSettings?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning("配置文件损坏或无法读取，将尝试备份或默认设置");
            return null;
        }
    }
}
