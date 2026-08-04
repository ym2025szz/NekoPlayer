using NekoPlayer.Core.Enums;

namespace NekoPlayer.Core.Interfaces;

public interface IAudioPlayerService : IAsyncDisposable
{
    PlaybackState State { get; }
    TimeSpan Position { get; }
    TimeSpan Duration { get; }
    float Volume { get; set; }
    bool IsMuted { get; set; }
    Task LoadAsync(string filePath, TimeSpan? startPosition = null, CancellationToken cancellationToken = default);
    Task PlayAsync(CancellationToken cancellationToken = default);
    Task PauseAsync();
    Task StopAsync();
    Task UnloadAsync();
    Task SeekAsync(TimeSpan position, CancellationToken cancellationToken = default);
    event EventHandler<PlaybackState>? StateChanged;
    event EventHandler<TimeSpan>? PositionChanged;
    event EventHandler? PlaybackCompleted;
    event EventHandler<Exception>? PlaybackFailed;
}

public interface IAudioPlaybackDiagnostics
{
    int BufferedBytes { get; }
    int BufferCapacityBytes { get; }
    long TotalPcmBytesReceived { get; }
    bool IsOutputInitialized { get; }
    bool IsDecodeActive { get; }
    string? CurrentFilePath { get; }
}
