using NAudio.Dsp;
using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Audio.Spectrum;

public sealed class SpectrumService : ISpectrumService
{
    private const int FftSize = 1024;
    private readonly float[] _bands = new float[40];
    private int _busy;
    private long _lastUpdate;

    public IReadOnlyList<float> Bands => _bands;
    public bool IsEnabled { get; set; } = true;
    public int FramesPerSecond { get; set; } = 30;
    public event EventHandler<IReadOnlyList<float>>? SpectrumUpdated;

    public void PushPcm(ReadOnlySpan<byte> pcm)
    {
        if (!IsEnabled || pcm.Length < FftSize * 8 || Interlocked.Exchange(ref _busy, 1) != 0) return;
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastUpdate) < 1000 / Math.Clamp(FramesPerSecond, 10, 60)) { Volatile.Write(ref _busy, 0); return; }
        Interlocked.Exchange(ref _lastUpdate, now);
        var copy = pcm[..(FftSize * 8)].ToArray();
        _ = Task.Run(() => Calculate(copy)).ContinueWith(_ => Volatile.Write(ref _busy, 0), TaskScheduler.Default);
    }

    private void Calculate(byte[] pcm)
    {
        try
        {
            var fft = new Complex[FftSize];
            for (var i = 0; i < FftSize; i++)
            {
                var left = BitConverter.ToSingle(pcm, i * 8);
                var right = BitConverter.ToSingle(pcm, i * 8 + 4);
                fft[i].X = (left + right) * 0.5f * (float)FastFourierTransform.HannWindow(i, FftSize);
            }
            FastFourierTransform.FFT(true, 10, fft);

            var next = new float[_bands.Length];
            for (var band = 0; band < next.Length; band++)
            {
                var start = Math.Max(1, (int)Math.Pow(FftSize / 2d, band / (double)next.Length));
                var end = Math.Max(start + 1, (int)Math.Pow(FftSize / 2d, (band + 1d) / next.Length));
                var peak = 0f;
                for (var i = start; i < Math.Min(end, FftSize / 2); i++)
                    peak = Math.Max(peak, MathF.Sqrt(fft[i].X * fft[i].X + fft[i].Y * fft[i].Y));
                next[band] = Math.Clamp(MathF.Log10(1 + peak * 40), 0, 1);
            }

            for (var i = 0; i < _bands.Length; i++) _bands[i] = Math.Max(next[i], _bands[i] * 0.78f);
            SpectrumUpdated?.Invoke(this, _bands.ToArray());
        }
        catch
        {
            // Spectrum visualization is intentionally isolated from playback failures.
        }
    }
}
