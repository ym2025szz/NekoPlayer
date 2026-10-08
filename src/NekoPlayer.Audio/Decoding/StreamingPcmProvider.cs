using NAudio.Wave;

namespace NekoPlayer.Audio.Decoding;

/// <summary>Starvation returns zero and drains the device; only true EOF permits a short read.</summary>
internal sealed class StreamingPcmProvider : IWaveProvider
{
    private readonly BufferedWaveProvider _buffer;
    private readonly object _sync = new();
    private long _submittedMediaBytes;
    private bool _complete;

    public StreamingPcmProvider(WaveFormat format, int capacityBytes)
    {
        _buffer = new BufferedWaveProvider(format)
        {
            BufferLength = capacityBytes,
            DiscardOnBufferOverflow = false,
            ReadFully = false
        };
    }
    public WaveFormat WaveFormat => _buffer.WaveFormat;
    public int BufferedBytes { get { lock (_sync) return _buffer.BufferedBytes; } }
    public int BufferLength => _buffer.BufferLength;
    public long SubmittedMediaBytes => Interlocked.Read(ref _submittedMediaBytes);
    public bool TryAddSamples(byte[] block)
    {
        lock (_sync)
        {
            if (_buffer.BufferedBytes + block.Length > _buffer.BufferLength) return false;
            _buffer.AddSamples(block, 0, block.Length);
            return true;
        }
    }
    public void Complete() { lock (_sync) _complete = true; }
    public void ClearBuffer() { lock (_sync) _buffer.ClearBuffer(); }
    public int Read(byte[] buffer, int offset, int count)
    {
        lock (_sync)
        {
            if (!_complete && _buffer.BufferedBytes < count) return 0;
            var read = _buffer.Read(buffer, offset, count);
            Interlocked.Add(ref _submittedMediaBytes, read);
            return read;
        }
    }
    public long GetMediaBytesPlayed(long deviceBytes) => Math.Clamp(deviceBytes, 0, SubmittedMediaBytes);
}
