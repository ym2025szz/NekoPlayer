using System.Collections.ObjectModel;
using NekoPlayer.App.ViewModels;
using NekoPlayer.Core.Models;

namespace NekoPlayer.App.Services;

public static class QueuePresentation
{
    // Keep row/control identities stable so progress ticks, highlights and inserts do not reload covers.
    public static void Synchronize(ObservableCollection<QueueItemViewModel> rows, IReadOnlyList<Track> tracks, Guid? currentId)
    {
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var existing = -1;
            for (var j = i; j < rows.Count; j++)
                if (rows[j].Track.Id == track.Id) { existing = j; break; }
            if (existing < 0) rows.Insert(i, new QueueItemViewModel(i + 1, track, track.Id == currentId));
            else if (existing != i) rows.Move(existing, i);
            rows[i].DisplayIndex = i + 1;
            rows[i].Track = track;
            rows[i].IsCurrent = track.Id == currentId;
        }
        while (rows.Count > tracks.Count) rows.RemoveAt(rows.Count - 1);
    }
}
