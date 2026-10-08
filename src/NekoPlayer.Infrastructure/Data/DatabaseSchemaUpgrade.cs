using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace NekoPlayer.Infrastructure.Data;

/// <summary>
/// Upgrades the pre-migration v1 database in place. SQLite's backup API includes committed WAL
/// pages; copying only the .db file would not provide a recoverable snapshot.
/// </summary>
public static class DatabaseSchemaUpgrade
{
    public const int CurrentVersion = 2;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] LegacyTrackColumns =
    [
        "Id", "FilePath", "Title", "Artist", "Album", "Genre", "Duration", "TrackNumber", "Year", "SampleRate",
        "Channels", "BitRate", "CodecName", "CoverCachePath", "IsFavorite", "PlayCount", "AddedAt", "LastPlayedAt",
        "FileSize", "FileLastWriteTimeUtc"
    ];
    private static readonly (string Name, string Definition)[] OnlineColumns =
    [
        ("SourceKind", "INTEGER NOT NULL DEFAULT 0"),
        ("ProviderId", "TEXT COLLATE NOCASE NULL"),
        ("ProviderTrackId", "TEXT NULL"),
        ("ProviderMetadataJson", "TEXT NOT NULL DEFAULT '{}'"),
        ("CoverUrl", "TEXT NULL"),
        ("VersionLabel", "TEXT NOT NULL DEFAULT ''")
    ];

    public static async Task InitializeAsync(NekoPlayerDbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Database.GetDbConnection() is not SqliteConnection connection)
            throw new NotSupportedException("音乐库升级仅支持 SQLite 数据库。");

        var settings = new SqliteConnectionStringBuilder(connection.ConnectionString);
        var gateKey = IsTransient(settings)
            ? connection.ConnectionString : Path.GetFullPath(settings.DataSource);
        var gate = Gates.GetOrAdd(gateKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var openedHere = connection.State != ConnectionState.Open;
        try
        {
            if (openedHere) await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var version = Convert.ToInt32(await ScalarAsync(connection, "PRAGMA user_version;", cancellationToken).ConfigureAwait(false));
            if (version > CurrentVersion)
                throw new InvalidOperationException($"音乐库版本 {version} 高于当前程序支持的版本 {CurrentVersion}，已保留原数据库。");

            var tableCount = Convert.ToInt32(await ScalarAsync(connection,
                "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';", cancellationToken).ConfigureAwait(false));
            if (tableCount == 0)
            {
                // EnsureCreated is appropriate only for an empty database, never an existing v1 library.
                await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA user_version = {CurrentVersion};", null, cancellationToken).ConfigureAwait(false);
                return;
            }

            var columns = await GetTrackColumnsAsync(connection, cancellationToken).ConfigureAwait(false);
            if (LegacyTrackColumns.Any(x => !columns.Contains(x)))
                throw new InvalidOperationException("音乐库结构无法识别，升级已停止并保留原数据库。");

            var missing = OnlineColumns.Where(x => !columns.Contains(x.Name)).ToArray();
            var localIndex = await IndexMatchesAsync(connection, "IX_Tracks_FilePath", "SourceKind=0", ["FilePath"], cancellationToken).ConfigureAwait(false);
            var onlineIndex = await IndexMatchesAsync(connection, "IX_Tracks_ProviderId_ProviderTrackId", "SourceKind=1", ["ProviderId", "ProviderTrackId"], cancellationToken).ConfigureAwait(false);
            if (version == CurrentVersion && missing.Length == 0 && localIndex && onlineIndex) return;

            var backupPath = await BackupAsync(connection, settings, cancellationToken).ConfigureAwait(false);
            try
            {
                // Non-deferred transaction reserves the writer before changing any schema objects.
                await using var transaction = connection.BeginTransaction(deferred: false);
                foreach (var column in missing)
                    await ExecuteAsync(connection, $"ALTER TABLE \"Tracks\" ADD COLUMN \"{column.Name}\" {column.Definition};",
                        transaction, cancellationToken).ConfigureAwait(false);

                await ExecuteAsync(connection, "DROP INDEX IF EXISTS \"IX_Tracks_FilePath\";", transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "CREATE UNIQUE INDEX \"IX_Tracks_FilePath\" ON \"Tracks\" (\"FilePath\" COLLATE NOCASE) WHERE \"SourceKind\" = 0;",
                    transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, "DROP INDEX IF EXISTS \"IX_Tracks_ProviderId_ProviderTrackId\";", transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection,
                    "CREATE UNIQUE INDEX \"IX_Tracks_ProviderId_ProviderTrackId\" ON \"Tracks\" (\"ProviderId\" COLLATE NOCASE, \"ProviderTrackId\") WHERE \"SourceKind\" = 1;",
                    transaction, cancellationToken).ConfigureAwait(false);
                await ExecuteAsync(connection, $"PRAGMA user_version = {CurrentVersion};", transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                Log.Information("音乐库已升级到 v{Version}；升级前备份：{BackupPath}", CurrentVersion, backupPath ?? "内存数据库");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Transaction disposal rolls every column/index/marker change back. Never replace a
                // live file with the snapshot: that could discard other writers' committed work.
                throw new InvalidOperationException($"音乐库升级失败，原数据已保留。升级前备份：{backupPath ?? "内存数据库"}", ex);
            }
        }
        finally
        {
            try { if (openedHere) await connection.CloseAsync().ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }

    private static async Task<HashSet<string>> GetTrackColumnsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(\"Tracks\");";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetString(1));
        return result;
    }

    private static async Task<bool> IndexMatchesAsync(SqliteConnection connection, string name, string filter, string[] columns, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='index' AND name=$name;";
        command.Parameters.AddWithValue("$name", name);
        var sql = (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string;
        if (sql is null) return false;
        var normalized = string.Concat(sql.Where(c => !char.IsWhiteSpace(c) && c != '"' && c != '[' && c != ']'));
        if (!normalized.Contains("CREATEUNIQUEINDEX", StringComparison.OrdinalIgnoreCase) ||
            !normalized.TrimEnd(';').EndsWith("WHERE" + filter, StringComparison.OrdinalIgnoreCase)) return false;
        command.CommandText = $"PRAGMA index_xinfo(\"{name}\");";
        command.Parameters.Clear();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var actual = new List<(string Name, string Collation)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            if (reader.GetInt32(5) == 1 && !reader.IsDBNull(2)) actual.Add((reader.GetString(2), reader.GetString(4)));
        return actual.Select(x => x.Name).SequenceEqual(columns, StringComparer.OrdinalIgnoreCase) &&
               string.Equals(actual[0].Collation, "NOCASE", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> BackupAsync(SqliteConnection connection, SqliteConnectionStringBuilder settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsTransient(settings)) return null;
        var path = Path.GetFullPath(connection.DataSource) + $".backup-v1-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db";
        var pendingPath = path + ".pending";
        try
        {
            var backupSettings = new SqliteConnectionStringBuilder { DataSource = pendingPath, Pooling = false };
            await using (var backup = new SqliteConnection(backupSettings.ToString()))
            {
                await backup.OpenAsync(cancellationToken).ConfigureAwait(false);
                connection.BackupDatabase(backup);
                cancellationToken.ThrowIfCancellationRequested();
                // Make the snapshot portable as one file, regardless of the source journal mode.
                await ExecuteAsync(backup, "PRAGMA journal_mode=DELETE;", null, cancellationToken).ConfigureAwait(false);
                await using var check = backup.CreateCommand();
                check.CommandText = "PRAGMA integrity_check;";
                if (!string.Equals((await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false)) as string, "ok", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("升级前备份未通过完整性检查，原数据库未修改。");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(pendingPath, path);
            return path;
        }
        catch
        {
            try { File.Delete(pendingPath); } catch (IOException) { }
            throw;
        }
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsTransient(SqliteConnectionStringBuilder settings) =>
        settings.Mode == SqliteOpenMode.Memory || string.IsNullOrEmpty(settings.DataSource) || settings.DataSource == ":memory:";

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
