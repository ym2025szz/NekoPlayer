using NekoPlayer.Core.Models;
using NekoPlayer.Core.Interfaces;

namespace NekoPlayer.Core.Services;

public sealed class TrackStateStore : ITrackStateStore
{
    public event EventHandler<FavoriteStateChanged>? FavoriteChanged;

    public void PublishFavoriteChanged(Guid trackId, bool favorite) =>
        FavoriteChanged?.Invoke(this, new FavoriteStateChanged(trackId, favorite, DateTime.UtcNow));
}
