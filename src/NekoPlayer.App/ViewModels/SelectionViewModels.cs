using CommunityToolkit.Mvvm.ComponentModel;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.ViewModels;

public partial class TrackPickerItemViewModel(Track track, bool isAlreadyAdded) : ObservableObject
{
    public Track Track { get; } = track;
    public bool IsAlreadyAdded { get; } = isAlreadyAdded;
    public bool CanSelect => !IsAlreadyAdded;
    [ObservableProperty] private bool isSelected;
}

public partial class PlaylistChoiceViewModel(Playlist playlist, bool alreadyContainsAll) : ObservableObject
{
    public Playlist Playlist { get; } = playlist;
    public bool AlreadyContainsAll { get; } = alreadyContainsAll;
    [ObservableProperty] private bool isSelected;
}

public sealed class QueueItemViewModel(int displayIndex, Track track, bool isCurrent)
{
    public int DisplayIndex { get; } = displayIndex;
    public Track Track { get; } = track;
    public bool IsCurrent { get; } = isCurrent;
    public string PlayAutomationName => $"立即播放：{Track.Title}";
}
