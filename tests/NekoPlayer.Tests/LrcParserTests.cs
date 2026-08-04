using NekoPlayer.Core.Services;

namespace NekoPlayer.Tests;

public sealed class LrcParserTests
{
    private readonly LrcParser _parser = new();

    [Fact]
    public void ParsesStandardAndMillisecondTimestamps()
    {
        var lines = _parser.Parse("[00:01]第一行\n[01:02.345]第二行");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(2.345), lines[1].Timestamp);
    }

    [Fact]
    public void ExpandsMultipleTagsAndSorts()
    {
        var lines = _parser.Parse("[00:08][00:03]重复\n[ti:标题]\n无效行");
        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), lines[0].Timestamp);
        Assert.All(lines, x => Assert.Equal("重复", x.Text));
    }

    [Fact]
    public void AppliesPositiveAndNegativeOffsets()
    {
        Assert.Equal(TimeSpan.FromSeconds(1.5), _parser.Parse("[offset:+500]\n[00:01]A").Single().Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(0.5), _parser.Parse("[offset:-500]\n[00:01]A").Single().Timestamp);
    }

    [Fact]
    public void IgnoresMetadataEmptyAndInvalidLines()
    {
        var lines = _parser.Parse("[ti:歌名]\n[ar:歌手]\n\nhello\n[xx:yy]");
        Assert.Empty(lines);
    }
}
