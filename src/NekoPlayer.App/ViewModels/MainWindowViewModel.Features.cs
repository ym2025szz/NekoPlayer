using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NekoPlayer.App.Services;
using NekoPlayer.Core.Enums;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.ViewModels;

public partial class MainWindowViewModel
{
    private IDesktopLyricsService _desktopLyrics = null!;
    private WindowsMediaControlsService _mediaControls = null!;
    private bool _syncingDesktopSettings;
    private bool _playlistIsLoading;
    private int _playlistLoadGeneration;
    private string _nowPlayingReturnPage = "搜索";
    public LyricsPresentationService LyricsPresentation { get; private set; } = null!;
    public SleepTimerService SleepTimer { get; private set; } = null!;
    public ObservableCollection<Track> VisiblePlaylistTracks { get; } = [];
    public bool CanOpenNowPlaying => !_stopping && CurrentTrack is not null;
    public bool ShowSpectrum => SpectrumEnabled && !ReduceMotion && IsNowPlayingPage && WindowVisible;
    public bool CanEditQueue => !_stopping && QueueTracks.Count > 0 && _playback.Snapshot.PendingTrack is null && PlaybackState != PlaybackState.Loading;
    public int QueueCount => QueueTracks.Count;
    public bool HasSelectedPlaylist => SelectedPlaylist is not null;
    public bool CanEditPlaylist => !_stopping && SelectedPlaylist is not null && !_playlistIsLoading && !_playlistOperationInProgress;
    public string SystemMediaStatus => _mediaControls?.StatusText ?? "系统媒体控制准备中";
    public string NowPlayingReturnLabel => _nowPlayingReturnPage == "搜索" ? "返回搜索结果" : $"返回{_nowPlayingReturnPage}";
    public event EventHandler? LocateCurrentQueueRequested;

    [ObservableProperty] private bool closeToTray = true;
    [ObservableProperty] private bool systemMediaControlsEnabled = true;
    [ObservableProperty] private bool desktopLyricsEnabled;
    [ObservableProperty] private bool desktopLyricsLocked;
    [ObservableProperty] private double desktopLyricsFontSize = 28;
    [ObservableProperty] private double desktopLyricsOpacity = 1;
    [ObservableProperty] private int customSleepMinutes = 30;
    [ObservableProperty] private string playlistSearchText = string.Empty;
    [ObservableProperty] private bool windowVisible = true;

    private void InitializeFeatures(LyricsPresentationService lyrics, IDesktopLyricsService desktop,
        SleepTimerService sleep, WindowsMediaControlsService media)
    {
        LyricsPresentation = lyrics; _desktopLyrics = desktop; SleepTimer = sleep; _mediaControls = media;
        desktop.SettingsChanged += OnDesktopSettingsChanged;
        media.StatusChanged += OnMediaStatusChanged;
    }
    private void ApplyFeaturePreferences()
    {
        CloseToTray = _settings.CloseToTray;
        SystemMediaControlsEnabled = _settings.SystemMediaControlsEnabled;
        _desktopLyrics.Apply(_settings.DesktopLyrics);
        SyncDesktopSettings();
    }
    private void ExportFeaturePreferences()
    {
        _settings.CloseToTray = CloseToTray;
        _settings.SystemMediaControlsEnabled = SystemMediaControlsEnabled;
        _settings.DesktopLyrics = _desktopLyrics.Export();
    }
    private async Task DisposeFeaturesAsync()
    {
        _desktopLyrics.SettingsChanged -= OnDesktopSettingsChanged;
        _mediaControls.StatusChanged -= OnMediaStatusChanged;
        await SleepTimer.DisposeAsync();
        _mediaControls.Dispose();
        if (_desktopLyrics is IDisposable desktop) desktop.Dispose();
        LyricsPresentation.Dispose();
    }
    private void OnDesktopSettingsChanged(object? sender, EventArgs e) => SyncDesktopSettings();
    private void SyncDesktopSettings()
    {
        if (_stopping) return;
        _syncingDesktopSettings = true;
        try
        {
            var value = _desktopLyrics.Export();
            DesktopLyricsEnabled = value.IsEnabled; DesktopLyricsLocked = value.IsLocked;
            DesktopLyricsFontSize = value.FontSize; DesktopLyricsOpacity = value.Opacity;
        }
        finally { _syncingDesktopSettings = false; }
    }
    private void ChangeDesktopSettings(Action<DesktopLyricsSettings> update)
    {
        if (_syncingDesktopSettings || _stopping) return;
        var value = _desktopLyrics.Export(); update(value); _desktopLyrics.Apply(value); SyncDesktopSettings();
    }
    partial void OnDesktopLyricsEnabledChanged(bool value) { if (!_syncingDesktopSettings && !_stopping) _desktopLyrics.SetEnabled(value); }
    partial void OnDesktopLyricsLockedChanged(bool value) => ChangeDesktopSettings(x => x.IsLocked = value);
    partial void OnDesktopLyricsFontSizeChanged(double value) => ChangeDesktopSettings(x => x.FontSize = value);
    partial void OnDesktopLyricsOpacityChanged(double value) => ChangeDesktopSettings(x => x.Opacity = value);
    partial void OnSystemMediaControlsEnabledChanged(bool value) { if (_mediaControls is not null) _mediaControls.IsEnabled = value; }
    private void OnMediaStatusChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(SystemMediaStatus)));
    partial void OnWindowVisibleChanged(bool value) { _spectrum.IsEnabled = ShowSpectrum; OnPropertyChanged(nameof(ShowSpectrum)); }
    [RelayCommand] private void ToggleDesktopLyrics() => _desktopLyrics.Toggle();
    [RelayCommand] private void UnlockDesktopLyrics() => _desktopLyrics.Unlock();
    [RelayCommand] private void StartSleepTimer(int minutes) { if (!_stopping) SleepTimer.Start(minutes); }
    [RelayCommand] private void StartCustomSleepTimer() => StartSleepTimer(Math.Clamp(CustomSleepMinutes, 1, 240));
    [RelayCommand] private void CancelSleepTimer() => SleepTimer.Cancel();

    private void NotifyFeatureState()
    {
        foreach (var property in new[] { nameof(CanOpenNowPlaying), nameof(CanEditQueue), nameof(CanEditPlaylist), nameof(HasSelectedPlaylist) }) OnPropertyChanged(property);
    }
    partial void OnCurrentPageChanging(string? oldValue, string newValue)
    {
        if (newValue != "正在播放" || oldValue == "正在播放") return;
        _nowPlayingReturnPage = oldValue ?? "搜索";
        OnPropertyChanged(nameof(NowPlayingReturnLabel));
    }
    [RelayCommand] private void MoveQueueUp(QueueItemViewModel? item) => MoveQueueRelative(item, -1);
    [RelayCommand] private void MoveQueueDown(QueueItemViewModel? item) => MoveQueueRelative(item, 1);
    private void MoveQueueRelative(QueueItemViewModel? item, int delta)
    {
        if (!CanEditQueue || item is null) return;
        var index = _queue.Items.ToList().FindIndex(x => x.Id == item.Track.Id);
        if (index >= 0) _queue.Move(item.Track.Id, index + delta);
    }
    public void MoveQueueTrack(Guid draggedId, Guid targetId)
    {
        if (!CanEditQueue || draggedId == targetId) return;
        var index = _queue.Items.ToList().FindIndex(x => x.Id == targetId);
        if (index >= 0) _queue.Move(draggedId, index);
    }
    [RelayCommand] private void ClearPendingQueue()
    {
        if (!CanEditQueue) return;
        _queue.ClearPending(); ShowSnackbar("已清空待播歌曲，保留当前曲", SnackbarTone.Success);
    }
    [RelayCommand] private void LocateCurrentQueue() => LocateCurrentQueueRequested?.Invoke(this, EventArgs.Empty);

    partial void OnPlaylistSearchTextChanged(string value) => ApplyPlaylistFilter();
    private void ApplyPlaylistFilter() => Replace(VisiblePlaylistTracks, PlaylistTracks.Where(x => NekoPlayer.Core.Services.TrackSearch.Matches(x, PlaylistSearchText)));
    [RelayCommand] private async Task PlayPlaylistAsync()
    {
        if (!CanEditPlaylist) return;
        var tracks = PlaylistTracks.ToArray();
        var first = tracks.FirstOrDefault(x => x.CanAttemptPlayback);
        if (first is null) { ShowSnackbar("歌单没有可尝试完整播放的歌曲，可单独选择试听", SnackbarTone.Warning); return; }
        await PlayFromContextAsync(first, tracks, preview: false);
    }
    [RelayCommand] private async Task AddPlaylistToQueueAsync()
    {
        if (!CanEditPlaylist) return;
        var tracks = PlaylistTracks.ToArray();
        foreach (var track in tracks) { if (_stopping) return; await _playback.AddToQueueAsync(track); }
        ShowSnackbar($"已将歌单中的 {tracks.Length} 首歌曲加入队列", SnackbarTone.Success);
    }
    [RelayCommand] private Task MovePlaylistTrackUpAsync(Track? track) => MovePlaylistRelativeAsync(track, -1);
    [RelayCommand] private Task MovePlaylistTrackDownAsync(Track? track) => MovePlaylistRelativeAsync(track, 1);
    private Task MovePlaylistRelativeAsync(Track? track, int delta)
    {
        if (track is null) return Task.CompletedTask;
        var index = PlaylistTracks.ToList().FindIndex(x => x.Id == track.Id);
        return index < 0 ? Task.CompletedTask : MovePlaylistTrackToAsync(track.Id, index + delta);
    }
    public Task MovePlaylistTrackAsync(Guid draggedId, Guid targetId)
    {
        var index = PlaylistTracks.ToList().FindIndex(x => x.Id == targetId);
        return index < 0 ? Task.CompletedTask : MovePlaylistTrackToAsync(draggedId, index);
    }
    private async Task MovePlaylistTrackToAsync(Guid trackId, int target)
    {
        if (!CanEditPlaylist || SelectedPlaylist is not { } playlist) return;
        _playlistOperationInProgress = true; NotifyFeatureState();
        try
        {
            await _playlists.MoveTrackAsync(playlist.Id, trackId, target, _lifetimeCts.Token);
            if (SelectedPlaylist?.Id == playlist.Id) await LoadPlaylistTracksAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_stopping) ShowSnackbar("歌单排序失败：" + ex.Message, SnackbarTone.Error); }
        finally { _playlistOperationInProgress = false; NotifyFeatureState(); }
    }
    private void OnSearchLoginRequested(object? sender, string providerId)
    {
        if (_stopping || HasModalLayer) return;
        Accounts.FocusProvider(providerId); OpenAccounts();
    }
}
