using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NekoPlayer.App.ViewModels;

namespace NekoPlayer.App.Views;
public partial class ProviderAccountsView : UserControl
{
    private ProviderAccountsViewModel? _viewModel;
    private bool _subscribed;
    private int _focusGeneration;

    public ProviderAccountsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => { Unsubscribe(); _viewModel = DataContext as ProviderAccountsViewModel; Subscribe(); QueueFocus(); };
        AttachedToVisualTree += (_, _) => { Subscribe(); QueueFocus(); };
        DetachedFromVisualTree += (_, _) => Unsubscribe();
        PropertyChanged += (_, change) => { if (change.Property == IsVisibleProperty && IsVisible) QueueFocus(); };
    }

    private void Subscribe()
    {
        if (_subscribed || _viewModel is null) return;
        _subscribed = true;
        _viewModel.PropertyChanged += OnViewModelChanged;
        _viewModel.Rows.CollectionChanged += OnRowsChanged;
    }

    private void Unsubscribe()
    {
        ++_focusGeneration;
        if (!_subscribed || _viewModel is null) return;
        _subscribed = false;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        _viewModel.Rows.CollectionChanged -= OnRowsChanged;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(ProviderAccountsViewModel.SelectedRow) or nameof(ProviderAccountsViewModel.SelectedProviderId)) QueueFocus();
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs args) => QueueFocus();

    private void QueueFocus()
    {
        var generation = ++_focusGeneration;
        Dispatcher.UIThread.Post(() =>
        {
            if (generation != _focusGeneration || !IsVisible || _viewModel?.SelectedRow is not { } selected) return;
            AccountRows.GetVisualDescendants().OfType<Border>()
                .FirstOrDefault(row => row.Classes.Contains("accountRow") && ReferenceEquals(row.DataContext, selected))?.BringIntoView();
        }, DispatcherPriority.Loaded);
    }
}
