using Jellyfin.Plugin.TorrCatalog.Configuration;
using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class PersistenceTests
{
    [Fact]
    public void MoviePathFollowsJellyfinNaming()
    {
        Assert.Equal(
            Path.Combine("Дюна Часть вторая (2024) [tmdbid-693134]", "Дюна Часть вторая (2024).strm"),
            Persistence.MoviePath("Дюна: Часть вторая", 2024, 693134));
        Assert.Equal(Path.Combine("Untitled [tmdbid-1]", "Untitled.strm"), Persistence.MoviePath("Untitled", null, 1));
    }

    [Fact]
    public void EpisodePathHasSeriesAndSeasonFolders()
    {
        Assert.Equal(
            Path.Combine("Дюна Пророчество (2024) [tmdbid-90228]", "Season 01", "Дюна Пророчество S01E04.strm"),
            Persistence.EpisodePath("Дюна: Пророчество", 2024, 90228, 1, 4));
    }

    [Theory]
    [InlineData("A/B\\C", "A B C")]
    [InlineData("What? Why*", "What Why")]
    [InlineData("..Hidden..", "Hidden")]
    public void CleanRemovesPathTricks(string raw, string clean) => Assert.Equal(clean, Persistence.Clean(raw));

    [Fact]
    public void StreamKeyMustMatchExactly()
    {
        var config = new PluginConfiguration { StreamKey = "s3cret" };
        var persistence = new Persistence(null!, null!, null!, null!, null!) { Config = () => config };

        Assert.True(persistence.KeyMatches("s3cret"));
        Assert.False(persistence.KeyMatches("s3cre"));
        Assert.False(persistence.KeyMatches(null));
        Assert.False(new Persistence(null!, null!, null!, null!, null!) { Config = () => new PluginConfiguration() }.KeyMatches(string.Empty));
    }
}
