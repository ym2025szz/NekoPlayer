using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace NekoPlayer.App.Services;

/// <summary>Coalesces producer frames into one pending UI callback and a fixed-size latest snapshot.</summary>
public sealed class LatestSpectrumDispatcher : IDisposable
{
    private readonly object _gate = new();
    private readonly Action<IReadOnlyList<float>> _apply;
    private readonly Action<Action> _post;
    private readonly float[] _latest;
    private readonly float[] _delivery;
    private bool _pending;
    private bool _hasSnapshot;
    private bool _disposed;

    public LatestSpectrumDispatcher(Action<IReadOnlyList<float>> apply, Action<Action>? post = null, int bandCount = 40)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (bandCount <= 0) throw new ArgumentOutOfRangeException(nameof(bandCount));
        _apply = apply;
        _post = post ?? (callback => Dispatcher.UIThread.Post(callback, DispatcherPriority.Background));
        _latest = new float[bandCount];
        _delivery = new float[bandCount];
    }

    public void Submit(IReadOnlyList<float> bands)
    {
        ArgumentNullException.ThrowIfNull(bands);
        var schedule = false;
        lock (_gate)
        {
            if (_disposed) return;
            for (var i = 0; i < _latest.Length; i++)
                _latest[i] = i < bands.Count && float.IsFinite(bands[i]) ? Math.Clamp(bands[i], 0, 1) : 0;
            _hasSnapshot = true;
            if (!_pending) { _pending = true; schedule = true; }
        }
        if (schedule) PostCallback();
    }

    public void Dispose()
    {
        lock (_gate) { _disposed = true; _hasSnapshot = false; }
    }

    private void PostCallback()
    {
        try { _post(DeliverLatest); }
        catch
        {
            lock (_gate) _pending = false;
            throw;
        }
    }

    private void DeliverLatest()
    {
        lock (_gate)
        {
            if (_disposed) { _pending = false; return; }
            Array.Copy(_latest, _delivery, _latest.Length);
            _hasSnapshot = false;
        }
        try { _apply(_delivery); }
        finally
        {
            var schedule = false;
            lock (_gate)
            {
                if (!_disposed && _hasSnapshot) schedule = true;
                else _pending = false;
            }
            if (schedule) PostCallback();
        }
    }
}

/// <summary>A permanent spectrum item; height updates do not replace collection items.</summary>
public sealed class SpectrumBarViewModel : ObservableObject
{
    private double _height = 3;
    public double Height { get => _height; set => SetProperty(ref _height, value); }
    public void SetAmplitude(float value) => Height = 3 + (float.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0) * 92;
}
