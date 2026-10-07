using Jellyfin.Plugin.TorrCatalog.Configuration;

namespace Jellyfin.Plugin.TorrCatalog.Releases;

/// <summary>Weighted release scoring. Every contribution is recorded as a reason so rankings are explainable.</summary>
// ponytail: weights are constants from the plan; only the user-facing knobs (resolution cap, size, seeders,
// languages) are in config. Move weights to config if people actually want to tune them.
public static class ReleaseScorer
{
    public sealed record Preferences(int MaxResolution, long MaxSizeBytes, int MinSeeders, string[] Languages)
    {
        public static Preferences From(PluginConfiguration c) => new(
            c.MaxResolution > 0 ? c.MaxResolution : 2160,
            c.MaxSizeGb > 0 ? c.MaxSizeGb * 1_000_000_000L : long.MaxValue,
            Math.Max(0, c.MinSeeders),
            c.PreferredLanguages.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(l => l.ToLowerInvariant()).ToArray());
    }

    /// <summary>The episode a TV search is for; null means a movie.</summary>
    public sealed record EpisodeTarget(int Season, int Episode);

    /// <summary>Returns false when the release must not be offered at all (hard limits).</summary>
    public static bool Allowed(long size, int seeders, Preferences prefs) => size <= prefs.MaxSizeBytes && seeders >= prefs.MinSeeders;

    /// <summary>Whether a TV release can contain the target episode: right season, and the episode (or range) covers it.
    /// Releases without any season info are kept (complete-series packs often lack tags) but ranked down.</summary>
    public static bool Matches(ParsedRelease r, EpisodeTarget target) =>
        (r.Seasons.Length == 0 || r.Seasons.Contains(target.Season))
        && (r.Episode is null || r.Episode == target.Episode)
        && (r.EpisodeRange is not { } range || (target.Episode >= range.From && target.Episode <= range.To));

    public static (double Score, string[] Reasons) Score(ParsedRelease r, int seeders, int? expectedYear, Preferences prefs, EpisodeTarget? episode = null)
    {
        var reasons = new List<string>();
        double score = 0;

        void Add(double points, string why)
        {
            if (points != 0)
            {
                score += points;
                reasons.Add($"{why} {points:+0;-0}");
            }
        }

        Add(r.Resolution switch { 2160 => 100, 1080 => 60, 720 => 20, _ => 0 }, r.Resolution > 0 ? $"{r.Resolution}p" : "resolution");
        if (r.Resolution > prefs.MaxResolution)
        {
            Add(-200, $"above max {prefs.MaxResolution}p");
        }

        Add(r.Source switch
        {
            ReleaseSource.Remux => 45,
            ReleaseSource.WebDl => 40,
            ReleaseSource.BluRay => 35,
            ReleaseSource.WebRip => 20,
            ReleaseSource.Hdtv => 10,
            ReleaseSource.Dvd => 5,
            ReleaseSource.Cam or ReleaseSource.Telesync => -1000,
            ReleaseSource.Telecine => -500,
            _ => 0,
        }, r.Source.ToString());

        Add(r.Hdr switch { HdrType.DolbyVision => 35, HdrType.Hdr10Plus => 30, HdrType.Hdr10 => 25, HdrType.Hdr => 20, _ => 0 }, r.Hdr.ToString());
        Add(r.Codec switch { VideoCodec.Hevc or VideoCodec.Av1 => 20, VideoCodec.H264 => 5, _ => 0 }, r.Codec.ToString());
        Add(r.Audio switch { AudioCodec.TrueHd => 12, AudioCodec.DtsHdMa or AudioCodec.DtsX => 10, AudioCodec.Eac3 => 7, _ => 0 }, r.Audio.ToString());
        Add(r.Atmos ? 15 : 0, "Atmos");

        var langRank = prefs.Languages.Select((lang, i) => r.Languages.Contains(lang) ? i : int.MaxValue).DefaultIfEmpty(int.MaxValue).Min();
        Add(langRank switch { 0 => 30, 1 => 15, _ => 0 }, langRank < prefs.Languages.Length ? prefs.Languages[langRank] : "language");

        // Seeds: log bonus capped at 60 (63+ seeders); swarm size drives streaming start-up. Dead torrents sink.
        Add(seeders == 0 ? -500 : Math.Round(Math.Min(60, 10 * Math.Log2(seeders + 1))), $"{seeders} seeds");

        // Series titles carry the show's first-air year, not the episode's: year checks are for movies only.
        if (episode is null && expectedYear is { } year && r.Years.Length > 0 && !r.Years.Any(y => Math.Abs(y - year) <= 1))
        {
            Add(-300, "wrong year");
        }

        var isTvRelease = r.Episode is not null || r.EpisodeRange is not null || r.Seasons.Length > 0;
        Add(episode is null && isTvRelease ? -500 : 0, "TV release");
        Add(episode is not null && r.Seasons.Length == 0 ? -100 : 0, "no season info");
        Add(r.IsJunk ? -1000 : 0, "sample/trailer/extras");
        Add(r.IsUpscale ? -60 : 0, "upscale"); // fake 4K: rank it roughly like the 1080p it came from

        return (score, reasons.ToArray());
    }
}
