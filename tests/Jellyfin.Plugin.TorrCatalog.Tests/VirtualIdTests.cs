using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class VirtualIdTests
{
    [Fact]
    public void MovieRoundTrips()
    {
        Assert.True(VirtualId.TryParse(VirtualId.Movie(693134).ToGuid(), out var id));
        Assert.Equal(VirtualId.Movie(693134), id);
    }

    [Fact]
    public void SameInputGivesSameGuid()
    {
        Assert.Equal(VirtualId.Movie(1).ToGuid(), VirtualId.Movie(1).ToGuid());
        Assert.Equal(VirtualId.Section("trending").ToGuid(), VirtualId.Section("trending").ToGuid());
    }

    [Fact]
    public void DifferentInputsGiveDifferentGuids()
    {
        Assert.NotEqual(VirtualId.Movie(1).ToGuid(), VirtualId.Movie(2).ToGuid());
        Assert.NotEqual(VirtualId.Section("trending").ToGuid(), VirtualId.Section("popular").ToGuid());
        Assert.NotEqual(VirtualId.Movie(1).ToGuid(), VirtualId.Series(1).ToGuid());
    }

    [Fact]
    public void RegularGuidsAreNotVirtual()
    {
        Assert.False(VirtualId.TryParse(Guid.NewGuid(), out _));
        Assert.False(VirtualId.TryParse(Guid.Empty, out _));
    }

    [Fact]
    public void EpisodesAndVersionsRoundTrip()
    {
        var episode = VirtualId.EpisodeOf(1399, 3, 9);
        var version = episode.WithRelease("a1b2c3d4e5f60718");

        Assert.True(VirtualId.TryParse(version.ToGuid(), out var parsed));
        Assert.Equal(version, parsed);
        Assert.Equal(episode, parsed.Base);
        Assert.True(parsed.IsRelease("a1b2c3ffffffffff"));
        Assert.False(parsed.IsRelease("000000ffffffffff"));
        Assert.NotEqual(episode.ToGuid(), version.ToGuid());
        Assert.NotEqual(VirtualId.SeasonOf(1399, 3).ToGuid(), VirtualId.EpisodeOf(1399, 3, 0).ToGuid());
    }

    [Fact]
    public void MediaSourceIdStringsParse()
    {
        var id = VirtualId.Movie(42).WithRelease("abcdef0011223344");

        Assert.True(VirtualId.TryParse(id.ToGuid().ToString("N"), out var parsed));
        Assert.Equal(id, parsed);
        Assert.False(VirtualId.TryParse("not-a-guid", out _));
        Assert.False(VirtualId.TryParse((string?)null, out _));
    }

    [Fact]
    public void ToStringIsCompactAndDoesNotRecurse()
    {
        // The generated record ToString recursed through Base and crashed Jellyfin with a stack overflow.
        Assert.Equal("Movie:42", VirtualId.Movie(42).ToString());
        Assert.Equal("Episode:1399:S03E09@a1b2c3", VirtualId.EpisodeOf(1399, 3, 9).WithRelease("a1b2c3d4e5f60718").ToString());
        Assert.Equal("Season:1399:S03", VirtualId.SeasonOf(1399, 3).ToString());
        Assert.StartsWith("Section:", VirtualId.Section("trending").ToString(), StringComparison.Ordinal);
    }
}
