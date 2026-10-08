using Microsoft.EntityFrameworkCore;
using NekoPlayer.Core.Models;

namespace NekoPlayer.Infrastructure.Data;

public sealed class NekoPlayerDbContext(DbContextOptions<NekoPlayerDbContext> options) : DbContext(options)
{
    public DbSet<Track> Tracks => Set<Track>();
    public DbSet<Playlist> Playlists => Set<Playlist>();
    public DbSet<PlaylistTrack> PlaylistTracks => Set<PlaylistTrack>();
    public DbSet<PlaybackHistory> PlaybackHistories => Set<PlaybackHistory>();
    public DbSet<LibraryFolder> LibraryFolders => Set<LibraryFolder>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Track>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.FilePath).IsUnique().HasFilter("\"SourceKind\" = 0");
            entity.HasIndex(x => new { x.ProviderId, x.ProviderTrackId }).IsUnique()
                .HasFilter("\"SourceKind\" = 1");
            entity.Property(x => x.FilePath).UseCollation("NOCASE");
            entity.Property(x => x.ProviderId).UseCollation("NOCASE");
            entity.Property(x => x.SourceKind).HasDefaultValue(TrackSourceKind.Local);
            entity.Property(x => x.ProviderMetadataJson).HasDefaultValue("{}");
            entity.Property(x => x.VersionLabel).HasDefaultValue(string.Empty);
            entity.Property(x => x.Duration).HasConversion(x => x.Ticks, x => TimeSpan.FromTicks(x));
        });

        modelBuilder.Entity<PlaylistTrack>(entity =>
        {
            entity.HasKey(x => new { x.PlaylistId, x.TrackId });
            entity.HasOne(x => x.Playlist).WithMany(x => x.Tracks).HasForeignKey(x => x.PlaylistId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.PlaylistId, x.SortOrder });
        });

        modelBuilder.Entity<PlaybackHistory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.LastPosition).HasConversion(x => x.Ticks, x => TimeSpan.FromTicks(x));
            entity.HasOne(x => x.Track).WithMany().HasForeignKey(x => x.TrackId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => x.PlayedAt);
        });

        modelBuilder.Entity<Playlist>().HasKey(x => x.Id);
        modelBuilder.Entity<LibraryFolder>().HasKey(x => x.Id);
    }
}
