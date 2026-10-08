using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace NekoPlayer.App.Services;

/// <summary>Observe actual IME preedit rather than treating input focus as composition.</summary>
public sealed class ImeCompositionMonitor : IDisposable
{
    private readonly TextBox _textBox;
    private readonly Action<bool> _changed;
    private TextPresenter? _presenter;
    private long _compositionEnded;
    private bool _disposed;
    public bool IsComposing { get; private set; }
    public bool HasRecentComposition => _compositionEnded != 0 && Stopwatch.GetElapsedTime(_compositionEnded) < TimeSpan.FromMilliseconds(150);

    private ImeCompositionMonitor(TextBox textBox, Action<bool> changed)
    {
        _textBox = textBox;
        _changed = changed;
        textBox.TemplateApplied += OnTemplateApplied;
        textBox.AttachedToVisualTree += OnAttached;
        AttachPresenter();
    }

    public static ImeCompositionMonitor Attach(TextBox textBox, Action<bool> changed) => new(textBox, changed);
    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => AttachPresenter();
    private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e) => AttachPresenter(e.NameScope.Find<TextPresenter>("PART_TextPresenter"));
    private void AttachPresenter(TextPresenter? presenter = null)
    {
        presenter ??= _textBox.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        if (ReferenceEquals(presenter, _presenter)) return;
        if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterChanged;
        _presenter = presenter;
        if (_presenter is not null) _presenter.PropertyChanged += OnPresenterChanged;
        Update();
    }
    private void OnPresenterChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == TextPresenter.PreeditTextProperty) Update();
    }
    private void Update()
    {
        var active = !string.IsNullOrEmpty(_presenter?.PreeditText);
        if (IsComposing == active || _disposed) return;
        IsComposing = active;
        if (!active) _compositionEnded = Stopwatch.GetTimestamp();
        _changed(active);
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _textBox.TemplateApplied -= OnTemplateApplied;
        _textBox.AttachedToVisualTree -= OnAttached;
        if (_presenter is not null) _presenter.PropertyChanged -= OnPresenterChanged;
    }
}
