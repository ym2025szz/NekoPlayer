using NekoPlayer.Core.Models;
using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class TrackUtilitiesTests
{
    [Theory]
    [InlineData("a.mp3", true)] [InlineData("a.FLAC", true)] [InlineData("a.opus", true)]
    [InlineData("a.lrc", false)] [InlineData("a.png", false)] [InlineData("a.txt", false)] [InlineData("a.mp4", false)]
    public void FiltersAudioExtensions(string path, bool expected) => Assert.Equal(expected, AudioFileExtensions.IsSupported(path));

    [Fact]
    public void NormalizesWindowsPathsAndTrimsQuotes()
    {
        var relative = Path.Combine("folder", "song.mp3");
        Assert.Equal(Path.GetFullPath(relative), WindowsPath.NormalizeFile($"\"{relative}\""));
    }

    [Fact]
    public void MetadataFallbacksAreFriendly()
    {
        Assert.Equal("song", MetadataFallback.Title("", "c:/music/song.mp3"));
        Assert.Equal("未知艺术家", MetadataFallback.Artist(null));
        Assert.Equal("未知专辑", MetadataFallback.Album("  "));
    }

    [Theory]
    [InlineData(65, "1:05")] [InlineData(3661, "1:01:01")]
    public void FormatsTime(double seconds, string expected) => Assert.Equal(expected, TimeFormatter.Format(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("hello", true)] [InlineData("ARTIST", true)] [InlineData("album", true)] [InlineData("missing", false)] [InlineData("", true)]
    public void SearchesTitleArtistAndAlbum(string query, bool expected)
    {
        var track = new Track { Title = "Hello", Artist = "Artist", Album = "Album" };
        Assert.Equal(expected, TrackSearch.Matches(track, query));
    }

    [Fact]
    public void SearchCanHideNewTrackWithoutClearingQuery()
    {
        var track = new Track { Title = "Visible song", Artist = "Artist", Album = "Album" };
        Assert.False(TrackSearch.Matches(track, "not-a-match"));
    }
}
