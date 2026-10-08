using System.Threading.Channels;

namespace NekoPlayer.Audio.Decoding;

internal sealed class BoundedPcmStream : Stream
{
    private const int MaximumBlockBytes = 32 * 1024;
    private readonly Channel<byte[]> _channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(12)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true
    });
    private readonly CancellationToken _cancellationToken;

    public BoundedPcmStream(CancellationToken cancellationToken) => _cancellationToken = cancellationToken;
    public ChannelReader<byte[]> Reader => _channel.Reader;
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count), _cancellationToken).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_cancellationToken, cancellationToken);
        while (!buffer.IsEmpty)
        {
            var count = Math.Min(buffer.Length, MaximumBlockBytes);
            await _channel.Writer.WriteAsync(buffer[..count].ToArray(), linked.Token);
            buffer = buffer[count..];
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _channel.Writer.TryComplete();
        base.Dispose(disposing);
    }

    public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
