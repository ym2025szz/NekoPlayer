using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NekoPlayer.App.Controls;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Models;
using NekoPlayer.App.Services;
using System.ComponentModel;
using Serilog;

namespace NekoPlayer.App.Views;

public partial class MainWindow : Window
{
    private bool _shutdownCompleted;
    private bool _shutdownStarted;
    private bool _openedInitialized;
    private Guid? _draggedQueueTrack;
    private Guid? _draggedPlaylistTrack;
    private Avalonia.Point _dragOrigin;
    private ImeCompositionMonitor? _composition;
    private MainWindowViewModel? _observedViewModel;
    private Control? _activeModal;
    private IInputElement? _restoreFocus;
    private readonly DispatcherTimer _layoutTimer;
    public PlayerLifetimeService? Lifetime { get; set; }
    public WindowsMediaControlsService? MediaControls { get; set; }

    public MainWindow()
    {
        InitializeComponent();
        _layoutTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _layoutTimer.Tick += (_, _) =>
        {
            _layoutTimer.Stop();
            if (DataContext is MainWindowViewModel vm) vm.UpdateLayout(Bounds.Width);
        };
        Opened += OnOpened;
        SizeChanged += OnSizeChanged;
        KeyDown += OnWindowKeyDown;
        DataContextChanged += OnDataContextChanged;
        PropertyChanged += (_, change) =>
        {
            if (change.Property == IsVisibleProperty && DataContext is MainWindowViewModel vm) vm.WindowVisible = IsVisible;
        };
        _composition = ImeCompositionMonitor.Attach(SearchBox, active =>
        {
            if (DataContext is MainWindowViewModel vm) vm.OnlineSearch.SetCompositionActive(active);
        });
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.InitializeAsync();
        if (_shutdownStarted) return;
        if (!_openedInitialized)
        {
            _openedInitialized = true;
            Width = Math.Max(MinWidth, vm.WindowWidth);
            Height = Math.Max(MinHeight, vm.WindowHeight);
            MediaControls?.Initialize(this);
        }
        vm.UpdateLayout(Bounds.Width);
    }

    private async void OnLyricJumpRequested(object? sender, LyricsJumpRequestedEventArgs e)
    {
        if (_shutdownStarted || DataContext is not MainWindowViewModel vm || !vm.CanSeek) return;
        await vm.CommitSeekAsync(e.Timestamp.TotalSeconds);
    }

    private static bool IsDragControl(Control? source) => source is Button or TextBox or Slider ||
        source?.FindAncestorOfType<Button>() is not null || source?.FindAncestorOfType<TextBox>() is not null || source?.FindAncestorOfType<Slider>() is not null;
    private bool DraggedEnough(Avalonia.Point point) => Math.Abs(point.X - _dragOrigin.X) + Math.Abs(point.Y - _dragOrigin.Y) >= 6;
    private void OnQueuePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedQueueTrack = null;
        if (DataContext is not MainWindowViewModel { CanEditQueue: true } || IsDragControl(e.Source as Control) || !e.GetCurrentPoint(QueueList).Properties.IsLeftButtonPressed) return;
        var row = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as QueueItemViewModel;
        _draggedQueueTrack = row?.Track.Id; _dragOrigin = e.GetPosition(QueueList);
    }
    private void OnQueuePointerMoved(object? sender, PointerEventArgs e) { }
    private void OnQueuePointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _draggedQueueTrack; _draggedQueueTrack = null;
        if (dragged is null || DataContext is not MainWindowViewModel vm || !DraggedEnough(e.GetPosition(QueueList))) return;
        var target = QueueList.InputHitTest(e.GetPosition(QueueList)) as Control;
        var row = (target as ListBoxItem ?? target?.FindAncestorOfType<ListBoxItem>())?.DataContext as QueueItemViewModel;
        if (row is not null) vm.MoveQueueTrack(dragged.Value, row.Track.Id);
    }
    private void OnPlaylistTrackPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _draggedPlaylistTrack = null;
        if (DataContext is not MainWindowViewModel { CanEditPlaylist: true } || IsDragControl(e.Source as Control) || !e.GetCurrentPoint(PlaylistTrackList).Properties.IsLeftButtonPressed) return;
        _draggedPlaylistTrack = ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>()?.DataContext as Track)?.Id;
        _dragOrigin = e.GetPosition(PlaylistTrackList);
    }
    private void OnPlaylistTrackPointerMoved(object? sender, PointerEventArgs e) { }
    private async void OnPlaylistTrackPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var dragged = _draggedPlaylistTrack; _draggedPlaylistTrack = null;
        if (dragged is null || DataContext is not MainWindowViewModel vm || !DraggedEnough(e.GetPosition(PlaylistTrackList))) return;
        var target = PlaylistTrackList.InputHitTest(e.GetPosition(PlaylistTrackList)) as Control;
        var track = (target as ListBoxItem ?? target?.FindAncestorOfType<ListBoxItem>())?.DataContext as Track;
        if (track is not null) await vm.MovePlaylistTrackAsync(dragged.Value, track.Id);
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_shutdownStarted || vm.HasModalLayer) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择音乐文件", AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("音乐文件") { Patterns = ["*.mp3", "*.flac", "*.wav", "*.aac", "*.m4a", "*.ogg", "*.opus", "*.wma", "*.ape"] }]
        });
        await vm.ImportFilesAsync(files.Select(x => x.TryGetLocalPath()).Where(x => x is not null).Cast<string>());
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "选择音乐文件夹", AllowMultiple = false });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) await vm.ImportFolderAsync(path);
    }

    private void OnTrackDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.SelectedTrack is not null) vm.PlayTrackCommand.Execute(vm.SelectedTrack);
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        _layoutTimer.Stop();
        _layoutTimer.Start();
    }

    private void OnSeekStarted(object? sender, SeekValueEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.BeginSeek();
    }

    private void OnSeekPreviewChanged(object? sender, SeekValueEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.UpdateSeekPreview(e.Value);
    }

    private async void OnSeekCompleted(object? sender, SeekValueEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) await vm.CommitSeekAsync(e.Value);
    }

    private void OnSeekCancelled(object? sender, SeekValueEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm) vm.CancelSeek();
    }

    private void OnLibrarySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox listBox && DataContext is MainWindowViewModel vm)
            vm.UpdateLibrarySelection(listBox.SelectedItems?.OfType<Track>() ?? []);
    }

    private void OnSelectAllLibraryClick(object? sender, RoutedEventArgs e) => LibraryList.SelectAll();

    private void OnClearLibrarySelectionClick(object? sender, RoutedEventArgs e) => LibraryList.UnselectAll();

    private async void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        if (_shutdownStarted) { e.Handled = true; return; }
        if (vm.HasModalLayer)
        {
            if (e.Key == Key.Escape) { vm.HandleEscape(); e.Handled = true; return; }
            if (e.Key == Key.Tab && _activeModal is not null)
            {
                var controls = _activeModal.GetVisualDescendants().OfType<Control>()
                    .Where(x => x.Focusable && x.IsEnabled && x.IsVisible && x.GetVisualAncestors().OfType<Control>().All(a => a.IsVisible)).ToArray();
                if (controls.Length > 0)
                {
                    var focused = FocusManager?.GetFocusedElement();
                    var index = Array.FindIndex(controls, x => ReferenceEquals(x, focused));
                    var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                    controls[(index + step + controls.Length) % controls.Length].Focus();
                }
                e.Handled = true;
                return;
            }
            if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                _activeModal?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault()?.Focus();
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.F && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            vm.HandleEscape();
            e.Handled = true;
            return;
        }

        var source = e.Source as Control;
        var isTextInput = source is TextBox || source?.FindAncestorOfType<TextBox>() is not null;
        if (e.Key == Key.Enter && (ReferenceEquals(source, SearchBox) || source?.FindAncestorOfType<TextBox>() == SearchBox))
        {
            if (_composition is not { IsComposing: true } && _composition is not { HasRecentComposition: true })
            {
                vm.NavigateSearchCommand.Execute(null);
                await vm.OnlineSearch.SearchNowAsync();
                e.Handled = true;
            }
            return;
        }
        var isSlider = source is Slider || source?.FindAncestorOfType<Slider>() is not null;
        var isListOrMenu = source is ListBoxItem or ComboBox or MenuItem || source?.FindAncestorOfType<ListBox>() is not null;
        if (isTextInput || isSlider || isListOrMenu || e.Handled) return;

        if (e.Key == Key.Space)
        {
            if (vm.TogglePlayCommand.CanExecute(null)) vm.TogglePlayCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key is Key.Left or Key.Right)
        {
            var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 15d : 5d;
            await vm.SeekRelativeAsync(e.Key == Key.Left ? -step : step);
            e.Handled = true;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (Lifetime?.TryHideOnClose(e) == true) return;
        if (!_shutdownCompleted && DataContext is MainWindowViewModel vm)
        {
            e.Cancel = true;
            if (_shutdownStarted) return;
            _shutdownStarted = true;
            _layoutTimer.Stop();
            try { await vm.SaveStateAsync(Width, Height); }
            catch (Exception ex) { Log.Warning(ex, "窗口关闭清理失败"); }
            finally
            {
                _composition?.Dispose();
                if (_observedViewModel is not null) _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
                _shutdownCompleted = true;
                Close();
            }
            return;
        }
        base.OnClosing(e);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _observedViewModel.AccountsRequested -= OnAccountsRequested;
            _observedViewModel.LocateCurrentQueueRequested -= OnLocateCurrentQueueRequested;
        }
        _observedViewModel = DataContext as MainWindowViewModel;
        if (_observedViewModel is not null)
        {
            _observedViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _observedViewModel.AccountsRequested += OnAccountsRequested;
            _observedViewModel.LocateCurrentQueueRequested += OnLocateCurrentQueueRequested;
        }
    }
    private void OnLocateCurrentQueueRequested(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        var current = vm.QueueTracks.FirstOrDefault(x => x.IsCurrent);
        if (current is not null) { QueueList.SelectedItem = current; QueueList.ScrollIntoView(current); }
    }
    private void OnAccountsRequested(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_shutdownStarted) SettingsScroll.Offset = default;
    }, DispatcherPriority.Loaded);
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.IsTrackPickerVisible) or nameof(MainWindowViewModel.IsPlaylistPickerVisible)
            or nameof(MainWindowViewModel.LibraryRemovalConfirmationVisible) or nameof(MainWindowViewModel.RecentClearConfirmationVisible)
            or nameof(MainWindowViewModel.DeleteConfirmationVisible))
            Dispatcher.UIThread.Post(UpdateModalFocus);
    }
    private void UpdateModalFocus()
    {
        if (_observedViewModel is not { } vm || _shutdownStarted) return;
        var layer = vm.IsTrackPickerVisible ? this.FindControl<Control>("TrackPickerLayer")
            : vm.IsPlaylistPickerVisible ? this.FindControl<Control>("PlaylistPickerLayer")
            : vm.LibraryRemovalConfirmationVisible ? this.FindControl<Control>("LibraryRemovalDialog")
            : vm.RecentClearConfirmationVisible ? this.FindControl<Control>("RecentClearDialog")
            : vm.DeleteConfirmationVisible ? this.FindControl<Control>("PlaylistDeleteDialog") : null;
        if (ReferenceEquals(layer, _activeModal)) return;
        if (layer is null)
        {
            _activeModal = null;
            _restoreFocus?.Focus();
            _restoreFocus = null;
            return;
        }
        if (_activeModal is null) _restoreFocus = FocusManager?.GetFocusedElement();
        _activeModal = layer;
        var focus = layer.GetVisualDescendants().OfType<Button>().FirstOrDefault(x => Equals(x.Content, "取消"))
            ?? layer.GetVisualDescendants().OfType<Control>().FirstOrDefault(x => x.Focusable && x.IsEnabled && x.IsVisible);
        focus?.Focus();
    }
}
