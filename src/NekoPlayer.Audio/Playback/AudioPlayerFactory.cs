using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Audio.Playback;

public static class AudioPlayerFactory
{
    public static IAudioPlayerService Create(IFfmpegLocator ffmpeg, ISpectrumService spectrum) =>
        CreateForPlatform(OperatingSystem.IsWindows(), ffmpeg, spectrum);

    public static IAudioPlayerService CreateForPlatform(bool isWindows, IFfmpegLocator ffmpeg, ISpectrumService spectrum) =>
        isWindows
            ? new FfmpegAudioPlayerService(ffmpeg, spectrum)
            : new LinuxFfmpegAudioPlayerService(ffmpeg);
}
