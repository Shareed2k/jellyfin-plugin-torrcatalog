using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class ReleaseTests
{
    static readonly Dictionary<string, System.Text.RegularExpressions.Regex> Langs =
        ReleaseParser.LanguageMatchers(new PluginConfiguration().LanguageAliases);

    static readonly ReleaseScorer.Preferences Prefs = ReleaseScorer.Preferences.From(new PluginConfiguration());

    static ParsedRelease P(string title) => ReleaseParser.Parse(title, Langs);

    static double S(string title, int seeders = 50, int? year = 2026, ReleaseScorer.Preferences? prefs = null) =>
        ReleaseScorer.Score(P(title), seeders, year, prefs ?? Prefs).Score;

    [Fact]
    public void ParsesFull4kTitle()
    {
        var r = P("Movie.2026.2160p.WEB-DL.DV.HDR.HEVC.TrueHD.Atmos-RUS");

        Assert.Equal(2160, r.Resolution);
        Assert.Equal(ReleaseSource.WebDl, r.Source);
        Assert.Equal(VideoCodec.Hevc, r.Codec);
        Assert.Equal(HdrType.DolbyVision, r.Hdr);
        Assert.Equal(AudioCodec.TrueHd, r.Audio);
        Assert.True(r.Atmos);
        Assert.Equal(["ru"], r.Languages);
        Assert.Equal([2026], r.Years);
        Assert.Null(r.Episode);
    }

    [Theory]
    [InlineData("Movie.2026.1080p.WEBRip.x264", 1080, ReleaseSource.WebRip, VideoCodec.H264)]
    [InlineData("Movie.2026.1080p.WEB-Rip.x264", 1080, ReleaseSource.WebRip, VideoCodec.H264)]
    [InlineData("Movie 2026 WEB-DLRip 720p", 720, ReleaseSource.WebRip, VideoCodec.Unknown)]
    [InlineData("Movie.2026.CAM.XViD", 0, ReleaseSource.Cam, VideoCodec.Xvid)]
    [InlineData("Вышка 2 / Fall 2: Deadpoint (2026) TSRip [H.264] [AD]", 0, ReleaseSource.Telesync, VideoCodec.H264)]
    [InlineData("Movie (2026) TS-Rip 720p", 720, ReleaseSource.Telesync, VideoCodec.Unknown)]
    [InlineData("Movie (2026) CAM-Rip", 0, ReleaseSource.Cam, VideoCodec.Unknown)]
    [InlineData("Movie.2026.HDCAM.1080p.WEB.x264", 1080, ReleaseSource.Cam, VideoCodec.H264)]
    [InlineData("Movie.2026.1080p.BluRay.REMUX.AVC", 1080, ReleaseSource.Remux, VideoCodec.H264)]
    [InlineData("Movie.2026.DVDRip.XviD", 480, ReleaseSource.Dvd, VideoCodec.Xvid)]
    public void ParsesResolutionSourceCodec(string title, int resolution, ReleaseSource source, VideoCodec codec)
    {
        var r = P(title);

        Assert.Equal(resolution, r.Resolution);
        Assert.Equal(source, r.Source);
        Assert.Equal(codec, r.Codec);
    }

    [Theory]
    [InlineData("Movie.2026.1080p.WEB-DL.DDP5.1.H.264", AudioCodec.Eac3)]
    [InlineData("Movie 2026 1080p WEB-DL DD+5.1", AudioCodec.Eac3)]
    [InlineData("Movie.2026.1080p.BluRay.DTS-HD.MA.5.1", AudioCodec.DtsHdMa)]
    [InlineData("Movie.2026.2160p.BluRay.DTS-X", AudioCodec.DtsX)]
    [InlineData("Movie.2026.720p.WEB.AAC", AudioCodec.Aac)]
    public void ParsesAudio(string title, AudioCodec audio) => Assert.Equal(audio, P(title).Audio);

    [Fact]
    public void ParsesEpisode()
    {
        var r = P("Show.S01E04.2160p.WEB-DL");

        Assert.Equal([1], r.Seasons);
        Assert.Equal(4, r.Episode);
        Assert.Equal(2160, r.Resolution);
    }

    [Fact]
    public void ParsesRussianTrackerTitle()
    {
        var r = P("Дюна: Часть вторая / Dune: Part Two (2024) WEB-DL 2160p | 4K | HDR10 | Дубляж, Original Eng");

        Assert.Equal(2160, r.Resolution);
        Assert.Equal(ReleaseSource.WebDl, r.Source);
        Assert.Equal(HdrType.Hdr10, r.Hdr);
        Assert.Contains("ru", r.Languages);
        Assert.Contains("en", r.Languages);
        Assert.Equal([2024], r.Years);
    }

    // Real RuTracker titles (via Prowlarr) that the first version misparsed.
    [Fact]
    public void ParsesUhdBdRemux()
    {
        var r = P("Дюна: Часть вторая / Dune: Part Two (2024) UHD BDRemux [H.265/2160p] [4K, HDR, Dolby Vision, 10-bit]");

        Assert.Equal(ReleaseSource.Remux, r.Source);
        Assert.Equal(2160, r.Resolution);
        Assert.Equal(VideoCodec.Hevc, r.Codec);
        Assert.Equal(HdrType.DolbyVision, r.Hdr);
    }

    [Theory]
    [InlineData("Дюна: Часть вторая / Dune: Part Two (2024) UHD WEB-DL-HEVC 2160p от selezen | 4K | HDR | Dolby Vision Profile 8 | D, P, A", true)]
    [InlineData("Movie (2026) WEB-DL 1080p | P", true)]
    [InlineData("Дюна: Часть вторая / Dune: Part Two (2024) WEB-DL [H.265/2160p] [4K, HDR10, Dolby Vision, Profile 8, 10-bit] [UKR, EN / UKR, EN Sub]", false)]
    [InlineData("Дюна: Часть вторая / Dune: Part Two (2024) UHD BDRemux [H.265/2160p] [4K, HDR, Dolby Vision P7, 10-bit]", false)]
    [InlineData("A Quiet Place (2018) 1080p BluRay", false)]
    public void RuTrackerVoiceCodesMeanRussian(string title, bool russian) => Assert.Equal(russian, P(title).Languages.Contains("ru"));

    [Fact]
    public void UpscaleRanksBelowReal4k()
    {
        const string Upscaled = "Дюна: Часть вторая / Dune: Part Two (2024) WEB-DL [H.265/2160p] [4K, SDR, 8-bit] [MVO] [handmade Upscale AI]";
        const string Real = "Дюна: Часть вторая / Dune: Part Two (2024) WEB-DL [H.265/2160p] [4K, SDR, 8-bit] [MVO]";

        Assert.True(P(Upscaled).IsUpscale);
        Assert.True(S(Upscaled, year: 2024) < S(Real, year: 2024));
    }

    [Fact]
    public void BigSwarmBeatsSmallSwarm() => Assert.True(S("Movie.2026.2160p.WEB-DL", seeders: 900) > S("Movie.2026.2160p.WEB-DL", seeders: 18));

    [Fact]
    public void TagsInsideWordsDoNotMatch()
    {
        // "DTS" must not read as TS, "DVD" not as DV, "DVO" not as DV, "Russian" not as "RU" alone.
        var r = P("Movie.2026.DVDRip.DVO.DTS");

        Assert.NotEqual(ReleaseSource.Telesync, r.Source);
        Assert.NotEqual(HdrType.DolbyVision, r.Hdr);
    }

    [Fact]
    public void CamNeverBeatsWebDl()
    {
        Assert.True(S("Movie.2026.CAM.2160p.HEVC.Atmos", seeders: 5000) < S("Movie.2026.720p.WEB-DL.x264", seeders: 1));
    }

    [Fact]
    public void HigherResolutionWinsNormally() =>
        Assert.True(S("Movie.2026.2160p.WEB-DL.x265") > S("Movie.2026.1080p.WEB-DL.x265"));

    [Fact]
    public void MaxResolutionCapsWinner()
    {
        var prefs = Prefs with { MaxResolution = 1080 };

        Assert.True(S("Movie.2026.2160p.WEB-DL.x265", prefs: prefs) < S("Movie.2026.1080p.WEB-DL.x265", prefs: prefs));
    }

    [Fact]
    public void ZeroSeedsSink() => Assert.True(S("Movie.2026.2160p.WEB-DL", seeders: 0) < S("Movie.2026.720p.WEBRip", seeders: 3));

    [Fact]
    public void PreferredLanguageWins() =>
        Assert.True(S("Movie.2026.1080p.WEB-DL.RUS") > S("Movie.2026.1080p.WEB-DL.ENG"));

    [Fact]
    public void WrongYearAndJunkArePenalized()
    {
        Assert.True(S("Movie.2019.1080p.WEB-DL") < S("Movie.2026.1080p.WEB-DL"));
        Assert.True(S("Movie.2026.1080p.WEB-DL.Sample") < S("Movie.2026.720p.WEBRip"));
        Assert.Equal(S("Movie.2025.1080p.WEB-DL"), S("Movie.2026.1080p.WEB-DL")); // ±1 year tolerated
    }

    [Fact]
    public void HardLimitsFilterReleases()
    {
        var prefs = Prefs with { MaxSizeBytes = 10_000_000_000, MinSeeders = 2 };

        Assert.True(ReleaseScorer.Allowed(8_000_000_000, 2, prefs));
        Assert.False(ReleaseScorer.Allowed(12_000_000_000, 50, prefs));
        Assert.False(ReleaseScorer.Allowed(8_000_000_000, 1, prefs));
    }

    [Fact]
    public void ReasonsExplainTheScore()
    {
        var (_, reasons) = ReleaseScorer.Score(P("Movie.2026.2160p.WEB-DL.HEVC-RUS"), 15, 2026, Prefs);

        Assert.Contains("2160p +100", reasons);
        Assert.Contains("WebDl +40", reasons);
        Assert.Contains("ru +30", reasons);
        Assert.Contains("15 seeds +40", reasons);
    }

    [Theory]
    [InlineData("Show.S02.1080p.WEB-DL", new[] { 2 }, null, null, null)]
    [InlineData("Show.S01-S03.Complete.1080p", new[] { 1, 2, 3 }, null, null, null)]
    [InlineData("Show.S01E01-E08.1080p", new[] { 1 }, null, 1, 8)]
    [InlineData("Игра престолов / Game of Thrones / Сезон: 3 / Серии: 1-10 из 10 [2013, BDRip 1080p]", new[] { 3 }, null, 1, 10)]
    [InlineData("Сериал / Show / Сезоны: 1-2 [2020, WEB-DL]", new[] { 1, 2 }, null, null, null)]
    [InlineData("Show 3x09 720p", new[] { 3 }, 9, null, null)]
    public void ParsesSeasonsAndEpisodeRanges(string title, int[] seasons, int? episode, int? from, int? to)
    {
        var r = P(title);

        Assert.Equal(seasons, r.Seasons);
        Assert.Equal(episode, r.Episode);
        Assert.Equal(from is null ? null : (from.Value, to!.Value), r.EpisodeRange);
    }

    [Theory]
    [InlineData("Show.S03E09.1080p", true)]
    [InlineData("Show.S03.1080p", true)]
    [InlineData("Show.S01-S05.Complete", true)]
    [InlineData("Show / Сезон: 3 / Серии: 1-10 из 10", true)]
    [InlineData("Show / Сезон: 3 / Серии: 1-5 из 10", false)]
    [InlineData("Show.S03E08.1080p", false)]
    [InlineData("Show.S02.1080p", false)]
    [InlineData("Show.Complete.Series.1080p", true)] // no season info: kept, ranked down
    public void EpisodeMatching(string title, bool matches) =>
        Assert.Equal(matches, ReleaseScorer.Matches(P(title), new ReleaseScorer.EpisodeTarget(3, 9)));

    [Fact]
    public void TvReleasesSinkForMovies() =>
        Assert.True(S("Movie.S01.2026.1080p.WEB-DL") < S("Movie.2026.1080p.WEB-DL"));
}
