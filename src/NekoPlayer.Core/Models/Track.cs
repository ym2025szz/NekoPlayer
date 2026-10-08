using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace NekoPlayer.Core.Models;

public sealed class Track : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private bool _isFavorite;
    private MusicAvailability _availability;
    private string? _restrictionReason;

    public Guid Id { get; set; } = Guid.NewGuid();
    public TrackSourceKind SourceKind { get; set; }
    public string? ProviderId { get; set; }
    public string? ProviderTrackId { get; set; }
    public string ProviderMetadataJson { get; set; } = "{}";
    public string? CoverUrl { get; set; }
    public string VersionLabel { get; set; } = string.Empty;
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value) return;
            _filePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileExists));
            OnPropertyChanged(nameof(CanAttemptPlayback));
        }
    }
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string Album { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public TimeSpan Duration { get; set; }
    public int TrackNumber { get; set; }
    public int Year { get; set; }
    public int SampleRate { get; set; }
    public int Channels { get; set; }
    public long BitRate { get; set; }
    public string CodecName { get; set; } = string.Empty;
    public string? CoverCachePath { get; set; }
    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            OnPropertyChanged();
        }
    }
    public int PlayCount { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedAt { get; set; }
    public long FileSize { get; set; }
    public DateTime FileLastWriteTimeUtc { get; set; }

    [NotMapped]
    public bool FileExists => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    [NotMapped] public bool IsOnline => SourceKind == TrackSourceKind.Online;
    [NotMapped] public bool IsLocal => !IsOnline;
    [NotMapped] public MusicAvailability Availability
    {
        get => _availability;
        set { if (_availability == value) return; _availability = value; NotifyAvailability(); }
    }
    [NotMapped] public string? RestrictionReason
    {
        get => _restrictionReason;
        set { if (_restrictionReason == value) return; _restrictionReason = value; NotifyAvailability(); }
    }
    [NotMapped] public bool CanAttemptPlayback => IsOnline
        ? Availability is MusicAvailability.Unknown or MusicAvailability.Full : FileExists;
    [NotMapped] public bool CanPreview => IsOnline && Availability == MusicAvailability.Preview;
    [NotMapped] public string ProviderName => ProviderId switch
    { "netease" => "网易云", "qq" => "QQ 音乐", "kuwo" => "酷我", "kugou" => "酷狗", "qishui" => "汽水", _ => "本地" };
    [NotMapped] public string AutomationContext => $"《{Title}》，{Artist}，{ProviderName}";
    [NotMapped] public string SourceVersionText => string.IsNullOrWhiteSpace(VersionLabel) ? ProviderName : $"{ProviderName} · {VersionLabel}";
    [NotMapped] public string AvailabilityText => IsLocal ? (FileExists ? "本地音乐" : "文件不存在") : Availability switch
    { MusicAvailability.Full => "可完整播放", MusicAvailability.Preview => "仅试听", MusicAvailability.Unavailable => RestrictionReason ?? "当前不可播放", _ => "播放权限待确认" };
    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(Availability), nameof(RestrictionReason), nameof(CanAttemptPlayback), nameof(CanPreview), nameof(AvailabilityText) })
            OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFileAvailability()
    {
        OnPropertyChanged(nameof(FileExists));
        OnPropertyChanged(nameof(CanAttemptPlayback));
        OnPropertyChanged(nameof(AvailabilityText));
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
