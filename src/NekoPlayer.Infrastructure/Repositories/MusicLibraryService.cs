using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using Serilog;

namespace NekoPlayer.Infrastructure.Repositories;

public sealed class MusicLibraryService(
    IDbContextFactory<NekoPlayerDbContext> contextFactory,
    TrackMetadataReader metadataReader,
    ITrackStateStore trackStateStore) : IMusicLibraryService
{
    private const int ExistingPathBatchSize = 400;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly SemaphoreSlim _favoriteGate = new(1, 1);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("SQLite 数据库初始化完成");
    }

    public async Task<IReadOnlyList<Track>> GetTracksAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().OrderBy(x => x.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Track>> GetFavoritesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.Tracks.AsNoTracking().Where(x => x.IsFavorite).OrderBy(x => x.Title).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<RecentTrack>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var histories = await db.PlaybackHistories.AsNoTracking().Include(x => x.Track)
            .OrderByDescending(x => x.PlayedAt).Take(count * 3).ToListAsync(cancellationToken).ConfigureAwait(false);
        return histories.Where(x => x.Track is not null).GroupBy(x => x.TrackId).Select(x => x.First()).Take(count)
            .Select(x => new RecentTrack(x.Id, x.Track!, x.PlayedAt, x.LastPosition)).ToArray();
    }

    public Task<ImportResult> ImportAsync(
        IEnumerable<string> files,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveImportAsync("文件", async state =>
        {
            Report(state, progress, ImportStage.Enumerating, "正在整理所选音乐文件……", null, true);
            var candidates = await Task.Run(
                () => NormalizeCandidates(files, cancellationToken), cancellationToken).ConfigureAwait(false);
            return await ImportCoreAsync(candidates, state, progress, cancellationToken).ConfigureAwait(false);
        }, progress, cancellationToken);

    public Task<ImportResult> ScanFolderAsync(
        string folder,
        bool recursive,
        IProgress<ImportProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunExclusiveImportAsync("文件夹", async state =>
        {
            var normalizedFolder = WindowsPath.NormalizeDirectory(folder);
            Report(state, progress, ImportStage.Enumerating, "正在查找音乐文件……", Path.GetFileName(normalizedFolder), true);
            var option = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
            var candidates = await Task.Run(() =>
            {
                var result = new List<string>();
                foreach (var path in Directory.EnumerateFiles(normalizedFolder, "*", option))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AudioFileExtensions.IsSupported(path)) result.Add(WindowsPath.NormalizeFile(path));
                }
                return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            }, cancellationToken).ConfigureAwait(false);

            await SaveLibraryFolderAsync(normalizedFolder, recursive, cancellationToken).ConfigureAwait(false);
            return await ImportCoreAsync(candidates, state, progress, cancellationToken).ConfigureAwait(false);
        }, progress, cancellationToken);

    public async Task SetFavoriteAsync(Guid trackId, bool favorite, CancellationToken cancellationToken = default)
    {
        await _favoriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            var track = await db.Tracks.FindAsync([trackId], cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException("未找到要更新收藏状态的歌曲。");
            if (track.IsFavorite != favorite)
            {
                track.IsFavorite = favorite;
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            trackStateStore.PublishFavoriteChanged(trackId, favorite);
            Log.Information("收藏状态已保存：TrackId={TrackId}, IsFavorite={IsFavorite}", trackId, favorite);
        }
        finally
        {
            _favoriteGate.Release();
        }
    }

    public async Task<RecentRemoval?> RemoveRecentAsync(Guid historyId, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var selected = await db.PlaybackHistories.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == historyId, cancellationToken).ConfigureAwait(false);
        if (selected is null) return null;

        var histories = await db.PlaybackHistories.Where(x => x.TrackId == selected.TrackId)
            .OrderByDescending(x => x.PlayedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var snapshot = histories.Select(x => new PlaybackHistorySnapshot(x.Id, x.TrackId, x.PlayedAt, x.LastPosition)).ToArray();
        db.PlaybackHistories.RemoveRange(histories);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("已从最近播放移除：TrackId={TrackId}, HistoryCount={HistoryCount}", selected.TrackId, histories.Count);
        return new RecentRemoval(selected.TrackId, snapshot);
    }

    public async Task RestoreRecentAsync(RecentRemoval removal, CancellationToken cancellationToken = default)
    {
        if (removal.Histories.Count == 0) return;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var trackExists = await db.Tracks.AnyAsync(x => x.Id == removal.TrackId, cancellationToken).ConfigureAwait(false);
        if (!trackExists) throw new InvalidOperationException("歌曲已不在音乐库中，无法恢复播放记录。");
        var ids = removal.Histories.Select(x => x.Id).ToArray();
        var existing = await db.PlaybackHistories.Where(x => ids.Contains(x.Id)).Select(x => x.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        db.PlaybackHistories.AddRange(removal.Histories.Where(x => !existing.Contains(x.Id)).Select(x => new PlaybackHistory
        {
            Id = x.Id,
            TrackId = x.TrackId,
            PlayedAt = x.PlayedAt,
            LastPosition = x.LastPosition
        }));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("最近播放记录已恢复：TrackId={TrackId}, HistoryCount={HistoryCount}", removal.TrackId, removal.Histories.Count);
    }

    public async Task<int> ClearRecentAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var histories = await db.PlaybackHistories.ToListAsync(cancellationToken).ConfigureAwait(false);
        db.PlaybackHistories.RemoveRange(histories);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        Log.Information("最近播放记录已清空：{Count}", histories.Count);
        return histories.Count;
    }

    public async Task RemoveFromLibraryAsync(Guid trackId, CancellationToken cancellationToken = default)
    {
        await RemoveFromLibraryAsync([trackId], cancellationToken).ConfigureAwait(false);
    }

    public async Task<LibraryRemovalResult> RemoveFromLibraryAsync(IEnumerable<Guid> trackIds, CancellationToken cancellationToken = default)
    {
        var requested = trackIds.Distinct().ToArray();
        if (requested.Length == 0) return new LibraryRemovalResult(0, 0, []);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var tracks = await db.Tracks.Where(x => requested.Contains(x.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        db.Tracks.RemoveRange(tracks);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        var removedIds = tracks.Select(x => x.Id).ToArray();
        Log.Information("已从音乐库移除 {RemovedCount}/{RequestedCount} 首歌曲；未删除磁盘文件", removedIds.Length, requested.Length);
        return new LibraryRemovalResult(requested.Length, removedIds.Length, removedIds);
    }

    public async Task RecordPlaybackAsync(Guid trackId, TimeSpan position, CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var track = await db.Tracks.FindAsync([trackId], cancellationToken).ConfigureAwait(false);
        if (track is null) return;
        var now = DateTime.UtcNow;
        var recent = await db.PlaybackHistories.AnyAsync(
            x => x.TrackId == trackId && x.PlayedAt > now.AddSeconds(-20), cancellationToken).ConfigureAwait(false);
        if (!recent) db.PlaybackHistories.Add(new PlaybackHistory { TrackId = trackId, PlayedAt = now, LastPosition = position });
        track.LastPlayedAt = now;
        if (position >= TimeSpan.FromSeconds(10)) track.PlayCount++;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<ImportResult> RunExclusiveImportAsync(
        string sourceType,
        Func<ImportState, Task<ImportResult>> action,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var state = new ImportState();
        var entered = false;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            entered = await _importGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
            if (!entered) throw new InvalidOperationException("已有导入任务正在运行，请等待当前任务结束。");
            Log.Information("音乐导入任务开始，来源类型：{SourceType}", sourceType);
            return await action(state).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Log.Information("音乐导入任务已取消：已处理 {Processed}/{Discovered}", state.Processed, state.Discovered);
            Report(state, progress, ImportStage.Cancelled, "导入已取消", state.CurrentFileName, false);
            return state.ToResult(ImportStage.Cancelled);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "音乐导入任务失败，来源类型：{SourceType}", sourceType);
            state.Failures.Add(new ImportFailure(string.Empty, string.Empty, FriendlyMessage(ex)));
            state.Failed++;
            Report(state, progress, ImportStage.Failed, "导入过程中遇到问题，请查看日志。", state.CurrentFileName, false);
            return state.ToResult(ImportStage.Failed);
        }
        finally
        {
            if (entered) _importGate.Release();
            stopwatch.Stop();
            Log.Information(
                "音乐导入任务结束：发现 {Discovered}，处理 {Processed}，新增 {Imported}，更新 {Updated}，跳过 {Skipped}，失败 {Failed}，耗时 {ElapsedMs} ms",
                state.Discovered, state.Processed, state.Imported, state.Updated, state.Skipped, state.Failed, stopwatch.ElapsedMilliseconds);
        }
    }

    private async Task<ImportResult> ImportCoreAsync(
        IReadOnlyList<string> files,
        ImportState state,
        IProgress<ImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        state.Discovered = files.Count;
        Log.Information("导入发现 {Discovered} 个受支持的音频文件", state.Discovered);
        Report(state, progress, ImportStage.ReadingMetadata,
            files.Count == 0 ? "没有找到可导入的音乐文件" : "准备读取音乐信息……", null, false);

        if (files.Count == 0)
        {
            Report(state, progress, ImportStage.Completed, "没有找到可导入的音乐文件", null, false);
            return state.ToResult(ImportStage.Completed);
        }

        var existing = await LoadExistingTracksAsync(files, cancellationToken).ConfigureAwait(false);
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.CurrentFileName = Path.GetFileName(path);
            Log.Debug("开始处理导入文件：{FilePath}", path);
            try
            {
                var fileInfo = new FileInfo(path);
                if (!fileInfo.Exists) throw new FileNotFoundException("文件不存在或已被移动。", path);

                existing.TryGetValue(path, out var existingTrack);
                if (existingTrack is not null &&
                    existingTrack.FileSize == fileInfo.Length &&
                    existingTrack.FileLastWriteTimeUtc == fileInfo.LastWriteTimeUtc)
                {
                    state.Skipped++;
                    state.Processed++;
                    Log.Debug("跳过未变化的已存在歌曲：{FilePath}", path);
                    Report(state, progress, ImportStage.ReadingMetadata, "歌曲已存在，正在继续……", state.CurrentFileName, false);
                    continue;
                }

                var parsed = await metadataReader.ReadAsync(path, stage =>
                {
                    var text = stage == ImportStage.AnalyzingMedia ? "正在分析媒体信息……" : "正在读取歌曲信息……";
                    Report(state, progress, stage, text, state.CurrentFileName, false);
                }, cancellationToken).ConfigureAwait(false);

                Report(state, progress, ImportStage.Saving, "正在保存到音乐库……", state.CurrentFileName, false);
                var savedId = await SaveAndConfirmTrackAsync(parsed, existingTrack, cancellationToken).ConfigureAwait(false);
                state.AffectedTrackIds.Add(savedId);
                if (existingTrack is null) state.Imported++;
                else state.Updated++;
                state.Processed++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                state.Failed++;
                state.Processed++;
                state.Failures.Add(new ImportFailure(path, Path.GetFileName(path), FriendlyMessage(ex)));
                Log.Error(ex, "导入音乐失败：{FilePath}", path);
            }

            Report(state, progress, ImportStage.ReadingMetadata, "正在处理音乐文件……", state.CurrentFileName, false);
        }

        var finalStage = state.Failed > 0 && state.Imported + state.Updated + state.Skipped == 0
            ? ImportStage.Failed
            : ImportStage.Completed;
        Report(state, progress, finalStage,
            finalStage == ImportStage.Failed ? "导入过程中遇到问题，请查看失败详情或日志。" : "音乐文件处理完成", null, false);
        return state.ToResult(finalStage);
    }

    private async Task<Dictionary<string, Track>> LoadExistingTracksAsync(
        IReadOnlyList<string> paths,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, Track>(StringComparer.OrdinalIgnoreCase);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        foreach (var batch in paths.Chunk(ExistingPathBatchSize))
        {
            var localBatch = batch.ToArray();
            var tracks = await db.Tracks.AsNoTracking().Where(x => localBatch.Contains(x.FilePath))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var track in tracks) result[WindowsPath.NormalizeFile(track.FilePath)] = track;
        }
        return result;
    }

    private async Task<Guid> SaveAndConfirmTrackAsync(
        Track parsed,
        Track? existing,
        CancellationToken cancellationToken)
    {
        var trackId = existing?.Id ?? parsed.Id;
        await using (var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            if (existing is null)
            {
                parsed.FilePath = WindowsPath.NormalizeFile(parsed.FilePath);
                db.Tracks.Add(parsed);
            }
            else
            {
                var target = await db.Tracks.FindAsync([existing.Id], cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("待更新的歌曲记录已不存在。");
                CopyMetadata(parsed, target);
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var verifyDb = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var confirmed = await verifyDb.Tracks.AsNoTracking().AnyAsync(
            x => x.Id == trackId && x.FilePath == parsed.FilePath, cancellationToken).ConfigureAwait(false);
        if (!confirmed) throw new InvalidOperationException("数据库保存完成，但重新查询未找到歌曲记录。");
        return trackId;
    }

    private async Task SaveLibraryFolderAsync(string folder, bool recursive, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var folders = await db.LibraryFolders.ToListAsync(cancellationToken).ConfigureAwait(false);
        var existing = folders.FirstOrDefault(x =>
            string.Equals(WindowsPath.NormalizeDirectory(x.FolderPath), folder, StringComparison.OrdinalIgnoreCase));
        if (existing is null)
            db.LibraryFolders.Add(new LibraryFolder { FolderPath = folder, IncludeSubdirectories = recursive, LastScanAt = DateTime.UtcNow });
        else
        {
            existing.FolderPath = folder;
            existing.IncludeSubdirectories = recursive;
            existing.LastScanAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string[] NormalizeCandidates(IEnumerable<string> files, CancellationToken cancellationToken)
    {
        var result = new List<string>();
        foreach (var path in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!AudioFileExtensions.IsSupported(path)) continue;
            result.Add(WindowsPath.NormalizeFile(path));
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void Report(
        ImportState state,
        IProgress<ImportProgress>? progress,
        ImportStage stage,
        string text,
        string? fileName,
        bool indeterminate)
    {
        double? percentage = !indeterminate && state.Discovered > 0
            ? Math.Clamp(state.Processed * 100d / state.Discovered, 0, 100)
            : null;
        progress?.Report(new ImportProgress(
            stage,
            state.Discovered,
            state.Processed,
            state.Imported,
            state.Updated,
            state.Skipped,
            state.Failed,
            fileName,
            text,
            percentage,
            indeterminate));
    }

    private static string FriendlyMessage(Exception exception) => exception switch
    {
        TimeoutException => exception.Message,
        FileNotFoundException => "文件不存在或已被移动。",
        UnauthorizedAccessException => "没有权限读取该文件。",
        IOException => "读取文件或保存数据库时发生 I/O 错误。",
        DbUpdateException => "数据库保存失败，可能存在重复路径或数据冲突。",
        _ => string.IsNullOrWhiteSpace(exception.Message) ? "处理文件时发生未知错误。" : exception.Message
    };

    private static void CopyMetadata(Track source, Track target)
    {
        target.FilePath = WindowsPath.NormalizeFile(source.FilePath);
        target.Title = source.Title;
        target.Artist = source.Artist;
        target.Album = source.Album;
        target.Genre = source.Genre;
        target.Duration = source.Duration;
        target.TrackNumber = source.TrackNumber;
        target.Year = source.Year;
        target.SampleRate = source.SampleRate;
        target.Channels = source.Channels;
        target.BitRate = source.BitRate;
        target.CodecName = source.CodecName;
        target.CoverCachePath = source.CoverCachePath;
        target.FileSize = source.FileSize;
        target.FileLastWriteTimeUtc = source.FileLastWriteTimeUtc;
    }

    private sealed class ImportState
    {
        public int Discovered { get; set; }
        public int Processed { get; set; }
        public int Imported { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public string? CurrentFileName { get; set; }
        public List<ImportFailure> Failures { get; } = [];
        public List<Guid> AffectedTrackIds { get; } = [];

        public ImportResult ToResult(ImportStage stage) => new(
            stage,
            Discovered,
            Processed,
            Imported,
            Updated,
            Skipped,
            Failed,
            Failures.ToArray(),
            AffectedTrackIds.Distinct().ToArray());
    }
}
