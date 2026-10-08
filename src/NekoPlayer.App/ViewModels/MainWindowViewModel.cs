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
using NekoPlayer.App.Services;

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
    private readonly PlaybackCoordinator _playback;
    private readonly ITrackCatalog _catalog;
    private readonly IGatewayRuntime _gateway;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly object _backgroundGate = new();
    private readonly List<Task> _backgroundTasks = [];
    private readonly HashSet<long> _recordedSessions = [];
    private Task? _initializationTask;
    private Task? _shutdownTask;
    private bool _stopping;
    private bool _settingsLoaded;
    private bool _preferencesApplied;
    private bool _queueRestored;
    private bool _hasPlayed;
    private bool _syncingSearch;
    private readonly LatestSpectrumDispatcher _spectrumDispatcher;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _scanCts;
    private CancellationTokenSource? _playCountCts;
    private CancellationTokenSource? _snackbarCts;
    private readonly SeekRequestCoordinator _seekCoordinator = new();
    private readonly UndoActionCoordinator _undoCoordinator = new();
    private AppSettings _settings = new();
    private IReadOnlyList<Track> _allTracks = [];
    private bool _initialized;
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
        ITrackStateStore trackStateStore, IClock clock, PlaybackCoordinator playback,
        OnlineSearchViewModel onlineSearch, ITrackCatalog catalog, IGatewayRuntime gateway, ProviderAccountsViewModel accounts,
        LyricsPresentationService lyricsPresentation, IDesktopLyricsService desktopLyrics, SleepTimerService sleepTimer,
        WindowsMediaControlsService mediaControls)
    {
        _library = library; _playlists = playlists; _audio = audio; _queue = queue; _lyrics = lyrics;
        _settingsService = settingsService; _paths = paths; _ffmpeg = ffmpeg;
        _spectrum = spectrum;
        _trackStateStore = trackStateStore;
        _clock = clock;
        _playback = playback;
        OnlineSearch = onlineSearch;
        _catalog = catalog;
        _gateway = gateway;
        Accounts = accounts;
        Accounts.AccountChanged += OnProviderAccountChanged;
        InitializeFeatures(lyricsPresentation, desktopLyrics, sleepTimer, mediaControls);
        _spectrumDispatcher = new LatestSpectrumDispatcher(UpdateSpectrum);
        _greetingTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _greetingTimer.Tick += OnGreetingTimerTick;
        _trackStateStore.FavoriteChanged += OnFavoriteChanged;
        _playback.SnapshotChanged += OnPlaybackSnapshotChanged;
        _playback.PlaybackStarted += OnPlaybackStarted;
        _queue.QueueChanged += OnQueueChanged;
        _queue.CurrentChanged += OnQueueCurrentChanged;
        spectrum.SpectrumUpdated += OnSpectrumUpdated;
        OnlineSearch.PlayRequested += OnOnlinePlayRequested;
        OnlineSearch.FavoriteRequested += OnOnlineFavoriteRequested;
        OnlineSearch.QueueRequested += OnOnlineQueueRequested;
        OnlineSearch.PlaylistRequested += OnOnlinePlaylistRequested;
        OnlineSearch.HistoryChanged += OnSearchHistoryChanged;
        OnlineSearch.PropertyChanged += OnOnlineSearchPropertyChanged;
        OnlineSearch.LoginRequested += OnSearchLoginRequested;
        for (var i = 0; i < 40; i++) SpectrumBars.Add(new SpectrumBarViewModel());
    }

    public ObservableCollection<Track> Tracks { get; } = [];
    public OnlineSearchViewModel OnlineSearch { get; }
    public ProviderAccountsViewModel Accounts { get; }
    public event EventHandler? AccountsRequested;
    public GridLength NavigationIconWidth => IsCompactLayout ? GridLength.Star : new GridLength(30);
    public GridLength NavigationLabelWidth => IsCompactLayout ? new GridLength(0) : GridLength.Star;
    public ObservableCollection<Track> Favorites { get; } = [];
    public ObservableCollection<RecentTrack> RecentTracks { get; } = [];
    public ObservableCollection<Playlist> Playlists { get; } = [];
    public ObservableCollection<Track> PlaylistTracks { get; } = [];
    public ObservableCollection<QueueItemViewModel> QueueTracks { get; } = [];
    public ObservableCollection<Track> SelectedLibraryTracks { get; } = [];
    public ObservableCollection<TrackPickerItemViewModel> TrackPickerItems { get; } = [];
    public ObservableCollection<TrackPickerItemViewModel> VisibleTrackPickerItems { get; } = [];
    public ObservableCollection<PlaylistChoiceViewModel> PlaylistChoices { get; } = [];
    public ObservableCollection<SpectrumBarViewModel> SpectrumBars { get; } = [];
    public IReadOnlyList<string> SortOptions { get; } = ["标题", "歌手", "专辑", "添加时间", "时长"];
    public IReadOnlyList<string> PlayModeOptions { get; } = ["顺序播放", "列表循环", "单曲循环", "随机播放"];

    [ObservableProperty] private string currentPage = "首页";
    [ObservableProperty] private string searchText = string.Empty;
    [ObservableProperty] private string localSearchText = string.Empty;
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
    public string CurrentArtist => CurrentTrack?.Artist ?? "搜索在线歌曲，或从本地音乐里挑一首吧";
    public string CurrentAlbum => CurrentTrack?.Album ?? "猫耳雷达正在待机";
    public string CurrentCoverPath => CurrentTrack?.CoverCachePath ?? string.Empty;
    public string CurrentCoverUrl => CurrentTrack?.CoverUrl ?? string.Empty;
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
    public bool IsSearchPage => CurrentPage == "搜索";
    public bool HasModalLayer => IsTrackPickerVisible || IsPlaylistPickerVisible || LibraryRemovalConfirmationVisible || RecentClearConfirmationVisible || DeleteConfirmationVisible;
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
    public bool CanSeek => !_stopping && CurrentTrack is not null && _ffmpeg.IsAvailable &&
        _playback.Snapshot.CanSeek && DurationSeconds > 0 && PlaybackState is not PlaybackState.Loading;
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

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    private async Task InitializeCoreAsync()
    {
        if (_initialized) return;
        _initialized = true;
        UpdateGreeting();
        _greetingTimer.Start();
        try
        {
            _settings = await _settingsService.LoadAsync(_lifetimeCts.Token);
            _settingsLoaded = true;
            OnlineSearch.LoadHistory(_settings.SearchHistory);
            TrackBackgroundOperation(WarmGatewayAsync());
            await _ffmpeg.ValidateAsync(_lifetimeCts.Token);
            OnPropertyChanged(nameof(FfmpegStatus));
            OnPropertyChanged(nameof(FfmpegVersion));
            OnPropertyChanged(nameof(FfmpegPath));
            OnPropertyChanged(nameof(FfmpegSharedStatus));
            await _library.InitializeAsync(_lifetimeCts.Token);
            VolumePercent = _settings.Volume * 100;
            IsMuted = _settings.IsMuted;
            IncludeSubdirectories = _settings.IncludeSubdirectories;
            SpectrumEnabled = _settings.SpectrumEnabled;
            SpectrumFps = Math.Clamp(_settings.SpectrumFps, 10, 60);
            ReduceMotion = _settings.ReduceMotion;
            ApplyFeaturePreferences();
            _audio.Volume = _settings.Volume;
            _audio.IsMuted = _settings.IsMuted;
            _queue.PlayMode = _settings.PlayMode;
            _spectrum.FramesPerSecond = SpectrumFps;
            _spectrum.IsEnabled = ShowSpectrum;
            _preferencesApplied = true;
            UpdatePlayModeText();
            await RefreshAllAsync();
            var savedTracks = await _catalog.GetTracksByIdsAsync(_settings.QueueTrackIds, _lifetimeCts.Token);
            var byId = savedTracks.ToDictionary(x => x.Id);
            var restored = _settings.QueueTrackIds.Where(byId.ContainsKey).Select(id => byId[id])
                .Where(x => x.IsOnline || x.FileExists).DistinctBy(x => x.Id).ToArray();
            var currentIndex = _settings.LastTrackId is { } currentId ? Array.FindIndex(restored, x => x.Id == currentId) : -1;
            if (currentIndex < 0) { currentIndex = restored.Length > 0 ? 0 : -1; _settings.LastPosition = TimeSpan.Zero; }
            _queue.Restore(restored, currentIndex);
            _queueRestored = true;
            if (_queue.Current is { } last)
            {
                CurrentTrack = last;
                DurationSeconds = Math.Max(0, last.Duration.TotalSeconds);
                DurationText = TimeFormatter.Format(last.Duration);
                PositionSeconds = SeekSeconds = Math.Clamp(_settings.LastPosition.TotalSeconds, 0, DurationSeconds);
                CurrentTimeText = TimeFormatter.Format(TimeSpan.FromSeconds(PositionSeconds));
                ApplyPlaybackState(PlaybackState.Stopped);
            }
            StatusMessage = _settings.QueueTrackIds.Count != restored.Length ? $"已跳过 {_settings.QueueTrackIds.Count - restored.Length} 首不存在的队列歌曲" : _ffmpeg.IsAvailable ? "音乐舱已就绪；在线搜索位于搜索页" : "界面和音乐库可用；播放前请放置 FFmpeg";
        }
        catch (OperationCanceledException) when (_lifetimeCts.IsCancellationRequested) { }
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

    public Task SaveStateAsync(double width, double height) => _shutdownTask ??= ShutdownCoreAsync(width, height);

    private async Task ShutdownCoreAsync(double width, double height)
    {
        _stopping = true;
        if (_settingsLoaded)
        {
            if (_preferencesApplied)
            {
                _settings.Volume = (float)(VolumePercent / 100d);
                _settings.IsMuted = IsMuted;
                _settings.PlayMode = _queue.PlayMode;
                _settings.IncludeSubdirectories = IncludeSubdirectories;
                _settings.SpectrumEnabled = SpectrumEnabled;
                _settings.SpectrumFps = SpectrumFps;
                _settings.ReduceMotion = ReduceMotion;
                ExportFeaturePreferences();
            }
            _settings.WindowWidth = width;
            _settings.WindowHeight = height;
            _settings.SearchHistory = OnlineSearch.ExportHistory().ToList();
            if (_queueRestored)
            {
                _settings.LastTrackId = CurrentTrack?.Id ?? _queue.Current?.Id;
                if (_hasPlayed) _settings.LastPosition = _playback.Snapshot.Position;
                _settings.QueueTrackIds = _queue.Items.Select(x => x.Id).ToList();
                _settings.QueueIndex = _queue.CurrentIndex;
            }
        }
        _lifetimeCts.Cancel();
        _seekCoordinator.Cancel(); _playCountCts?.Cancel(); _searchCts?.Cancel(); _scanCts?.Cancel(); _snackbarCts?.Cancel();
        _greetingTimer.Stop();
        _greetingTimer.Tick -= OnGreetingTimerTick;
        try
        {
            Task[] pending;
            lock (_backgroundGate) pending = _backgroundTasks.Where(x => !x.IsCompleted).ToArray();
            var all = pending.Concat(new[] { _initializationTask, _activeImportTask }.OfType<Task>()).Distinct().ToArray();
            if (all.Length > 0)
            {
                try { await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception ex) { Log.Warning(ex, "等待受管任务结束时遇到问题，继续回收资源"); }
            }
            if (_settingsLoaded) await _settingsService.SaveAsync(_settings);
        }
        catch (Exception ex) { Log.Warning(ex, "退出时保存配置失败，原配置保留"); }
        finally
        {
            _trackStateStore.FavoriteChanged -= OnFavoriteChanged;
            _playback.SnapshotChanged -= OnPlaybackSnapshotChanged;
            _playback.PlaybackStarted -= OnPlaybackStarted;
            _queue.QueueChanged -= OnQueueChanged;
            _queue.CurrentChanged -= OnQueueCurrentChanged;
            _spectrum.SpectrumUpdated -= OnSpectrumUpdated;
            _spectrumDispatcher.Dispose();
            OnlineSearch.PlayRequested -= OnOnlinePlayRequested;
            OnlineSearch.FavoriteRequested -= OnOnlineFavoriteRequested;
            OnlineSearch.QueueRequested -= OnOnlineQueueRequested;
            OnlineSearch.PlaylistRequested -= OnOnlinePlaylistRequested;
            OnlineSearch.HistoryChanged -= OnSearchHistoryChanged;
            OnlineSearch.PropertyChanged -= OnOnlineSearchPropertyChanged;
            OnlineSearch.LoginRequested -= OnSearchLoginRequested;
            _seekCoordinator.Dispose();
            _snackbarCts?.Dispose();
            if ((object)OnlineSearch is IDisposable disposableSearch) disposableSearch.Dispose();
            Accounts.AccountChanged -= OnProviderAccountChanged;
            Accounts.Dispose();
            await DisposeFeaturesAsync();
            try { await _playback.DisposeAsync(); } catch (Exception ex) { Log.Warning(ex, "释放播放协调器失败"); }
            try { await _audio.DisposeAsync(); } catch (Exception ex) { Log.Warning(ex, "释放音频失败"); }
            try { await _gateway.DisposeAsync(); } catch (Exception ex) { Log.Warning(ex, "释放在线接口失败"); }
        }
    }

    [RelayCommand] private void Navigate(string page)
    {
        if (_stopping || HasModalLayer) return;
        CurrentPage = page;
        if (page == "我的收藏" || page == "最近播放" || page == "我的歌单") _ = RefreshSecondaryPageAsync(page);
    }
    [RelayCommand] private void NavigateSearch() => Navigate("搜索");
    [RelayCommand] private void OpenAccounts()
    {
        if (_stopping || HasModalLayer) return;
        Navigate("设置");
        TrackBackgroundOperation(Accounts.RefreshAsync());
        AccountsRequested?.Invoke(this, EventArgs.Empty);
    }
    [RelayCommand] private void BackToSearch() => Navigate(_nowPlayingReturnPage);

    [RelayCommand] private async Task RefreshAsync() => await RefreshLibraryAsync();
    [RelayCommand] private async Task RetryLibraryLoadAsync() => await RefreshLibraryAsync();

    [RelayCommand] private async Task PlayTrackAsync(Track? track)
    {
        await PlayFromContextAsync(track, GetPlaybackContext(track), false);
    }

    [RelayCommand] private async Task PreviewTrackAsync(Track? track) => await PlayFromContextAsync(track, GetPlaybackContext(track), true);

    private IReadOnlyList<Track> GetPlaybackContext(Track? track)
    {
        if (IsSearchPage && track is not null)
            return OnlineSearch.Groups.FirstOrDefault(x => x.Tracks.Any(t => t.Id == track.Id))?.Tracks.ToArray() ?? [track];
        if (IsFavoritesPage) return Favorites.ToArray();
        if (IsRecentPage) return RecentTracks.Select(x => x.Track).ToArray();
        if (IsPlaylistsPage) return PlaylistTracks.ToArray();
        if (IsNowPlayingPage || IsQueueVisible) return _queue.Items.ToArray();
        return Tracks.ToArray();
    }

    private async Task PlayFromContextAsync(Track? track, IReadOnlyList<Track>? context, bool preview, TimeSpan? startPosition = null)
    {
        if (track is null || _stopping) return;
        try
        {
            if (!_ffmpeg.IsAvailable) { StatusMessage = "缺少 FFmpeg，暂时无法播放；设置页可查看放置路径"; return; }
            CancelSeek();
            var result = await _playback.PlayAsync(track, context, preview, startPosition, _lifetimeCts.Token);
            if (result.Track is not null) OnlineSearch.ApplyAvailability(result.Track);
            if (!result.Started && !string.IsNullOrWhiteSpace(result.Message))
                ShowSnackbar(result.Message, SnackbarTone.Warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error(ex, "播放歌曲失败"); StatusMessage = ex.Message; ShowSnackbar(ex.Message, SnackbarTone.Error); }
    }

    [RelayCommand] private async Task TogglePlayAsync()
    {
        if (!CanTogglePlay || _stopping) return;
        if (_playback.Snapshot.Track is null)
        {
            var candidate = CurrentTrack ?? _queue.Current ?? SelectedTrack ?? Tracks.FirstOrDefault();
            await PlayFromContextAsync(candidate, _queue.Items.Count > 0 ? _queue.Items.ToArray() : GetPlaybackContext(candidate), false,
                !_hasPlayed && candidate?.Id == _settings.LastTrackId ? _settings.LastPosition : null);
        }
        else await RunPlaybackActionAsync(() => _playback.ToggleAsync());
    }

    [RelayCommand] private async Task StopAsync()
    {
        _playCountCts?.Cancel();
        CancelSeek();
        await RunPlaybackActionAsync(() => _playback.StopAsync());
        UpdatePosition(TimeSpan.Zero);
    }
    [RelayCommand] private async Task PreviousAsync() { CancelSeek(); await RunPlaybackActionAsync(() => _playback.PreviousAsync()); }
    [RelayCommand] private async Task NextAsync() { CancelSeek(); await RunPlaybackActionAsync(() => _playback.NextAsync()); }
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
            if (track.IsOnline)
            {
                var canonical = await _catalog.EnsureOnlineTrackAsync(track, _lifetimeCts.Token);
                track.Id = canonical.Id;
            }
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

    [RelayCommand] private async Task AddToQueueAsync(Track? track) { if (track is not null) await RunPlaybackActionAsync(() => _playback.AddToQueueAsync(track)); }
    [RelayCommand] private async Task PlayNextAsync(Track? track) { if (track is not null) await RunPlaybackActionAsync(() => _playback.PlayNextAsync(track)); }
    [RelayCommand] private async Task PlayQueueItemAsync(QueueItemViewModel? item)
    {
        if (item is null) return;
        await PlayFromContextAsync(item.Track, _queue.Items.ToArray(), false);
    }
    [RelayCommand] private async Task RemoveQueueItemAsync(QueueItemViewModel? item) { if (item is not null) await RunPlaybackActionAsync(() => _playback.RemoveQueueItemAsync(item.Track.Id)); }
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
            foreach (var id in ids) await _playback.RemoveQueueItemAsync(id);
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
    [RelayCommand] private void OpenContainingFolder(Track? track) { var folder = track is null || track.IsOnline ? null : Path.GetDirectoryName(track.FilePath); if (folder is not null) OpenFolder(folder); }
    [RelayCommand] private void ShowTrackInfo(Track? track) { if (track is not null) StatusMessage = $"{track.Title} · {track.Artist} · {track.Album} · {TimeFormatter.Format(track.Duration)} · {track.ProviderName} · {track.AvailabilityText}"; }
    [RelayCommand] private void FindOtherVersion(Track? track)
    {
        if (track is null || _stopping) return;
        NavigateSearch();
        OnlineSearch.FindOtherCommand.Execute(track);
    }

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
        if (track is null || _stopping) return;
        try
        {
            if (track.IsOnline) { var canonical = await _catalog.EnsureOnlineTrackAsync(track, _lifetimeCts.Token); track.Id = canonical.Id; }
            await OpenPlaylistPickerCoreAsync([track.Id]);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "准备添加到歌单失败"); ShowSnackbar("歌曲记录保存失败，请重试", SnackbarTone.Error); }
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
    [RelayCommand] private async Task RemoveSelectedFromPlaylistAsync(Track? track)
    {
        if (!CanEditPlaylist || SelectedPlaylist is not { } playlist || track is null) return;
        var originalIndex = PlaylistTracks.ToList().FindIndex(x => x.Id == track.Id);
        _playlistOperationInProgress = true; NotifyFeatureState();
        try
        {
            await _playlists.RemoveTrackAsync(playlist.Id, track.Id, _lifetimeCts.Token);
            if (SelectedPlaylist?.Id == playlist.Id) await LoadPlaylistTracksAsync();
            ShowSnackbar("已从歌单移除，可撤销", SnackbarTone.Success, async token =>
            {
                await _playlists.AddTrackAsync(playlist.Id, track.Id, token);
                await _playlists.MoveTrackAsync(playlist.Id, track.Id, originalIndex, token);
                if (SelectedPlaylist?.Id == playlist.Id) await LoadPlaylistTracksAsync();
            }, track.Id);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stopping) ShowSnackbar("移除失败：" + ex.Message, SnackbarTone.Error); }
        finally { _playlistOperationInProgress = false; NotifyFeatureState(); }
    }

    [RelayCommand] private void SelectAllFilteredTracks() => UpdateLibrarySelection(Tracks);
    [RelayCommand] private void ClearLibrarySelection() => UpdateLibrarySelection([]);

    [RelayCommand] private void OpenDataFolder() => OpenFolder(_paths.Root);
    [RelayCommand] private void OpenLogsFolder() => OpenFolder(_paths.LogsDirectory);

    partial void OnCurrentPageChanged(string value)
    {
        if (value == "设置") TrackBackgroundOperation(Accounts.RefreshAsync());
        else Accounts.CloseLogin();
        _spectrum.IsEnabled = ShowSpectrum;
        OnPropertyChanged(nameof(ShowSpectrum));
        foreach (var name in new[] { nameof(IsHomePage), nameof(IsLibraryPage), nameof(IsNowPlayingPage), nameof(IsRecentPage), nameof(IsFavoritesPage), nameof(IsPlaylistsPage), nameof(IsSettingsPage), nameof(IsSearchPage) }) OnPropertyChanged(name);
    }
    partial void OnCurrentTrackChanged(Track? value)
    {
        value?.RefreshFileAvailability();
        OnPropertyChanged(nameof(CurrentTitle)); OnPropertyChanged(nameof(CurrentArtist)); OnPropertyChanged(nameof(CurrentAlbum)); OnPropertyChanged(nameof(CurrentCoverPath)); OnPropertyChanged(nameof(CurrentCoverUrl));
        NotifyPlaybackActionChanged();
        NotifyFavoriteCommandState();
        NotifyFeatureState();
    }
    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        OnPropertyChanged(nameof(HasSelectedPlaylist));
        NotifyFeatureState();
        if (value is not null) { PlaylistName = value.Name; TrackBackgroundOperation(LoadPlaylistTracksAsync()); }
        else { Replace(PlaylistTracks, []); ApplyPlaylistFilter(); }
    }
    partial void OnSearchTextChanged(string value)
    {
        if (_syncingSearch || _stopping) return;
        OnlineSearch.Query = value;
        if (!string.IsNullOrWhiteSpace(value) && !HasModalLayer) CurrentPage = "搜索";
    }
    partial void OnLocalSearchTextChanged(string value) => DebounceSearch();
    partial void OnIsTrackPickerVisibleChanged(bool value) => OnPropertyChanged(nameof(HasModalLayer));
    partial void OnIsPlaylistPickerVisibleChanged(bool value) => OnPropertyChanged(nameof(HasModalLayer));
    partial void OnLibraryRemovalConfirmationVisibleChanged(bool value) => OnPropertyChanged(nameof(HasModalLayer));
    partial void OnRecentClearConfirmationVisibleChanged(bool value) => OnPropertyChanged(nameof(HasModalLayer));
    partial void OnDeleteConfirmationVisibleChanged(bool value) => OnPropertyChanged(nameof(HasModalLayer));
    partial void OnPlaylistTrackSearchTextChanged(string value) => ApplyTrackPickerFilter();
    partial void OnSortFieldChanged(string value) => ApplyFilter();
    partial void OnVolumePercentChanged(double value) { _audio.Volume = (float)Math.Clamp(value / 100d, 0, 1); }
    partial void OnIsMutedChanged(bool value) { _audio.IsMuted = value; }
    partial void OnSpectrumEnabledChanged(bool value) { _spectrum.IsEnabled = ShowSpectrum; OnPropertyChanged(nameof(ShowSpectrum)); }
    partial void OnReduceMotionChanged(bool value) { _spectrum.IsEnabled = ShowSpectrum; OnPropertyChanged(nameof(ShowSpectrum)); }
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
            nameof(IconSize), nameof(IconButtonSize), nameof(PrimaryPlayButtonSize), nameof(NavigationIconWidth), nameof(NavigationLabelWidth)
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
    private async Task LoadPlaylistTracksAsync()
    {
        var playlistId = SelectedPlaylist?.Id;
        if (playlistId is null) return;
        var generation = ++_playlistLoadGeneration;
        _playlistIsLoading = true; NotifyFeatureState();
        try
        {
            var tracks = await _playlists.GetTracksAsync(playlistId.Value, _lifetimeCts.Token);
            if (_stopping || generation != _playlistLoadGeneration || SelectedPlaylist?.Id != playlistId) return;
            Replace(PlaylistTracks, tracks); ApplyPlaylistFilter();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stopping && SelectedPlaylist?.Id == playlistId) ShowSnackbar("歌单加载失败：" + ex.Message, SnackbarTone.Error); }
        finally { if (generation == _playlistLoadGeneration) { _playlistIsLoading = false; NotifyFeatureState(); } }
    }

    private void ApplyFilter()
    {
        IEnumerable<Track> query = _allTracks.Where(x => TrackSearch.Matches(x, LocalSearchText));
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
            var searchActive = !string.IsNullOrWhiteSpace(LocalSearchText);
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
            var session = _playback.CurrentSessionId;
            await _seekCoordinator.SubmitAsync(target, async (value, token) =>
            {
                if (session != _playback.CurrentSessionId) throw new OperationCanceledException(token);
                await _playback.SeekAsync(value, token);
            }, delay);
            if (version != _seekRequestVersion) return;
            PositionSeconds = target.TotalSeconds;
            SeekSeconds = target.TotalSeconds;
            CurrentTimeText = TimeFormatter.Format(target);
            LyricsPresentation.RequestImmediateFollow();
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
        Dispatcher.UIThread.Post(() => { if (!_stopping) ApplyFavoriteState(change.TrackId, change.IsFavorite); });
    }

    private void ApplyFavoriteState(Guid trackId, bool favorite)
    {
        foreach (var track in EnumerateTrackInstances(trackId)) track.IsFavorite = favorite;
        OnlineSearch.ApplyFavoriteState(trackId, favorite);

        var existing = Favorites.FirstOrDefault(x => x.Id == trackId);
        if (!favorite && existing is not null) Favorites.Remove(existing);
        else if (favorite && existing is null)
        {
            var source = _allTracks.FirstOrDefault(x => x.Id == trackId)
                ?? RecentTracks.Select(x => x.Track).FirstOrDefault(x => x.Id == trackId)
                ?? PlaylistTracks.FirstOrDefault(x => x.Id == trackId)
                ?? QueueTracks.Select(x => x.Track).FirstOrDefault(x => x.Id == trackId)
                ?? OnlineSearch.Groups.SelectMany(x => x.Tracks).FirstOrDefault(x => x.Id == trackId)
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
        foreach (var track in OnlineSearch.Groups.SelectMany(x => x.Tracks).Where(x => x.Id == trackId)) yield return track;
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
    }

    private void UpdateSpectrum(IReadOnlyList<float> bands)
    {
        if (_stopping || !ShowSpectrum) return;
        for (var i = 0; i < Math.Min(SpectrumBars.Count, bands.Count); i++) SpectrumBars[i].SetAmplitude(bands[i]);
    }

    private void RefreshQueue()
    {
        QueuePresentation.Synchronize(QueueTracks, _queue.Items, CurrentTrack?.Id);
        OnPropertyChanged(nameof(QueueCount)); NotifyFeatureState();
    }

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

    private async Task WarmGatewayAsync()
    {
        try { await _gateway.EnsureReadyAsync(_lifetimeCts.Token); await Accounts.RefreshAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "在线接口暂不可用，本地音乐仍可使用"); }
    }

    private void OnProviderAccountChanged(object? sender, string providerId)
    {
        foreach (var track in OnlineSearch.Groups.SelectMany(x => x.Tracks)
            .Concat(QueueTracks.Select(x => x.Track)).Concat(Favorites).Concat(PlaylistTracks)
            .Concat(RecentTracks.Select(x => x.Track)).Concat(CurrentTrack is null ? [] : new[] { CurrentTrack })
            .Where(x => x.ProviderId == providerId).Distinct())
        {
            track.Availability = MusicAvailability.Unknown;
            track.RestrictionReason = "账号授权已变化，将重新确认播放权限";
        }
    }

    private void TrackBackgroundOperation(Task task)
    {
        lock (_backgroundGate) _backgroundTasks.Add(task);
        _ = ObserveBackgroundOperationAsync(task);
    }
    private async Task ObserveBackgroundOperationAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "后台操作失败"); }
        finally { lock (_backgroundGate) _backgroundTasks.Remove(task); }
    }

    private void OnQueueChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => { if (!_stopping) RefreshQueue(); });
    private void OnQueueCurrentChanged(object? sender, Track? track) => OnQueueChanged(sender, EventArgs.Empty);
    private void OnSpectrumUpdated(object? sender, IReadOnlyList<float> bands)
    {
        if (!_stopping) _spectrumDispatcher.Submit(bands);
    }

    private void OnPlaybackSnapshotChanged(object? sender, PlaybackSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stopping || snapshot.SessionId != _playback.CurrentSessionId) return;
            var currentChanged = CurrentTrack?.Id != snapshot.Track?.Id;
            CurrentTrack = snapshot.Track;
            if (snapshot.Track is { } track)
            {
                foreach (var instance in EnumerateTrackInstances(track.Id))
                {
                    instance.Availability = track.Availability;
                    instance.RestrictionReason = track.RestrictionReason;
                }
            }
            ApplyPlaybackState(snapshot.State);
            NotifyFeatureState();
            if (snapshot.IsPreview && snapshot.State == PlaybackState.Playing) PlaybackStateText = "正在试听";
            UpdatePosition(snapshot.Position);
            if (!string.IsNullOrWhiteSpace(snapshot.Message)) StatusMessage = snapshot.Message;
            else if (snapshot.PendingTrack is { } pending) StatusMessage = $"正在准备：{pending.Title} · {pending.ProviderName}";
            else if (snapshot.Track is { } current && snapshot.State == PlaybackState.Playing)
                StatusMessage = $"{(snapshot.IsPreview ? "正在试听" : "正在播放")}：{current.Title} · {current.ProviderName}";
            else if (snapshot.Track is { } selected && snapshot.State is PlaybackState.Paused or PlaybackState.Stopped or PlaybackState.Buffering)
                StatusMessage = $"{StateText(snapshot.State)}：{selected.Title} · {selected.ProviderName}";
            if (currentChanged) RefreshQueue();
        });
    }


    private void OnPlaybackStarted(object? sender, PlaybackRequestResult result)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_stopping || !result.Started || result.Track is null || !_recordedSessions.Add(result.SessionId)) return;
            if (_recordedSessions.Count > 256) { _recordedSessions.Clear(); _recordedSessions.Add(result.SessionId); }
            _hasPlayed = true;
            TrackBackgroundOperation(RecordPlaybackStartAsync(result.Track.Id));
            if (result.SessionId == _playback.CurrentSessionId) SchedulePlayCount(result.Track.Id, _playback.CurrentIntentId);
        });
    }
    private async Task RecordPlaybackStartAsync(Guid trackId)
    {
        try
        {
            await _library.RecordPlaybackAsync(trackId, TimeSpan.Zero, _lifetimeCts.Token);
            if (IsRecentPage && !_stopping) await RefreshRecentAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "播放历史保存失败");
            if (!_stopping) ShowSnackbar("歌曲正在播放，但播放历史保存失败", SnackbarTone.Warning);
        }
    }

    private void OnOnlinePlayRequested(object? sender, SearchPlayRequest request) =>
        TrackBackgroundOperation(PlayFromContextAsync(request.Track, request.Context, request.Preview));
    private void OnOnlineFavoriteRequested(object? sender, Track track) => TrackBackgroundOperation(ToggleFavoriteAsync(track));
    private void OnOnlineQueueRequested(object? sender, Track track) => TrackBackgroundOperation(AddToQueueAsync(track));
    private void OnOnlinePlaylistRequested(object? sender, Track track) => TrackBackgroundOperation(OpenPlaylistPickerAsync(track));
    private void OnOnlineSearchPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OnlineSearchViewModel.Query) || _syncingSearch || _stopping) return;
        _syncingSearch = true;
        try { SearchText = OnlineSearch.Query; }
        finally { _syncingSearch = false; }
    }
    private void OnSearchHistoryChanged(object? sender, EventArgs e)
    {
        if (_stopping || !_settingsLoaded) return;
        _settings.SearchHistory = OnlineSearch.ExportHistory().ToList();
        TrackBackgroundOperation(_settingsService.SaveAsync(_settings, _lifetimeCts.Token));
    }
    private async Task RunPlaybackActionAsync(Func<Task> action)
    {
        if (_stopping) return;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Warning(ex, "播放操作失败"); ShowSnackbar("播放操作失败，请重试或查看来源状态", SnackbarTone.Warning); }
    }

    private void SchedulePlayCount(Guid trackId, long intentId)
    {
        _playCountCts?.Cancel(); _playCountCts?.Dispose(); _playCountCts = new CancellationTokenSource();
        var token = _playCountCts.Token;
        TrackBackgroundOperation(Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), token);
                if (_playback.CurrentIntentId == intentId && CurrentTrack?.Id == trackId && _audio.Position >= TimeSpan.FromSeconds(10))
                    await _library.RecordPlaybackAsync(trackId, _audio.Position, token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Log.Warning(ex, "播放计数保存失败"); }
        }, token));
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
    private static void OpenFolder(string path) { Directory.CreateDirectory(path); Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); }
}
