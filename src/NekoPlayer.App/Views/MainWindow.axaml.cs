using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NekoPlayer.App.Controls;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Views;

public partial class MainWindow : Window
{
    private bool _shutdownCompleted;
    private readonly DispatcherTimer _layoutTimer;

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
    }

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
        await vm.InitializeAsync();
        Width = Math.Max(MinWidth, vm.WindowWidth);
        Height = Math.Max(MinHeight, vm.WindowHeight);
        vm.UpdateLayout(Bounds.Width);
    }

    private async void OnAddFilesClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel vm) return;
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
        var isSlider = source is Slider || source?.FindAncestorOfType<Slider>() is not null;
        if (isTextInput || isSlider) return;

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
        if (!_shutdownCompleted && DataContext is MainWindowViewModel vm)
        {
            e.Cancel = true;
            _layoutTimer.Stop();
            try { await vm.SaveStateAsync(Width, Height); }
            finally { _shutdownCompleted = true; Close(); }
            return;
        }
        base.OnClosing(e);
    }
}
