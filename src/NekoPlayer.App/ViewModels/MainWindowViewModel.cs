using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;
using Serilog;

namespace NekoPlayer.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly IMusicLibraryService _library;
    private readonly IPlaylistService _playlists;
    private readonly IAudioPlayerService _audio;
    private readonly IPlaybackQueueService _queue;
    private readonly ILyricsService _lyrics;
    private readonly ISettingsService _settingsService;
    private readonly IUserDataPaths _paths;
    private readonly IFfmpegLocator _ffmpeg;
    private readonly ISpectrumService _spectrum;
    private readonly ITrackStateStore _trackStateStore;
    private readonly IClock _clock;
    private readonly DispatcherTimer _greetingTimer;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _playCountCts;
    private CancellationTokenSource? _snackbarCts;
    private readonly SeekRequestCoordinator _seekCoordinator = new();
    private readonly UndoActionCoordinator _undoCoordinator = new();
    private AppSettings _settings = new();
    private IReadOnlyList<Track> _allTracks = [];
    private bool _initialized;
    private bool _disposed;
    private int _seekRequestVersion;
    private Task? _activeImportTask;
    private bool _favoriteOperationInProgress;
    private bool _playlistOperationInProgress;
    private bool _libraryRemovalInProgress;
    private Guid[] _playlistPickerTrackIds = [];
    private Guid[] _pendingLibraryRemovalIds = [];

    public MainWindowViewModel(
        IMusicLibraryService library, IPlaylistService playlists, IAudioPlayerService audio,
        IPlaybackQueueService queue, ILyricsService lyrics, ISettingsService settingsService,
        IUserDataPaths paths, IFfmpegLocator ffmpeg, ISpectrumService spectrum,
        ITrackStateStore trackStateStore, IClock clock)
    {
        _library = library; _playlists = playlists; _audio = audio; _queue = queue; _lyrics = lyrics;
        _settingsService = settingsService; _paths = paths; _ffmpeg = ffmpeg;
        _spectrum = spectrum;
        _trackStateStore = trackStateStore;
        _clock = clock;
        _greetingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _greetingTimer.Tick += OnGreetingTimerTick;
        _trackStateStore.FavoriteChanged += OnFavoriteChanged;
        _audio.StateChanged += (_, state) => Dispatcher.UIThread.Post(() => ApplyPlaybackState(state));
        _audio.PositionChanged += (_, position) => Dispatcher.UIThread.Post(() => UpdatePosition(position));
        _audio.PlaybackCompleted += (_, _) => _ = HandleCompletedAsync();
        _audio.PlaybackFailed += (_, ex) => Dispatcher.UIThread.Post(() => StatusMessage = ex.Message);
        _queue.QueueChanged += (_, _) => Dispatcher.UIThread.Post(RefreshQueue);
        _queue.CurrentChanged += (_, track) => Dispatcher.UIThread.Post(() => { CurrentTrack = track; RefreshQueue(); });
        spectrum.SpectrumUpdated += (_, bands) => Dispatcher.UIThread.Post(() => UpdateSpectrum(bands));
        for (var i = 0; i < 40; i++) SpectrumBars.Add(3);
    }

    public ObservableCollection<Track> Tracks { get; } = [];
    public ObservableCollection<Track> Favorites { get; } = [];
    public ObservableCollection<RecentTrack> RecentTracks { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];
    public ObservableCollection<Track> PlaylistTracks { get; } = [];
    public ObservableCollection<QueueItemViewModel> QueueTracks { get; } = [];
    public ObservableCollection<Track> SelectedLibraryTracks { get; } = [];
    public ObservableCollection<TrackPickerItemViewModel> TrackPickerItems { get; } = [];
    public ObservableCollection<TrackPickerItemViewModel> VisibleTrackPickerItems { get; } = [];
    public ObservableCollection<PlaylistChoiceViewModel> PlaylistChoices { get; } = [];
    public ObservableCollection<LyricsRowViewModel> LyricsRows { get; } = [];
    public ObservableCollection<double> SpectrumBars { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["标题", "歌手", "专辑", "添加时间", "时长"];
    public IReadOnlyList<string> PlayModeOptions { get; } = ["顺序播放", "列表循环", "单曲循环", "随机播放"];

    [ObservableProperty] private string currentPage = "首页";
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string sortField = "标题";
    [ObservableProperty] private string statusMessage = "准备就绪";
    [ObservableProperty] private string scanStatus = string.Empty;
    [ObservableProperty] private string importStatusText = string.Empty;
    [ObservableProperty] private ImportProgress importProgress = new(ImportStage.Idle, 0, 0, 0, 0, 0, 0, null, string.Empty, null, false);
    [ObservableProperty] private bool isLibraryLoading;
    [ObservableProperty] private bool hasLibraryError;
    [ObservableProperty] private string libraryErrorMessage = string.Empty;
    [ObservableProperty] private string playbackStateText = "未播放";
    [ObservableProperty] private PlaybackState playbackState = PlaybackState.Idle;
    [ObservableProperty] private Track? selectedTrack;
    [ObservableProperty] private RecentTrack? selectedRecentTrack;
    [ObservableProperty] private Track? currentTrack;
    [ObservableProperty] private Playlist? selectedPlaylist;
    [ObservableProperty] private string playlistName = "新建歌单";
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private bool isPlaying;
    [ObservableProperty] private bool isMuted;
    [ObservableProperty] private bool isQueueVisible;
    [ObservableProperty] private bool deleteConfirmationVisible;
    [ObservableProperty] private bool includeSubdirectories = true;
    [ObservableProperty] private bool spectrumEnabled = true;
    [ObservableProperty] private bool reduceMotion;
    [ObservableProperty] private int spectrumFps = 30;
    [ObservableProperty] private double volumePercent = 75;
    [ObservableProperty] private double positionSeconds;
    [ObservableProperty] private double seekSeconds;
    [ObservableProperty] private double durationSeconds;
    [ObservableProperty] private string currentTimeText = "0:00";
    [ObservableProperty] private string durationText = "0:00";
    [ObservableProperty] private string playModeText = "列表循环";
    [ObservableProperty] private bool isUserSeeking;
    [ObservableProperty] private bool isSeekPending;
    [ObservableProperty] private PageLoadState favoritesPageState = PageLoadState.Empty;
    [ObservableProperty] private PageLoadState recentPageState = PageLoadState.Empty;
    [ObservableProperty] private string favoritesErrorMessage = string.Empty;
    [ObservableProperty] private string recentErrorMessage = string.Empty;
    [ObservableProperty] private bool recentClearConfirmationVisible;
    [ObservableProperty] private ResponsiveLayoutMode layoutMode = ResponsiveLayoutMode.Wide;
    [ObservableProperty] private bool isSnackbarVisible;
    [ObservableProperty] private string snackbarMessage = string.Empty;
    [ObservableProperty] private SnackbarTone snackbarTone = SnackbarTone.Success;
    [ObservableProperty] private bool snackbarCanUndo;
    [ObservableProperty] private bool isTrackPickerVisible;
    [ObservableProperty] private bool isPlaylistPickerVisible;
    [ObservableProperty] private string playlistTrackSearchText = string.Empty;
    [ObservableProperty] private string playlistPickerNewName = "新建歌单";
    [ObservableProperty] private string trackPickerTargetName = string.Empty;
    [ObservableProperty] private bool libraryRemovalConfirmationVisible;
    [ObservableProperty] private string libraryRemovalConfirmationText = string.Empty;
    [ObservableProperty] private string greetingText = string.Empty;
    [ObservableProperty] private string localTimeText = string.Empty;

    public string CurrentTitle => CurrentTrack?.Title ?? "还没有播放歌曲";
    public string CurrentArtist => CurrentTrack?.Artist ?? "从本地音乐里挑一首吧";
    public string CurrentAlbum => CurrentTrack?.Album ?? "猫耳雷达正在待机";
    public string CurrentCoverPath => CurrentTrack?.CoverCachePath ?? string.Empty;
    public string FfmpegStatus => _ffmpeg.StatusMessage;
    public string FfmpegVersion => _ffmpeg.Version;
    public string FfmpegPath => _ffmpeg.BinaryDirectory;
    public string FfmpegSharedStatus => _ffmpeg.HasSharedLibraries ? "Shared DLL 完整" : "Shared DLL 缺失";
    public string AppVersion => AppVersionInfo.Version;
    public double WindowWidth => _settings.WindowWidth;
    public double WindowHeight => _settings.WindowHeight;
    public int TrackCount => _allTracks.Count;
    public int FilteredTrackCount => Tracks.Count;
    public int FavoriteCount => Favorites.Count;
    public int PlaylistCount => Playlists.Count;
    public bool IsHomePage => CurrentPage == "首页";
    public bool IsLibraryPage => CurrentPage == "本地音乐";
    public bool IsNowPlayingPage => CurrentPage == "正在播放";
    public bool IsRecentPage => CurrentPage == "最近播放";
    public bool IsFavoritesPage => CurrentPage == "我的收藏";
    public bool IsPlaylistsPage => CurrentPage == "我的歌单";
    public bool IsSettingsPage => CurrentPage == "设置";
    public bool IsWideLayout => LayoutMode == ResponsiveLayoutMode.Wide;
    public bool IsStandardLayout => LayoutMode == ResponsiveLayoutMode.Standard;
    public bool IsCompactLayout => LayoutMode == ResponsiveLayoutMode.Compact;
    public double NavigationWidth => IsCompactLayout ? 78 : 220;
    public bool ShowNavigationText => !IsCompactLayout;
    public Thickness PageMargin => IsCompactLayout ? new Thickness(14, 14, 14, 16) : new Thickness(24, 18, 24, 20);
    public Thickness ShellContentMargin => new(NavigationWidth, 0, 0, 0);
    public Thickness PageContentMargin => new(NavigationWidth + PageMargin.Left, PageMargin.Top, PageMargin.Right, PageMargin.Bottom);
    public double HeaderSearchWidth => IsCompactLayout ? 230 : 380;
    public bool ShowDecorativeArtwork => !IsCompactLayout;
    public bool ShowAlbumColumn => IsWideLayout;
    public bool ShowExtendedTrackActions => IsWideLayout;
    public bool ShowVolumeControls => !IsCompactLayout;
    public GridLength AlbumColumnWidth => ShowAlbumColumn ? new GridLength(1.2, GridUnitType.Star) : new GridLength(0);
    public GridLength ArtistColumnWidth => IsCompactLayout ? new GridLength(0.9, GridUnitType.Star) : new GridLength(1.2, GridUnitType.Star);
    public GridLength TrackActionColumnWidth => IsWideLayout ? new GridLength(214) : new GridLength(132);
    public double QueueDrawerWidth => IsCompactLayout ? 300 : 340;
    public double IconSize => IsCompactLayout ? 18 : IsWideLayout ? 22 : 20;
    public double IconButtonSize => IsCompactLayout ? 38 : IsWideLayout ? 42 : 40;
    public double PrimaryPlayButtonSize => IsCompactLayout ? 44 : IsWideLayout ? 48 : 46;
    public bool IsImportPanelVisible => ImportProgress.Stage != ImportStage.Idle;
    public bool IsImporting => ImportProgress.Stage is ImportStage.Enumerating or ImportStage.ReadingMetadata or ImportStage.AnalyzingMedia or ImportStage.Saving or ImportStage.RefreshingLibrary;
    public bool CanStartImport => !IsImporting;
    public bool CanCancelImport => ImportProgress.Stage is ImportStage.Enumerating or ImportStage.ReadingMetadata or ImportStage.AnalyzingMedia or ImportStage.Saving;
    public bool CanDismissImport => IsImportPanelVisible && !IsImporting;
    public bool ImportIsIndeterminate => ImportProgress.IsIndeterminate;
    public double ImportPercentage => ImportProgress.Percentage ?? 0;
    public string ImportCurrentFileName => ImportProgress.CurrentFileName ?? string.Empty;
    public string ImportCountsText => $"已处理 {ImportProgress.ProcessedCount} / {ImportProgress.DiscoveredCount} · 新增 {ImportProgress.ImportedCount} · 更新 {ImportProgress.UpdatedCount} · 跳过 {ImportProgress.SkippedCount} · 失败 {ImportProgress.FailedCount}";
    public bool HasImportFailures => ImportProgress.FailedCount > 0;
    public bool HasLibraryContent => !IsLibraryLoading && !HasLibraryError && Tracks.Count > 0;
    public bool IsLibraryEmpty => !IsLibraryLoading && !HasLibraryError && _allTracks.Count == 0;
    public bool IsSearchEmptyResult => !IsLibraryLoading && !HasLibraryError && _allTracks.Count > 0 && Tracks.Count == 0;
    public bool IsFavoritesLoading => FavoritesPageState == PageLoadState.Loading;
    public bool IsFavoritesEmpty => FavoritesPageState == PageLoadState.Empty;
    public bool HasFavoritesContent => FavoritesPageState == PageLoadState.Content;
    public bool IsFavoritesError => FavoritesPageState == PageLoadState.Error;
    public bool IsRecentLoading => RecentPageState == PageLoadState.Loading;
    public bool IsRecentEmpty => RecentPageState == PageLoadState.Empty;
    public bool HasRecentContent => RecentPageState == PageLoadState.Content;
    public bool IsRecentError => RecentPageState == PageLoadState.Error;
    public bool CanSeek => CurrentTrack is { FileExists: true } && _ffmpeg.IsAvailable && _audio.Duration > TimeSpan.Zero && PlaybackState is not PlaybackState.Loading;
    public PlaybackPrimaryAction PrimaryPlaybackAction => PlaybackActionMapper.Resolve(PlaybackState, CurrentTrack is not null, _queue.Items.Count > 0);
    public bool ShowPauseIcon => PrimaryPlaybackAction == PlaybackPrimaryAction.Pause;
    public bool ShowPlayIcon => PrimaryPlaybackAction is PlaybackPrimaryAction.Play or PlaybackPrimaryAction.Retry or PlaybackPrimaryAction.Disabled;
    public bool ShowPlaybackBusyIcon => PrimaryPlaybackAction == PlaybackPrimaryAction.Busy;
    public bool CanTogglePlay => PrimaryPlaybackAction is not PlaybackPrimaryAction.Disabled and not PlaybackPrimaryAction.Busy;
    public string PrimaryPlayTooltip => PlaybackActionMapper.Tooltip(PrimaryPlaybackAction);
    public string PrimaryPlayAutomationName => PrimaryPlaybackAction switch
    {
        PlaybackPrimaryAction.Pause => "暂停当前歌曲",
        PlaybackPrimaryAction.Play => "继续播放当前歌曲",
        PlaybackPrimaryAction.Retry => "重试播放当前歌曲",
        PlaybackPrimaryAction.Busy => "正在处理播放状态",
        _ => "暂无可播放歌曲"
    };
    public bool CanToggleCurrentFavorite => CurrentTrack is not null && !_favoriteOperationInProgress;
    public int SelectedLibraryTrackCount => SelectedLibraryTracks.Count;
    public bool HasLibrarySelection => SelectedLibraryTracks.Count > 0;
    public int SelectedTrackPickerCount => TrackPickerItems.Count(x => x.IsSelected && x.CanSelect);
    public bool CanConfirmTrackPicker => SelectedTrackPickerCount > 0 && !_playlistOperationInProgress;
    public bool HasPlaylistChoices => PlaylistChoices.Count > 0;
    public int SelectedPlaylistChoiceCount => PlaylistChoices.Count(x => x.IsSelected);
    public bool CanConfirmPlaylistPicker => SelectedPlaylistChoiceCount > 0 && _playlistPickerTrackIds.Length > 0 && !_playlistOperationInProgress;
    public bool IsLibraryRemovalInProgress => _libraryRemovalInProgress;

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        UpdateGreeting();
        _greetingTimer.Start();
        try
        {
            await _ffmpeg.ValidateAsync();
            OnPropertyChanged(nameof(FfmpegStatus));
            OnPropertyChanged(nameof(FfmpegVersion));
            OnPropertyChanged(nameof(FfmpegPath));
            OnPropertyChanged(nameof(FfmpegSharedStatus));
            await _library.InitializeAsync();
            _settings = await _settingsService.LoadAsync();
            VolumePercent = _settings.Volume * 100;
            IsMuted = _settings.IsMuted;
            IncludeSubdirectories = _settings.IncludeSubdirectories;
            SpectrumEnabled = _settings.SpectrumEnabled;
            SpectrumFps = Math.Clamp(_settings.SpectrumFps, 10, 60);
            ReduceMotion = _settings.ReduceMotion;
            _audio.Volume = _settings.Volume;
            _audio.IsMuted = _settings.IsMuted;
            _queue.PlayMode = _settings.PlayMode;
            _spectrum.FramesPerSecond = SpectrumFps;
            _spectrum.IsEnabled = SpectrumEnabled && IsNowPlayingPage;
            UpdatePlayModeText();
            await RefreshAllAsync();
            var requested = _settings.QueueTrackIds.Select(id => _allTracks.FirstOrDefault(x => x.Id == id)).Where(x => x is not null).Cast<Track>().ToArray();
            var restored = requested.Where(x => File.Exists(x.FilePath)).ToArray();
            _queue.Restore(restored, _settings.QueueIndex);
            if (_queue.Current is { } last && File.Exists(last.FilePath) && _ffmpeg.IsAvailable)
            {
                await _audio.LoadAsync(last.FilePath, _settings.LastPosition);
                CurrentTrack = last;
                await LoadLyricsAsync(last);
            }
            StatusMessage = requested.Length != restored.Length ? $"已跳过 {requested.Length - restored.Length} 首文件不存在的队列歌曲" : _ffmpeg.IsAvailable ? "音乐舱已就绪" : "界面和音乐库可用；播放前请放置 FFmpeg";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "应用初始化失败");
            HasLibraryError = true;
            LibraryErrorMessage = "本地音乐加载失败。";
            StatusMessage = "初始化遇到问题，请查看日志";
        }
    }

    public async Task ImportFilesAsync(IEnumerable<string> files)
    {
        if (IsImporting) return;
        _activeImportTask = RunImportAsync((progress, token) => _library.ImportAsync(files, progress, token));
        await _activeImportTask;
    }

    public async Task ImportFolderAsync(string folder)
    {
        if (IsImporting) return;
        _activeImportTask = RunImportAsync((progress, token) => _library.ScanFolderAsync(folder, IncludeSubdirectories, progress, token));
        await _activeImportTask;
    }

    public void UpdateLayout(double width)
    {
        var next = ResponsiveLayout.Resolve(width);
        if (LayoutMode != next) LayoutMode = next;
    }

    public void BeginSeek()
    {
        if (!CanSeek) return;
        IsUserSeeking = true;
        SeekSeconds = Math.Clamp(PositionSeconds, 0, DurationSeconds);
    }

    public void UpdateSeekPreview(double seconds)
    {
        if (!CanSeek) return;
        if (!IsUserSeeking) BeginSeek();
        SeekSeconds = Math.Clamp(seconds, 0, DurationSeconds);
    }

    public Task CommitSeekAsync(double seconds)
    {
        UpdateSeekPreview(seconds);
        return CommitSeekAsync(TimeSpan.Zero);
    }

    public void CancelSeek()
    {
        IsUserSeeking = false;
        IsSeekPending = false;
        SeekSeconds = PositionSeconds;
        CurrentTimeText = TimeFormatter.Format(TimeSpan.FromSeconds(Math.Max(0, PositionSeconds)));
    }

    public Task CommitSeekAsync() => CommitSeekAsync(TimeSpan.Zero);

    public async Task SeekRelativeAsync(double seconds)
    {
        if (!CanSeek) return;
        if (!IsUserSeeking) BeginSeek();
        SeekSeconds = Math.Clamp(SeekSeconds + seconds, 0, DurationSeconds);
        await CommitSeekAsync(TimeSpan.FromMilliseconds(180));
    }

    public void HandleEscape()
    {
        if (IsTrackPickerVisible) CancelTrackPicker();
        else if (IsPlaylistPickerVisible) CancelPlaylistPicker();
        else if (LibraryRemovalConfirmationVisible) CancelLibraryRemoval();
        else if (RecentClearConfirmationVisible) RecentClearConfirmationVisible = false;
        else if (DeleteConfirmationVisible) DeleteConfirmationVisible = false;
        else if (IsQueueVisible) IsQueueVisible = false;
    }

    public async Task SaveStateAsync(double width, double height)
    {
        _settings.Volume = (float)(VolumePercent / 100d);
        _settings.IsMuted = IsMuted;
        _settings.PlayMode = _queue.PlayMode;
        _settings.LastTrackId = CurrentTrack?.Id;
        _settings.LastPosition = _audio.Position;
        _settings.QueueTrackIds = _queue.Items.Select(x => x.Id).ToList();
        _settings.QueueIndex = _queue.CurrentIndex;
        _settings.IncludeSubdirectories = IncludeSubdirectories;
        _settings.SpectrumEnabled = SpectrumEnabled;
        _settings.SpectrumFps = SpectrumFps;
        _settings.ReduceMotion = ReduceMotion;
        _settings.WindowWidth = width;
        _settings.WindowHeight = height;
        _seekCoordinator.Cancel(); _playCountCts?.Cancel(); _searchCts?.Cancel(); _scanCts?.Cancel(); _snackbarCts?.Cancel();
        _greetingTimer.Stop();
        _greetingTimer.Tick -= OnGreetingTimerTick;
        if (_activeImportTask is { IsCompleted: false })
        {
            try { await _activeImportTask.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { Log.Warning("等待导入任务退出超过 5 秒，应用继续关闭"); }
            catch (OperationCanceledException) { }
        }
        await _settingsService.SaveAsync(_settings);
        if (!_disposed)
        {
            _disposed = true;
            _trackStateStore.FavoriteChanged -= OnFavoriteChanged;
            _seekCoordinator.Dispose();
            _snackbarCts?.Dispose();
        }
        await _audio.DisposeAsync();
    }

    [RelayCommand] private void Navigate(string page)
    {
        CurrentPage = page;
        if (page == "我的收藏" || page == "最近播放" || page == "我的歌单") _ = RefreshSecondaryPageAsync(page);
    }

    [RelayCommand] private async Task RefreshAsync() => await RefreshLibraryAsync();
    [RelayCommand] private async Task RetryLibraryLoadAsync() => await RefreshLibraryAsync();

    [RelayCommand] private async Task PlayTrackAsync(Track? track)
    {
        if (track is null) return;
        try
        {
            if (!_ffmpeg.IsAvailable) { StatusMessage = "缺少 FFmpeg，暂时无法播放；设置页可查看放置路径"; return; }
            track.RefreshFileAvailability();
            if (!track.FileExists)
            {
                StatusMessage = "文件不存在，未启动播放";
                ShowSnackbar("文件不存在，歌曲记录仍保留在音乐库中", SnackbarTone.Warning);
                return;
            }
            if (!_queue.Items.Any(x => x.Id == track.Id)) _queue.Replace(Tracks.Count > 0 ? Tracks : [track], track.Id);
            else _queue.SetCurrent(track.Id);
            CurrentTrack = track;
            OnPropertyChanged(nameof(CurrentTitle)); OnPropertyChanged(nameof(CurrentArtist)); OnPropertyChanged(nameof(CurrentAlbum)); OnPropertyChanged(nameof(CurrentCoverPath));
            await _audio.LoadAsync(track.FilePath);
            await LoadLyricsAsync(track);
            await _audio.PlayAsync();
            await _library.RecordPlaybackAsync(track.Id, TimeSpan.Zero);
            SchedulePlayCount(track.Id);
            StatusMessage = $"正在播放：{track.Title}";
        }
        catch (Exception ex) { Log.Error(ex, "播放歌曲失败"); StatusMessage = ex.Message; ShowSnackbar(ex.Message, SnackbarTone.Error); }
    }

    [RelayCommand] private async Task TogglePlayAsync()
    {
        if (!CanTogglePlay) return;
        if (_audio.State == PlaybackState.Playing) { await _audio.PauseAsync(); return; }
        var candidate = CurrentTrack ?? SelectedTrack ?? _queue.Current ?? Tracks.FirstOrDefault();
        if (candidate is null) return;
        candidate.RefreshFileAvailability();
        if (!candidate.FileExists)
        {
            StatusMessage = "文件不存在，无法播放";
            ShowSnackbar("文件不存在，无法播放", SnackbarTone.Warning);
            return;
        }
        if (CurrentTrack is null || _audio.State == PlaybackState.Error) await PlayTrackAsync(candidate);
        else await _audio.PlayAsync();
    }

    [RelayCommand] private async Task StopAsync()
    {
        _playCountCts?.Cancel();
        await _audio.StopAsync();
        UpdatePosition(TimeSpan.Zero);
    }
    [RelayCommand] private async Task PreviousAsync() { var track = _queue.MovePrevious(_audio.Position); if (track is not null) await PlayTrackAsync(track); }
    [RelayCommand] private async Task NextAsync() { var track = _queue.MoveNext(); if (track is not null) await PlayTrackAsync(track); }
    [RelayCommand] private void ToggleQueue() => IsQueueVisible = !IsQueueVisible;
    [RelayCommand] private void CancelScan() => _scanCts?.Cancel();
    [RelayCommand] private void DismissImport()
    {
        if (IsImporting) return;
        ImportProgress = new ImportProgress(ImportStage.Idle, 0, 0, 0, 0, 0, 0, null, string.Empty, null, false);
        ImportStatusText = string.Empty;
        ScanStatus = string.Empty;
    }

    [RelayCommand] private void CyclePlayMode()
    {
        _queue.PlayMode = _queue.PlayMode switch { PlayMode.Sequential => PlayMode.RepeatAll, PlayMode.RepeatAll => PlayMode.RepeatOne, PlayMode.RepeatOne => PlayMode.Shuffle, _ => PlayMode.Sequential };
        UpdatePlayModeText();
    }

    [RelayCommand] private async Task ToggleFavoriteAsync(Track? track)
    {
        track ??= CurrentTrack;
        if (track is null || _favoriteOperationInProgress) return;
        var previous = track.IsFavorite;
        _favoriteOperationInProgress = true;
        NotifyFavoriteCommandState();
        ApplyFavoriteState(track.Id, !previous);
        try
        {
            await _library.SetFavoriteAsync(track.Id, !previous);
            var id = track.Id;
            ShowSnackbar(
                previous ? "已取消喜欢" : "已添加到喜欢",
                SnackbarTone.Success,
                async token => await _library.SetFavoriteAsync(id, previous, token));
        }
        catch (Exception ex)
        {
            ApplyFavoriteState(track.Id, previous);
            Log.Error(ex, "更新收藏失败：{TrackId}", track.Id);
            ShowSnackbar("收藏状态保存失败，已恢复原状态", SnackbarTone.Error);
        }
        finally
        {
            _favoriteOperationInProgress = false;
            NotifyFavoriteCommandState();
        }
    }

    [RelayCommand] private async Task RemoveRecentAsync(RecentTrack? recent)
    {
        if (recent is null) return;
        try
        {
            var removal = await _library.RemoveRecentAsync(recent.HistoryId);
            if (removal is null) return;
            foreach (var item in RecentTracks.Where(x => x.Track.Id == removal.TrackId).ToArray()) RecentTracks.Remove(item);
            UpdateRecentPageState();
            ShowSnackbar("已从最近播放中移除", SnackbarTone.Success, async token =>
            {
                await _library.RestoreRecentAsync(removal, token);
                await Dispatcher.UIThread.InvokeAsync(RefreshRecentAsync);
            }, recent.HistoryId);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "移除最近播放失败");
            ShowSnackbar("移除最近播放失败", SnackbarTone.Error);
        }
    }

    [RelayCommand] private void AskClearRecent() => RecentClearConfirmationVisible = true;
    [RelayCommand] private void CancelClearRecent() => RecentClearConfirmationVisible = false;
    [RelayCommand] private async Task ConfirmClearRecentAsync()
    {
        RecentClearConfirmationVisible = false;
        try
        {
            await _library.ClearRecentAsync();
            RecentTracks.Clear();
            UpdateRecentPageState();
            ShowSnackbar("最近播放记录已清空", SnackbarTone.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "清空最近播放失败");
            ShowSnackbar("清空最近播放失败", SnackbarTone.Error);
        }
    }

    [RelayCommand] private async Task UndoSnackbarAsync()
    {
        try
        {
            if (!await _undoCoordinator.ExecuteCurrentAsync()) return;
            ShowSnackbar("已撤销", SnackbarTone.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "撤销操作失败");
            ShowSnackbar("撤销失败，已保留数据库真实状态", SnackbarTone.Error);
        }
    }

    [RelayCommand] private void AddToQueue(Track? track) { if (track is not null) _queue.Add(track); }
    [RelayCommand] private void PlayNext(Track? track) { if (track is not null) _queue.PlayNext(track); }
    [RelayCommand] private async Task PlayQueueItemAsync(QueueItemViewModel? item)
    {
        if (item is null) return;
        item.Track.RefreshFileAvailability();
        if (!item.Track.FileExists)
        {
            ShowSnackbar("歌曲文件不存在", SnackbarTone.Warning);
            return;
        }
        _queue.SetCurrent(item.Track.Id);
        await PlayTrackAsync(item.Track);
    }
    [RelayCommand] private void RemoveQueueItem(QueueItemViewModel? item) { if (item is not null) _queue.Remove(item.Track.Id); }
    [RelayCommand] private void AskRemoveFromLibrary(Track? track)
    {
        if (track is null) return;
        PrepareLibraryRemoval([track]);
    }
    [RelayCommand] private void AskRemoveSelectedFromLibrary()
    {
        if (SelectedLibraryTracks.Count > 0) PrepareLibraryRemoval(SelectedLibraryTracks);
    }
    [RelayCommand] private void CancelLibraryRemoval()
    {
        LibraryRemovalConfirmationVisible = false;
        _pendingLibraryRemovalIds = [];
    }
    [RelayCommand] private async Task ConfirmLibraryRemovalAsync()
    {
        if (_libraryRemovalInProgress || _pendingLibraryRemovalIds.Length == 0) return;
        _libraryRemovalInProgress = true;
        OnPropertyChanged(nameof(IsLibraryRemovalInProgress));
        var ids = _pendingLibraryRemovalIds;
        try
        {
            var removedCurrentTrack = CurrentTrack is not null && ids.Contains(CurrentTrack.Id);
            if (removedCurrentTrack)
            {
                _playCountCts?.Cancel();
                await _audio.UnloadAsync();
                CurrentTrack = null;
                UpdatePosition(TimeSpan.Zero);
            }
            foreach (var id in ids) _queue.Remove(id);
            if (removedCurrentTrack) _queue.ClearCurrent();
            var result = await _library.RemoveFromLibraryAsync(ids);
            SelectedTrack = null;
            SelectedLibraryTracks.Clear();
            await RefreshAllAsync();
            RefreshQueue();
            ShowSnackbar($"已从音乐库移除 {result.RemovedCount} 首歌曲", SnackbarTone.Success);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "从音乐库移除歌曲失败");
            ShowSnackbar("部分歌曲移除失败", SnackbarTone.Error);
        }
        finally
        {
            _libraryRemovalInProgress = false;
            OnPropertyChanged(nameof(IsLibraryRemovalInProgress));
            CancelLibraryRemoval();
            NotifyLibrarySelectionChanged();
        }
    }
    [RelayCommand] private void OpenContainingFolder(Track? track) { var folder = track is null ? null : Path.GetDirectoryName(track.FilePath); if (folder is not null) OpenFolder(folder); }
    [RelayCommand] private void ShowTrackInfo(Track? track) { if (track is not null) StatusMessage = $"{track.Title} · {track.Artist} · {track.CodecName} · {track.SampleRate} Hz · {track.Channels} 声道"; }

    [RelayCommand] private async Task CreatePlaylistAsync() { await _playlists.CreateAsync(PlaylistName); PlaylistName = "新建歌单"; await RefreshPlaylistsAsync(); }
    [RelayCommand] private async Task RenamePlaylistAsync() { if (SelectedPlaylist is null) return; await _playlists.RenameAsync(SelectedPlaylist.Id, PlaylistName); await RefreshPlaylistsAsync(); }
    [RelayCommand] private void AskDeletePlaylist() { if (SelectedPlaylist is not null) DeleteConfirmationVisible = true; }
    [RelayCommand] private void CancelDeletePlaylist() => DeleteConfirmationVisible = false;
    [RelayCommand] private async Task ConfirmDeletePlaylistAsync() { if (SelectedPlaylist is null) return; await _playlists.DeleteAsync(SelectedPlaylist.Id); DeleteConfirmationVisible = false; SelectedPlaylist = null; PlaylistTracks.Clear(); await RefreshPlaylistsAsync(); }
    [RelayCommand] private async Task OpenTrackPickerAsync()
    {
        if (SelectedPlaylist is null)
        {
            ShowSnackbar("请先选择一个歌单", SnackbarTone.Warning);
            return;
        }
        var existing = await _playlists.GetTrackIdsAsync(SelectedPlaylist.Id);
        TrackPickerItems.Clear();
        foreach (var track in _allTracks)
        {
            var item = new TrackPickerItemViewModel(track, existing.Contains(track.Id));
            item.PropertyChanged += (_, _) => NotifyTrackPickerSelectionChanged();
            TrackPickerItems.Add(item);
        }
        TrackPickerTargetName = SelectedPlaylist.Name;
        PlaylistTrackSearchText = string.Empty;
        ApplyTrackPickerFilter();
        IsTrackPickerVisible = true;
        NotifyTrackPickerSelectionChanged();
    }
    [RelayCommand] private void CancelTrackPicker()
    {
        IsTrackPickerVisible = false;
        TrackPickerItems.Clear();
        VisibleTrackPickerItems.Clear();
        PlaylistTrackSearchText = string.Empty;
        NotifyTrackPickerSelectionChanged();
    }
    [RelayCommand] private async Task ConfirmTrackPickerAsync()
    {
        if (SelectedPlaylist is null || !CanConfirmTrackPicker) return;
        _playlistOperationInProgress = true;
        NotifyTrackPickerSelectionChanged();
        try
        {
            var ids = TrackPickerItems.Where(x => x.IsSelected && x.CanSelect).Select(x => x.Track.Id).ToArray();
            var result = await _playlists.AddTracksAsync(SelectedPlaylist.Id, ids);
            await LoadPlaylistTracksAsync();
            var message = result.AddedCount == 0
                ? "所选歌曲已在歌单中"
                : result.SkippedCount == 0
                    ? $"已添加 {result.AddedCount} 首歌曲到歌单"
                    : $"已添加 {result.AddedCount} 首歌曲，跳过 {result.SkippedCount} 首";
            ShowSnackbar(message, SnackbarTone.Success);
            CancelTrackPicker();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "添加歌曲到歌单失败：{PlaylistId}", SelectedPlaylist.Id);
            ShowSnackbar(ex.Message, SnackbarTone.Error);
        }
        finally
        {
            _playlistOperationInProgress = false;
            NotifyTrackPickerSelectionChanged();
        }
    }
    [RelayCommand] private async Task OpenPlaylistPickerAsync(Track? track)
    {
        if (track is null) return;
        await OpenPlaylistPickerCoreAsync([track.Id]);
    }
    [RelayCommand] private async Task OpenPlaylistPickerForSelectionAsync()
    {
        if (SelectedLibraryTracks.Count == 0) return;
        await OpenPlaylistPickerCoreAsync(SelectedLibraryTracks.Select(x => x.Id).ToArray());
    }
    [RelayCommand] private void CancelPlaylistPicker()
    {
        IsPlaylistPickerVisible = false;
        PlaylistChoices.Clear();
        _playlistPickerTrackIds = [];
        NotifyPlaylistChoiceSelectionChanged();
    }
    [RelayCommand] private async Task CreatePlaylistFromPickerAsync()
    {
        var playlist = await _playlists.CreateAsync(PlaylistPickerNewName);
        PlaylistPickerNewName = "新建歌单";
        await RefreshPlaylistsAsync();
        await OpenPlaylistPickerCoreAsync(_playlistPickerTrackIds, playlist.Id);
    }
    [RelayCommand] private async Task ConfirmPlaylistPickerAsync()
    {
        if (!CanConfirmPlaylistPicker) return;
        _playlistOperationInProgress = true;
        NotifyPlaylistChoiceSelectionChanged();
        try
        {
            var added = 0;
            var skipped = 0;
            foreach (var choice in PlaylistChoices.Where(x => x.IsSelected))
            {
                var result = await _playlists.AddTracksAsync(choice.Playlist.Id, _playlistPickerTrackIds);
                added += result.AddedCount;
                skipped += result.SkippedCount;
            }
            if (SelectedPlaylist is not null && PlaylistChoices.Any(x => x.IsSelected && x.Playlist.Id == SelectedPlaylist.Id))
                await LoadPlaylistTracksAsync();
            ShowSnackbar(added == 0 ? "所选歌曲已在歌单中" : $"已新增 {added} 条歌单歌曲关系，跳过 {skipped} 条", SnackbarTone.Success);
            CancelPlaylistPicker();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "批量添加到歌单失败");
            ShowSnackbar(ex.Message, SnackbarTone.Error);
        }
        finally
        {
            _playlistOperationInProgress = false;
            NotifyPlaylistChoiceSelectionChanged();
        }
    }
    [RelayCommand] private async Task RemoveSelectedFromPlaylistAsync(Track? track) { if (SelectedPlaylist is null || track is null) return; await _playlists.RemoveTrackAsync(SelectedPlaylist.Id, track.Id); await LoadPlaylistTracksAsync(); }

    [RelayCommand] private void SelectAllFilteredTracks() => UpdateLibrarySelection(Tracks);
    [RelayCommand] private void ClearLibrarySelection() => UpdateLibrarySelection([]);

    [RelayCommand] private void OpenDataFolder() => OpenFolder(_paths.Root);
    [RelayCommand] private void OpenLogsFolder() => OpenFolder(_paths.LogsDirectory);

    partial void OnCurrentPageChanged(string value)
    {
        _spectrum.IsEnabled = SpectrumEnabled && value == "正在播放";
        foreach (var name in new[] { nameof(IsHomePage), nameof(IsLibraryPage), nameof(IsNowPlayingPage), nameof(IsRecentPage), nameof(IsFavoritesPage), nameof(IsPlaylistsPage), nameof(IsSettingsPage) }) OnPropertyChanged(name);
    }
    partial void OnCurrentTrackChanged(Track? value)
    {
        value?.RefreshFileAvailability();
        OnPropertyChanged(nameof(CurrentTitle)); OnPropertyChanged(nameof(CurrentArtist)); OnPropertyChanged(nameof(CurrentAlbum)); OnPropertyChanged(nameof(CurrentCoverPath));
        NotifyPlaybackActionChanged();
        NotifyFavoriteCommandState();
    }
    partial void OnSelectedPlaylistChanged(Playlist? value) { if (value is not null) { PlaylistName = value.Name; _ = LoadPlaylistTracksAsync(); } }
    partial void OnSearchTextChanged(string value) => DebounceSearch();
    partial void OnPlaylistTrackSearchTextChanged(string value) => ApplyTrackPickerFilter();
    partial void OnSortFieldChanged(string value) => ApplyFilter();
    partial void OnVolumePercentChanged(double value) { _audio.Volume = (float)Math.Clamp(value / 100d, 0, 1); }
    partial void OnIsMutedChanged(bool value) { _audio.IsMuted = value; }
    partial void OnSpectrumEnabledChanged(bool value) { _spectrum.IsEnabled = value && IsNowPlayingPage; }
    partial void OnSpectrumFpsChanged(int value) { _spectrum.FramesPerSecond = Math.Clamp(value, 10, 60); }
    partial void OnPlayModeTextChanged(string value)
    {
        _queue.PlayMode = value switch { "顺序播放" => PlayMode.Sequential, "单曲循环" => PlayMode.RepeatOne, "随机播放" => PlayMode.Shuffle, _ => PlayMode.RepeatAll };
    }

    partial void OnLayoutModeChanged(ResponsiveLayoutMode value)
    {
        foreach (var name in new[]
        {
            nameof(IsWideLayout), nameof(IsStandardLayout), nameof(IsCompactLayout), nameof(NavigationWidth),
            nameof(ShowNavigationText), nameof(PageMargin), nameof(ShellContentMargin), nameof(PageContentMargin), nameof(HeaderSearchWidth), nameof(ShowDecorativeArtwork),
            nameof(ShowAlbumColumn), nameof(ShowExtendedTrackActions), nameof(ShowVolumeControls),
            nameof(AlbumColumnWidth), nameof(ArtistColumnWidth), nameof(TrackActionColumnWidth), nameof(QueueDrawerWidth),
            nameof(IconSize), nameof(IconButtonSize), nameof(PrimaryPlayButtonSize)
        }) OnPropertyChanged(name);
    }

    partial void OnPlaybackStateChanged(PlaybackState value) => NotifyPlaybackActionChanged();

    partial void OnSeekSecondsChanged(double value)
    {
        if (IsUserSeeking || IsSeekPending) CurrentTimeText = TimeFormatter.Format(TimeSpan.FromSeconds(Math.Max(0, value)));
    }

    partial void OnFavoritesPageStateChanged(PageLoadState value)
    {
        foreach (var name in new[] { nameof(IsFavoritesLoading), nameof(IsFavoritesEmpty), nameof(HasFavoritesContent), nameof(IsFavoritesError) }) OnPropertyChanged(name);
    }

    partial void OnRecentPageStateChanged(PageLoadState value)
    {
        foreach (var name in new[] { nameof(IsRecentLoading), nameof(IsRecentEmpty), nameof(HasRecentContent), nameof(IsRecentError) }) OnPropertyChanged(name);
    }

    partial void OnImportProgressChanged(ImportProgress value)
    {
        ScanStatus = value.StatusText;
        foreach (var name in new[]
        {
            nameof(IsImportPanelVisible), nameof(IsImporting), nameof(CanStartImport), nameof(CanCancelImport),
            nameof(CanDismissImport), nameof(ImportIsIndeterminate), nameof(ImportPercentage),
            nameof(ImportCurrentFileName), nameof(ImportCountsText), nameof(HasImportFailures)
        }) OnPropertyChanged(name);
    }

    partial void OnIsLibraryLoadingChanged(bool value) => NotifyLibraryStateChanged();
    partial void OnHasLibraryErrorChanged(bool value) => NotifyLibraryStateChanged();

    private async Task RefreshAllAsync()
    {
        await RefreshLibraryAsync();
        try
        {
            await RefreshFavoritesAsync();
            await RefreshRecentAsync();
            await RefreshPlaylistsAsync();
            OnPropertyChanged(nameof(FavoriteCount));
            OnPropertyChanged(nameof(PlaylistCount));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新收藏、最近播放或歌单失败");
        }
    }

    private async Task<bool> RefreshLibraryAsync()
    {
        IsLibraryLoading = true;
        HasLibraryError = false;
        LibraryErrorMessage = string.Empty;
        try
        {
            var tracks = await _library.GetTracksAsync();
            _allTracks = tracks.Where(x => !string.IsNullOrWhiteSpace(x.FilePath)).ToArray();
            var selectedIds = SelectedLibraryTracks.Select(x => x.Id).ToHashSet();
            Replace(SelectedLibraryTracks, _allTracks.Where(x => selectedIds.Contains(x.Id)));
            ApplyFilter();
            OnPropertyChanged(nameof(TrackCount));
            NotifyLibrarySelectionChanged();
            Log.Information("本地音乐刷新完成：数据库 {DatabaseCount} 首，当前筛选 {FilteredCount} 首", _allTracks.Count, Tracks.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "本地音乐刷新失败");
            HasLibraryError = true;
            LibraryErrorMessage = "本地音乐加载失败。";
            StatusMessage = "本地音乐加载失败，请查看日志";
            return false;
        }
        finally
        {
            IsLibraryLoading = false;
            NotifyLibraryStateChanged();
        }
    }

    private async Task RefreshSecondaryPageAsync(string page)
    {
        try
        {
            if (page == "我的收藏") await RefreshFavoritesAsync();
            else if (page == "最近播放") await RefreshRecentAsync();
            else if (page == "我的歌单")
            {
                await RefreshPlaylistsAsync();
                OnPropertyChanged(nameof(PlaylistCount));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "切换到页面 {Page} 时刷新数据失败", page);
            StatusMessage = $"{page}加载失败，请查看日志";
            if (page == "我的收藏") { FavoritesErrorMessage = StatusMessage; FavoritesPageState = PageLoadState.Error; }
            if (page == "最近播放") { RecentErrorMessage = StatusMessage; RecentPageState = PageLoadState.Error; }
        }
    }

    private async Task RefreshFavoritesAsync()
    {
        FavoritesPageState = PageLoadState.Loading;
        FavoritesErrorMessage = string.Empty;
        try
        {
            Replace(Favorites, await _library.GetFavoritesAsync());
            FavoritesPageState = Favorites.Count == 0 ? PageLoadState.Empty : PageLoadState.Content;
            OnPropertyChanged(nameof(FavoriteCount));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新收藏失败");
            FavoritesErrorMessage = "喜欢的歌曲加载失败，请重试。";
            FavoritesPageState = PageLoadState.Error;
            throw;
        }
    }

    private async Task RefreshRecentAsync()
    {
        RecentPageState = PageLoadState.Loading;
        RecentErrorMessage = string.Empty;
        try
        {
            Replace(RecentTracks, await _library.GetRecentAsync());
            UpdateRecentPageState();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "刷新最近播放失败");
            RecentErrorMessage = "最近播放加载失败，请重试。";
            RecentPageState = PageLoadState.Error;
            throw;
        }
    }

    private async Task RefreshPlaylistsAsync() => Replace(Playlists, await _playlists.GetPlaylistsAsync());
    private async Task LoadPlaylistTracksAsync() { if (SelectedPlaylist is null) return; Replace(PlaylistTracks, await _playlists.GetTracksAsync(SelectedPlaylist.Id)); }

    private void ApplyFilter()
    {
        IEnumerable<Track> query = _allTracks.Where(x => TrackSearch.Matches(x, SearchText));
        query = SortField switch
        {
            "歌手" => query.OrderBy(x => x.Artist), "专辑" => query.OrderBy(x => x.Album),
            "添加时间" => query.OrderByDescending(x => x.AddedAt), "时长" => query.OrderBy(x => x.Duration),
            _ => query.OrderBy(x => x.Title)
        };
        Replace(Tracks, query);
        NotifyLibraryStateChanged();
    }

    private void DebounceSearch()
    {
        _searchCts?.Cancel(); _searchCts?.Dispose(); _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        _ = Task.Run(async () => { try { await Task.Delay(250, token); await Dispatcher.UIThread.InvokeAsync(ApplyFilter); } catch (OperationCanceledException) { } }, token);
    }

    private async Task RunImportAsync(Func<IProgress<ImportProgress>, CancellationToken, Task<ImportResult>> action)
    {
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        ImportProgress = new ImportProgress(ImportStage.Enumerating, 0, 0, 0, 0, 0, 0, null, "正在查找音乐文件……", null, true);
        ImportStatusText = ImportProgress.StatusText;
        var progress = new Progress<ImportProgress>(value =>
        {
            ImportProgress = value;
            ImportStatusText = value.StatusText;
        });
        try
        {
            var result = await action(progress, _scanCts.Token);
            if (result.IsCancelled)
            {
                ImportProgress = ToProgress(result, "导入已取消");
                ImportStatusText = ImportSummary.Create(result, true, false);
                StatusMessage = ImportStatusText;
                return;
            }

            ImportProgress = new ImportProgress(
                ImportStage.RefreshingLibrary,
                result.DiscoveredCount,
                result.ProcessedCount,
                result.ImportedCount,
                result.UpdatedCount,
                result.SkippedCount,
                result.FailedCount,
                null,
                "正在刷新本地音乐……",
                100,
                false);

            var refreshed = await RefreshLibraryAsync();
            var affected = result.AffectedTrackIds.ToHashSet();
            var confirmedInDatabaseView = refreshed && affected.All(id => _allTracks.Any(x => x.Id == id));
            var searchActive = !string.IsNullOrWhiteSpace(SearchText);
            var hiddenBySearch = confirmedInDatabaseView && searchActive && affected.Any(id => Tracks.All(x => x.Id != id));
            var visibleConfirmed = confirmedInDatabaseView && (searchActive || affected.All(id => Tracks.Any(x => x.Id == id)));
            var summary = ImportSummary.Create(result, visibleConfirmed, hiddenBySearch);
            var finalStage = visibleConfirmed ? result.Stage : ImportStage.Failed;
            ImportProgress = ToProgress(result with { Stage = finalStage }, summary);
            ImportStatusText = summary;
            StatusMessage = summary;
            if (!visibleConfirmed)
                Log.Error("导入后 UI 刷新未确认：Affected={AffectedCount}, DatabaseTracks={DatabaseCount}, VisibleTracks={VisibleCount}", affected.Count, _allTracks.Count, Tracks.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入 UI 流程失败");
            ImportProgress = ImportProgress with { Stage = ImportStage.Failed, StatusText = "导入过程中遇到问题，请查看失败详情或日志。", IsIndeterminate = false };
            ImportStatusText = "导入过程中遇到问题，请查看失败详情或日志。";
            StatusMessage = ImportStatusText;
        }
        finally
        {
            _scanCts.Dispose();
            _scanCts = null;
            IsScanning = false;
            _activeImportTask = null;
            OnPropertyChanged(nameof(CanStartImport));
        }
    }

    private static ImportProgress ToProgress(ImportResult result, string statusText) => new(
        result.Stage,
        result.DiscoveredCount,
        result.ProcessedCount,
        result.ImportedCount,
        result.UpdatedCount,
        result.SkippedCount,
        result.FailedCount,
        null,
        statusText,
        result.DiscoveredCount > 0 ? Math.Clamp(result.ProcessedCount * 100d / result.DiscoveredCount, 0, 100) : 0,
        false);

    private void NotifyLibraryStateChanged()
    {
        foreach (var name in new[]
        {
            nameof(FilteredTrackCount), nameof(HasLibraryContent), nameof(IsLibraryEmpty), nameof(IsSearchEmptyResult)
        }) OnPropertyChanged(name);
    }

    private async Task CommitSeekAsync(TimeSpan delay)
    {
        if (!CanSeek)
        {
            IsUserSeeking = false;
            SeekSeconds = PositionSeconds;
            return;
        }

        var version = Interlocked.Increment(ref _seekRequestVersion);
        var target = SeekTarget.Clamp(TimeSpan.FromSeconds(SeekSeconds), _audio.Duration);
        var actualBefore = _audio.Position;
        IsSeekPending = true;
        try
        {
            await _seekCoordinator.SubmitAsync(target, (value, token) => _audio.SeekAsync(value, token), delay);
            if (version != _seekRequestVersion) return;
            PositionSeconds = target.TotalSeconds;
            SeekSeconds = target.TotalSeconds;
            CurrentTimeText = TimeFormatter.Format(target);
        }
        catch (OperationCanceledException) when (version != _seekRequestVersion) { }
        catch (Exception ex)
        {
            Log.Error(ex, "Seek 提交失败：{Target}", target);
            var actual = _audio.Position == TimeSpan.Zero ? actualBefore : _audio.Position;
            PositionSeconds = actual.TotalSeconds;
            SeekSeconds = actual.TotalSeconds;
            CurrentTimeText = TimeFormatter.Format(actual);
            ShowSnackbar("定位失败，进度已恢复到真实位置", SnackbarTone.Error);
        }
        finally
        {
            if (version == _seekRequestVersion)
            {
                IsSeekPending = false;
                IsUserSeeking = false;
                NotifyPlaybackActionChanged();
            }
        }
    }

    private void OnFavoriteChanged(object? sender, FavoriteStateChanged change)
    {
        Dispatcher.UIThread.Post(() => ApplyFavoriteState(change.TrackId, change.IsFavorite));
    }

    private void ApplyFavoriteState(Guid trackId, bool favorite)
    {
        foreach (var track in EnumerateTrackInstances(trackId)) track.IsFavorite = favorite;

        var existing = Favorites.FirstOrDefault(x => x.Id == trackId);
        if (!favorite && existing is not null) Favorites.Remove(existing);
        else if (favorite && existing is null)
        {
            var source = _allTracks.FirstOrDefault(x => x.Id == trackId)
                ?? RecentTracks.Select(x => x.Track).FirstOrDefault(x => x.Id == trackId)
                ?? PlaylistTracks.FirstOrDefault(x => x.Id == trackId)
                ?? QueueTracks.Select(x => x.Track).FirstOrDefault(x => x.Id == trackId)
                ?? (CurrentTrack?.Id == trackId ? CurrentTrack : null);
            if (source is not null) Favorites.Add(source);
        }

        FavoritesPageState = Favorites.Count == 0 ? PageLoadState.Empty : PageLoadState.Content;
        OnPropertyChanged(nameof(FavoriteCount));
        NotifyFavoriteCommandState();
    }

    private IEnumerable<Track> EnumerateTrackInstances(Guid trackId)
    {
        foreach (var track in _allTracks.Where(x => x.Id == trackId)) yield return track;
        foreach (var track in Tracks.Where(x => x.Id == trackId)) yield return track;
        foreach (var track in Favorites.Where(x => x.Id == trackId)) yield return track;
        foreach (var track in RecentTracks.Select(x => x.Track).Where(x => x.Id == trackId)) yield return track;
        foreach (var track in PlaylistTracks.Where(x => x.Id == trackId)) yield return track;
        foreach (var track in QueueTracks.Select(x => x.Track).Where(x => x.Id == trackId)) yield return track;
        if (CurrentTrack?.Id == trackId) yield return CurrentTrack;
    }

    private void ApplyPlaybackState(PlaybackState state)
    {
        PlaybackState = state;
        IsPlaying = state == PlaybackState.Playing;
        PlaybackStateText = StateText(state);
        NotifyPlaybackActionChanged();
    }

    private void NotifyPlaybackActionChanged()
    {
        foreach (var name in new[]
        {
            nameof(CanSeek), nameof(PrimaryPlaybackAction), nameof(ShowPauseIcon), nameof(ShowPlayIcon),
            nameof(ShowPlaybackBusyIcon), nameof(CanTogglePlay), nameof(PrimaryPlayTooltip), nameof(PrimaryPlayAutomationName)
        }) OnPropertyChanged(name);
    }

    private void NotifyFavoriteCommandState() => OnPropertyChanged(nameof(CanToggleCurrentFavorite));

    private void UpdateRecentPageState() => RecentPageState = RecentTracks.Count == 0 ? PageLoadState.Empty : PageLoadState.Content;

    private void ShowSnackbar(
        string message,
        SnackbarTone tone,
        Func<CancellationToken, Task>? undo = null,
        Guid? subjectId = null)
    {
        _snackbarCts?.Cancel();
        _snackbarCts?.Dispose();
        _snackbarCts = new CancellationTokenSource();
        var cancellationToken = _snackbarCts.Token;
        Guid? undoToken = null;
        if (undo is null) _undoCoordinator.Reset();
        else undoToken = _undoCoordinator.Set(subjectId ?? Guid.NewGuid(), undo);

        SnackbarMessage = message;
        SnackbarTone = tone;
        SnackbarCanUndo = undo is not null;
        IsSnackbarVisible = true;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
                if (undoToken is { } token) _undoCoordinator.Clear(token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsSnackbarVisible = false;
                    SnackbarCanUndo = false;
                });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }, cancellationToken);
    }

    private async Task LoadLyricsAsync(Track track)
    {
        var rows = (await _lyrics.LoadForTrackAsync(track)).Select(x => new LyricsRowViewModel(x)).ToArray();
        Replace(LyricsRows, rows.Length == 0 ? [new LyricsRowViewModel(new LyricsLine(TimeSpan.Zero, "暂未找到本地歌词"))] : rows);
    }

    private void UpdatePosition(TimeSpan position)
    {
        PositionSeconds = position.TotalSeconds;
        DurationSeconds = Math.Max(0, _audio.Duration.TotalSeconds);
        if (!IsUserSeeking && !IsSeekPending)
        {
            SeekSeconds = PositionSeconds;
            CurrentTimeText = TimeFormatter.Format(position);
        }
        DurationText = TimeFormatter.Format(_audio.Duration);
        NotifyPlaybackActionChanged();
        var current = LyricsRows.LastOrDefault(x => x.Timestamp <= position);
        foreach (var row in LyricsRows) row.IsCurrent = ReferenceEquals(row, current);
    }

    private void UpdateSpectrum(IReadOnlyList<float> bands)
    {
        if (!SpectrumEnabled) return;
        for (var i = 0; i < Math.Min(SpectrumBars.Count, bands.Count); i++) SpectrumBars[i] = 3 + bands[i] * 92;
    }

    private async Task HandleCompletedAsync()
    {
        var track = _queue.MoveNext(true);
        if (track is null) { await _audio.StopAsync(); return; }
        await Dispatcher.UIThread.InvokeAsync(() => PlayTrackCommand.Execute(track));
    }

    private void RefreshQueue() => Replace(QueueTracks, QueueIndexing.Create(_queue.Items, _queue.Current?.Id)
        .Select(x => new QueueItemViewModel(x.DisplayIndex, x.Track, x.IsCurrent)));

    public void UpdateLibrarySelection(IEnumerable<Track> tracks)
    {
        var selected = tracks.DistinctBy(x => x.Id).ToArray();
        Replace(SelectedLibraryTracks, selected);
        SelectedTrack = selected.Length == 1
            ? selected[0]
            : SelectedTrack is not null && selected.Any(x => x.Id == SelectedTrack.Id) ? SelectedTrack : null;
        NotifyLibrarySelectionChanged();
    }

    private void PrepareLibraryRemoval(IEnumerable<Track> tracks)
    {
        var selected = tracks.DistinctBy(x => x.Id).ToArray();
        if (selected.Length == 0) return;
        _pendingLibraryRemovalIds = selected.Select(x => x.Id).ToArray();
        LibraryRemovalConfirmationText = selected.Length == 1
            ? $"确定从音乐库中移除《{selected[0].Title}》吗？\n歌曲将从播放器音乐库中移除，但不会删除磁盘上的音频文件。"
            : $"确定从音乐库中移除选中的 {selected.Length} 首歌曲吗？\n歌曲将从播放器音乐库中移除，但不会删除磁盘上的音频文件。";
        LibraryRemovalConfirmationVisible = true;
    }

    private async Task OpenPlaylistPickerCoreAsync(IReadOnlyCollection<Guid> trackIds, Guid? selectPlaylistId = null)
    {
        _playlistPickerTrackIds = trackIds.Distinct().ToArray();
        await RefreshPlaylistsAsync();
        PlaylistChoices.Clear();
        foreach (var playlist in Playlists)
        {
            var existing = await _playlists.GetTrackIdsAsync(playlist.Id);
            var choice = new PlaylistChoiceViewModel(playlist, _playlistPickerTrackIds.All(existing.Contains))
            {
                IsSelected = playlist.Id == selectPlaylistId
            };
            choice.PropertyChanged += (_, _) => NotifyPlaylistChoiceSelectionChanged();
            PlaylistChoices.Add(choice);
        }
        IsPlaylistPickerVisible = true;
        NotifyPlaylistChoiceSelectionChanged();
    }

    private void ApplyTrackPickerFilter()
    {
        Replace(VisibleTrackPickerItems, TrackPickerItems.Where(x => TrackSearch.Matches(x.Track, PlaylistTrackSearchText)));
    }

    private void NotifyLibrarySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedLibraryTrackCount));
        OnPropertyChanged(nameof(HasLibrarySelection));
    }

    private void NotifyTrackPickerSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedTrackPickerCount));
        OnPropertyChanged(nameof(CanConfirmTrackPicker));
    }

    private void NotifyPlaylistChoiceSelectionChanged()
    {
        OnPropertyChanged(nameof(HasPlaylistChoices));
        OnPropertyChanged(nameof(SelectedPlaylistChoiceCount));
        OnPropertyChanged(nameof(CanConfirmPlaylistPicker));
    }

    private void OnGreetingTimerTick(object? sender, EventArgs e) => UpdateGreeting();

    private void UpdateGreeting()
    {
        var now = _clock.LocalNow;
        GreetingText = GreetingFormatter.GetGreeting(now);
        LocalTimeText = now.ToString("HH:mm");
    }
    private void SchedulePlayCount(Guid trackId)
    {
        _playCountCts?.Cancel(); _playCountCts?.Dispose(); _playCountCts = new CancellationTokenSource();
        var token = _playCountCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                if (CurrentTrack?.Id == trackId && _audio.Position >= TimeSpan.FromSeconds(10)) await _library.RecordPlaybackAsync(trackId, _audio.Position, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        }, token);
    }
    private void UpdatePlayModeText() => PlayModeText = _queue.PlayMode switch { PlayMode.Sequential => "顺序播放", PlayMode.RepeatAll => "列表循环", PlayMode.RepeatOne => "单曲循环", _ => "随机播放" };
    private static string StateText(PlaybackState state) => state switch
    {
        PlaybackState.Playing => "正在播放",
        PlaybackState.Paused => "已暂停",
        PlaybackState.Loading => "正在加载",
        PlaybackState.Seeking => "正在定位",
        PlaybackState.Buffering => "正在缓冲",
        PlaybackState.Error => "播放失败，可重试",
        PlaybackState.Stopped => "已停止",
        _ => "未播放"
    };
    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values) { target.Clear(); foreach (var value in values) target.Add(value); }
    private static void OpenFolder(string path)
    {
        try { PlatformLauncher.OpenDirectory(path); }
        catch (Exception ex) { Log.Warning(ex, "无法打开目录：{Path}", path); }
    }
}

public partial class LyricsRowViewModel(LyricsLine line) : ObservableObject
{
    public TimeSpan Timestamp => line.Timestamp;
    public string Text => line.Text;
    [ObservableProperty] private bool isCurrent;
    public IBrush Foreground => IsCurrent ? Brush.Parse("#5EE8DE") : Brush.Parse("#A8C1C4");
    partial void OnIsCurrentChanged(bool value) => OnPropertyChanged(nameof(Foreground));
}
