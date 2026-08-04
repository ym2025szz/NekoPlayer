using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Runtime.CompilerServices;

namespace NekoPlayer.Core.Models;

public sealed class Track : INotifyPropertyChanged
{
    private string _filePath = string.Empty;
    private bool _isFavorite;

    public Guid Id { get; set; } = Guid.NewGuid();
    public string FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value) return;
            _filePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FileExists));
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

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RefreshFileAvailability() => OnPropertyChanged(nameof(FileExists));

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
