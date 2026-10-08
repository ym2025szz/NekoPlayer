using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Models;
using NekoPlayer.Infrastructure.Data;

namespace NekoPlayer.Tests;

public sealed class DataUpgradeTests
{
    [Fact]
    public async Task LegacySchemaUpgradePreservesIdentityUserStateAndRelations()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true);
        await fixture.InitializeAsync();
        await using var db = fixture.Factory.CreateDbContext();
        var track = Assert.Single(await db.Tracks.AsNoTracking().ToListAsync());
        Assert.Equal(fixture.LegacyTrackId, track.Id);
        Assert.Equal(TrackSourceKind.Local, track.SourceKind);
        Assert.True(track.IsFavorite);
        Assert.Equal(7, track.PlayCount);
        Assert.Equal("{}", track.ProviderMetadataJson);
        Assert.Equal(fixture.LegacyTrackId, (await db.PlaylistTracks.SingleAsync()).TrackId);
        Assert.Equal(fixture.LegacyTrackId, (await db.PlaybackHistories.SingleAsync()).TrackId);
        Assert.Equal(DatabaseSchemaUpgrade.CurrentVersion, await fixture.VersionAsync());

        var backup = Assert.Single(fixture.Backups);
        await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await snapshot.OpenAsync();
        Assert.Equal(1L, await DataUpgradeFixture.ScalarAsync(snapshot, "SELECT COUNT(*) FROM Tracks;"));
        Assert.Equal(0L, await DataUpgradeFixture.ScalarAsync(snapshot, "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='SourceKind';"));
    }

    [Fact]
    public async Task BackupContainsCommittedWalRowsAndIsStandalone()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true, wal: true);
        Assert.True(File.Exists(fixture.DatabasePath + "-wal"));
        await fixture.InitializeAsync();
        var backup = Assert.Single(fixture.Backups);
        await using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        await snapshot.OpenAsync();
        Assert.Equal("原来的歌曲", await DataUpgradeFixture.ScalarAsync(snapshot, "SELECT Title FROM Tracks;"));
        Assert.Equal(1L, await DataUpgradeFixture.ScalarAsync(snapshot, "SELECT COUNT(*) FROM PlaybackHistories;"));
        Assert.Equal("ok", await DataUpgradeFixture.ScalarAsync(snapshot, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task RepeatedAndConcurrentInitializationIsIdempotent()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => fixture.InitializeAsync()));
        await fixture.InitializeAsync();
        Assert.Single(fixture.Backups);
        await using var db = fixture.Factory.CreateDbContext();
        Assert.Single(await db.Tracks.ToListAsync());
        Assert.Equal(2, await fixture.VersionAsync());
    }

    [Fact]
    public async Task FailedUpgradeRollsBackColumnsIndexesAndVersion()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true, duplicatePaths: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InitializeAsync());
        Assert.Contains("原数据已保留", error.Message);
        Assert.Single(fixture.Backups);
        Assert.Equal(0, await fixture.VersionAsync());
        Assert.Equal(0L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper,
            "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='SourceKind';"));
        Assert.Equal(2L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper, "SELECT COUNT(*) FROM Tracks;"));
        Assert.Equal(1L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper, "SELECT COUNT(*) FROM PlaylistTracks;"));
        Assert.Equal(0L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper,
            "SELECT COUNT(*) FROM sqlite_master WHERE name='IX_Tracks_ProviderId_ProviderTrackId';"));
    }

    [Fact]
    public async Task FutureVersionIsRejectedWithoutChangingDatabase()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true);
        await DataUpgradeFixture.ExecuteAsync(fixture.Keeper, "PRAGMA user_version=99;");
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.InitializeAsync());
        Assert.Equal(99, await fixture.VersionAsync());
        Assert.Empty(fixture.Backups);
        Assert.Equal(0L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper,
            "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='SourceKind';"));
    }

    [Fact]
    public async Task CancelledUpgradeLeavesLegacySchemaUntouched()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync(legacy: true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.InitializeAsync(cancellation.Token));
        Assert.Empty(fixture.Backups);
        Assert.Equal(0, await fixture.VersionAsync());
        Assert.Equal(0L, await DataUpgradeFixture.ScalarAsync(fixture.Keeper,
            "SELECT COUNT(*) FROM pragma_table_info('Tracks') WHERE name='SourceKind';"));
    }

    [Fact]
    public async Task FreshSchemaHasFilteredIndexesAndDoesNotMapRuntimeAvailability()
    {
        await using var fixture = await DataUpgradeFixture.CreateAsync();
        await fixture.InitializeAsync();
        await using var db = fixture.Factory.CreateDbContext();
        db.Tracks.AddRange(
            new Track { FilePath = "C:\\Music\\song.mp3" },
            new Track { SourceKind = TrackSourceKind.Online, ProviderId = "qq", ProviderTrackId = "1", Availability = MusicAvailability.Preview },
            new Track { SourceKind = TrackSourceKind.Online, ProviderId = "qq", ProviderTrackId = "2" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.All(await db.Tracks.ToListAsync(), track => Assert.Equal(MusicAvailability.Unknown, track.Availability));
        Assert.Null(db.Model.FindEntityType(typeof(Track))!.FindProperty(nameof(Track.Availability)));
        Assert.Empty(fixture.Backups);
        Assert.Equal(2, await fixture.VersionAsync());
        db.Tracks.Add(new Track { FilePath = "c:\\music\\SONG.mp3" });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}

internal sealed class DataUpgradeFixture : IAsyncDisposable
{
    private DataUpgradeFixture(string root, SqliteConnection keeper, CatalogDbContextFactory factory)
    {
        Root = root;
        Keeper = keeper;
        Factory = factory;
    }

    public string Root { get; }
    public string DatabasePath => Path.Combine(Root, "library.db");
    public Guid LegacyTrackId { get; } = Guid.NewGuid();
    public Guid LegacyPlaylistId { get; } = Guid.NewGuid();
    public SqliteConnection Keeper { get; }
    public CatalogDbContextFactory Factory { get; }
    public string[] Backups => Directory.GetFiles(Root, "library.db.backup-*.db");

    public static async Task<DataUpgradeFixture> CreateAsync(bool legacy = false, bool wal = false, bool duplicatePaths = false)
    {
        var root = Path.Combine(Path.GetTempPath(), "NekoPlayerDataUpgradeTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "library.db"), Pooling = false }.ToString();
        var keeper = new SqliteConnection(connectionString);
        await keeper.OpenAsync();
        var fixture = new DataUpgradeFixture(root, keeper, new CatalogDbContextFactory(
            new DbContextOptionsBuilder<NekoPlayerDbContext>().UseSqlite(connectionString).Options));
        if (wal) await ExecuteAsync(keeper, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;");
        if (legacy) await fixture.CreateLegacyAsync(duplicatePaths);
        return fixture;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = Factory.CreateDbContext();
        await DatabaseSchemaUpgrade.InitializeAsync(db, cancellationToken);
    }

    public async Task<int> VersionAsync() => Convert.ToInt32(await ScalarAsync(Keeper, "PRAGMA user_version;"));

    private async Task CreateLegacyAsync(bool duplicatePaths)
    {
        await ExecuteAsync(Keeper, """
            CREATE TABLE Tracks (
                Id TEXT NOT NULL PRIMARY KEY, FilePath TEXT COLLATE NOCASE NOT NULL,
                Title TEXT NOT NULL, Artist TEXT NOT NULL, Album TEXT NOT NULL, Genre TEXT NOT NULL,
                Duration INTEGER NOT NULL, TrackNumber INTEGER NOT NULL, Year INTEGER NOT NULL,
                SampleRate INTEGER NOT NULL, Channels INTEGER NOT NULL, BitRate INTEGER NOT NULL,
                CodecName TEXT NOT NULL, CoverCachePath TEXT NULL, IsFavorite INTEGER NOT NULL,
                PlayCount INTEGER NOT NULL, AddedAt TEXT NOT NULL, LastPlayedAt TEXT NULL,
                FileSize INTEGER NOT NULL, FileLastWriteTimeUtc TEXT NOT NULL);
            CREATE TABLE Playlists (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CreatedAt TEXT NOT NULL, UpdatedAt TEXT NOT NULL);
            CREATE TABLE PlaylistTracks (PlaylistId TEXT NOT NULL, TrackId TEXT NOT NULL, SortOrder INTEGER NOT NULL,
                PRIMARY KEY (PlaylistId, TrackId), FOREIGN KEY (PlaylistId) REFERENCES Playlists(Id) ON DELETE CASCADE,
                FOREIGN KEY (TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE);
            CREATE TABLE PlaybackHistories (Id TEXT NOT NULL PRIMARY KEY, TrackId TEXT NOT NULL, PlayedAt TEXT NOT NULL,
                LastPosition INTEGER NOT NULL, FOREIGN KEY (TrackId) REFERENCES Tracks(Id) ON DELETE CASCADE);
            CREATE TABLE LibraryFolders (Id TEXT NOT NULL PRIMARY KEY, FolderPath TEXT NOT NULL, IncludeSubdirectories INTEGER NOT NULL, LastScanAt TEXT NULL);
            CREATE INDEX IX_PlaylistTracks_PlaylistId_SortOrder ON PlaylistTracks(PlaylistId, SortOrder);
            CREATE INDEX IX_PlaylistTracks_TrackId ON PlaylistTracks(TrackId);
            CREATE INDEX IX_PlaybackHistories_PlayedAt ON PlaybackHistories(PlayedAt);
            CREATE INDEX IX_PlaybackHistories_TrackId ON PlaybackHistories(TrackId);
            """);
        if (!duplicatePaths) await ExecuteAsync(Keeper, "CREATE UNIQUE INDEX IX_Tracks_FilePath ON Tracks(FilePath);");
        await InsertLegacyTrackAsync(LegacyTrackId);
        if (duplicatePaths) await InsertLegacyTrackAsync(Guid.NewGuid());
        await using var relations = Keeper.CreateCommand();
        relations.CommandText = """
            INSERT INTO Playlists VALUES ($playlist, '已有歌单', '2026-01-01 00:00:00', '2026-01-01 00:00:00');
            INSERT INTO PlaylistTracks VALUES ($playlist, $track, 0);
            INSERT INTO PlaybackHistories VALUES ($history, $track, '2026-01-01 00:00:00', 123000000);
            """;
        relations.Parameters.AddWithValue("$playlist", LegacyPlaylistId);
        relations.Parameters.AddWithValue("$track", LegacyTrackId);
        relations.Parameters.AddWithValue("$history", Guid.NewGuid());
        await relations.ExecuteNonQueryAsync();
    }

    private async Task InsertLegacyTrackAsync(Guid id)
    {
        await using var command = Keeper.CreateCommand();
        command.CommandText = """
            INSERT INTO Tracks VALUES ($id, 'C:\Music\legacy.mp3', '原来的歌曲', '歌手', '专辑', '',
                1800000000, 1, 2026, 44100, 2, 320000, 'mp3', NULL, 1, 7,
                '2026-01-01 00:00:00', '2026-01-02 00:00:00', 100, '2026-01-01 00:00:00');
            """;
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task<object?> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    public static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Keeper.DisposeAsync();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}

internal sealed class CatalogDbContextFactory(DbContextOptions<NekoPlayerDbContext> options) : IDbContextFactory<NekoPlayerDbContext>
{
    public NekoPlayerDbContext CreateDbContext() => new(options);
}
