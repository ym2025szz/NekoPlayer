using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NekoPlayer.App.ViewModels;
using NekoPlayer.App.Services;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Views;

public partial class OnlineSearchView : UserControl
{
    private OnlineSearchViewModel? _viewModel;
    private ProviderSearchGroupViewModel? _displayedGroup;
    private ScrollViewer? _resultScroll;
    private bool _restoring;
    private ImeCompositionMonitor? _compositionMonitor;
    private int _restoreGeneration;

    public OnlineSearchView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
        SearchResults.LayoutUpdated += OnResultsLayoutUpdated;
        OnlineQueryBox.AddHandler(KeyDownEvent, OnQueryKeyDown, RoutingStrategies.Tunnel);
        PropertyChanged += (_, change) =>
        {
            if (change.Property != IsVisibleProperty) return;
            if (IsVisible) QueueRestore(); else SaveScrollPosition();
        };
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        SaveScrollPosition();
        if (_viewModel is not null) _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel = DataContext as OnlineSearchViewModel;
        _displayedGroup = _viewModel?.SelectedGroup;
        if (_viewModel is not null) _viewModel.PropertyChanged += OnViewModelChanged;
        QueueRestore();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(OnlineSearchViewModel.SelectedGroup)) return;
        _displayedGroup = _viewModel?.SelectedGroup;
        QueueRestore();
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _compositionMonitor ??= ImeCompositionMonitor.Attach(OnlineQueryBox, value => _viewModel?.SetCompositionActive(value));
        QueueRestore();
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        SaveScrollPosition();
        if (_resultScroll is not null) _resultScroll.ScrollChanged -= OnScrollChanged;
        _resultScroll = null;
        _compositionMonitor?.Dispose();
        _compositionMonitor = null;
        _viewModel?.SetCompositionActive(false);
    }

    private void OnResultsLayoutUpdated(object? sender, EventArgs e)
    {
        if (_resultScroll is not null) return;
        _resultScroll = SearchResults.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (_resultScroll is null) return;
        _resultScroll.ScrollChanged += OnScrollChanged;
        QueueRestore();
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (!_restoring && IsVisible && e.OffsetDelta.Y != 0 && ReferenceEquals(_viewModel?.SelectedGroup, _displayedGroup)) SaveScrollPosition();
    }

    private void SaveScrollPosition()
    {
        if (_displayedGroup is not null && _resultScroll is not null && !_restoring)
            _displayedGroup.ScrollOffset = _resultScroll.Offset.Y;
    }

    private void QueueRestore()
    {
        var generation = ++_restoreGeneration;
        _restoring = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _restoreGeneration) return;
            if (_resultScroll is not null && _displayedGroup is not null)
                _resultScroll.Offset = new Vector(0, Math.Max(0, _displayedGroup.ScrollOffset));
            _restoring = false;
        }, DispatcherPriority.Loaded);
    }

    private void OnQueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _compositionMonitor is { IsComposing: true } || _compositionMonitor is { HasRecentComposition: true } || _viewModel is null) return;
        if (_viewModel.SearchCommand.CanExecute(null)) _viewModel.SearchCommand.Execute(null);
        e.Handled = true;
    }

    private void OnResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        // Button double taps must not also invoke a row's full-play action.
        if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<Button>().Any() == true) return;
        if (SearchResults.SelectedItem is Track { CanAttemptPlayback: true } track && _viewModel is not null && _viewModel.PlayCommand.CanExecute(track))
            _viewModel.PlayCommand.Execute(track);
    }

}
