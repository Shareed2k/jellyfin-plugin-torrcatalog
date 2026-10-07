using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog.Releases;

/// <summary>A scored release. Keeps the magnet/download URL server-side; clients only get <see cref="ReleaseDto"/>.</summary>
public sealed record RankedRelease(string Id, ProwlarrRelease Source, ParsedRelease Parsed, double Score, string[] Reasons)
{
    /// <summary>Short human label for version pickers, e.g. "2160p WEB-DL DV HEVC · RU · 33.1 GB · 912 seeds".</summary>
    public string Label
    {
        get
        {
            var quality = string.Join(' ', new[]
            {
                Parsed.Resolution > 0 ? $"{Parsed.Resolution}p" : null,
                Parsed.Source switch { ReleaseSource.WebDl => "WEB-DL", ReleaseSource.WebRip => "WEBRip", ReleaseSource.Unknown => null, var s => s.ToString() },
                Parsed.Hdr switch { HdrType.DolbyVision => "DV", HdrType.Hdr10Plus => "HDR10+", HdrType.Hdr10 => "HDR10", HdrType.Hdr => "HDR", _ => null },
                Parsed.Codec is VideoCodec.Unknown ? null : Parsed.Codec.ToString().ToUpperInvariant(),
            }.Where(p => p is not null));
            var parts = new[]
            {
                quality.Length > 0 ? quality : Source.Title,
                Parsed.Languages.Length > 0 ? string.Join('/', Parsed.Languages).ToUpperInvariant() : null,
                $"{Source.Size / 1e9:0.0} GB",
                $"{Source.Seeders ?? 0} seeds",
            };
            return string.Join(" · ", parts.Where(p => p is not null));
        }
    }

    public ReleaseDto ToDto() => new(
        Id,
        Source.Title,
        Source.Indexer,
        Source.Size,
        Source.Seeders ?? 0,
        Source.Leechers ?? 0,
        Source.PublishDate,
        Parsed.Resolution,
        Parsed.Source.ToString(),
        Parsed.Codec.ToString(),
        Parsed.Hdr.ToString(),
        Parsed.Audio.ToString(),
        Parsed.Atmos,
        Parsed.Languages,
        Score,
        Reasons);
}

public sealed record ReleaseDto(
    string Id,
    string Title,
    string? Indexer,
    long Size,
    int Seeders,
    int Leechers,
    DateTime? PublishDate,
    int Resolution,
    string Source,
    string Codec,
    string Hdr,
    string Audio,
    bool Atmos,
    string[] Languages,
    double Score,
    string[] Reasons);

public sealed class ReleaseService(Catalog catalog, ProwlarrClient prowlarr, IMemoryCache cache, ILogger<ReleaseService> logger)
{
    static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(30);

    const int MoviesCategory = 2000;
    const int TvCategory = 5000;

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>What to search for: Prowlarr queries plus the context needed to rank the results.</summary>
    sealed record SearchPlan(string CacheKey, string Label, int Category, (string Type, string Query)[] Queries, int? Year, ReleaseScorer.EpisodeTarget? Episode, ItemIdentity Identity);

    /// <summary>
    /// Whether a release is really for this item. Text searches return near misses ("… 3 (2026)" found
    /// "Enola Holmes 3" for an unrelated film), so a release must carry a matching id from the indexer or
    /// contain one of the item's titles as whole words.
    /// </summary>
    internal sealed record ItemIdentity(string[] Titles, int TmdbId, int ImdbId, int TvdbId)
    {
        public static ItemIdentity Of(int tmdbId, string? imdbId, int? tvdbId, params string?[] titles) => new(
            titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => Normalize(t!)).Where(t => t.Length > 0).Distinct().ToArray(),
            tmdbId,
            imdbId is { Length: > 2 } && int.TryParse(imdbId.AsSpan(2), NumberStyles.None, CultureInfo.InvariantCulture, out var imdb) ? imdb : 0,
            tvdbId ?? 0);

        public bool Matches(ProwlarrRelease r)
        {
            if ((TmdbId > 0 && r.TmdbId == TmdbId) || (ImdbId > 0 && r.ImdbId == ImdbId) || (TvdbId > 0 && r.TvdbId == TvdbId))
            {
                return true;
            }

            var title = $" {Normalize(r.Title)} ";
            return Titles.Any(t => title.Contains($" {t} ", StringComparison.Ordinal));
        }

        // Lowercase, ё→е, punctuation to spaces: "Дюна: Часть вторая" and "ДЮНА - часть вторая" compare equal.
        internal static string Normalize(string text)
        {
            var chars = text.ToLowerInvariant().Replace('ё', 'е').Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray();
            return string.Join(' ', new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }

    /// <summary>Releases for a playable virtual item (movie or episode), best first. The release pin is ignored.</summary>
    public Task<RankedRelease[]> SearchAsync(VirtualId id, CancellationToken ct) => id.Kind switch
    {
        VirtualKind.Movie => SearchMovieAsync(id.TmdbId, ct),
        VirtualKind.Episode => SearchEpisodeAsync(id.TmdbId, id.Season, id.Episode, ct),
        _ => throw new InvalidOperationException($"{id.Kind} is not playable"),
    };

    /// <summary>Searches Prowlarr for a TMDB movie and returns allowed releases, best first.</summary>
    public async Task<RankedRelease[]> SearchMovieAsync(int tmdbId, CancellationToken ct)
    {
        var movie = await catalog.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
        var year = Year(movie.ReleaseDate);
        var queries = new List<(string, string)>();
        if (!string.IsNullOrEmpty(movie.ImdbId))
        {
            queries.Add(("movie", $"{{ImdbId:{movie.ImdbId}}}{{TmdbId:{movie.Id}}}"));
        }

        // Title searches cover indexers without ID support (most Russian trackers): localized and original title.
        queries.AddRange(Titles(movie.Title, movie.OriginalTitle).Select(t => ("search", year is null ? t : $"{t} {year}")));
        return await SearchAsync(
            new SearchPlan($"releases:movie:{tmdbId}", $"TMDB movie {tmdbId}", MoviesCategory, [.. queries], year, null,
                ItemIdentity.Of(movie.Id, movie.ImdbId, null, movie.Title, movie.OriginalTitle)), ct).ConfigureAwait(false);
    }

    /// <summary>Searches for releases containing one episode: single-episode releases and season packs.</summary>
    public async Task<RankedRelease[]> SearchEpisodeAsync(int tvId, int season, int episode, CancellationToken ct)
    {
        var show = await catalog.GetShowAsync(tvId, ct).ConfigureAwait(false);
        var ids = $"{{TmdbId:{tvId}}}"
            + (show.ExternalIds?.ImdbId is { Length: > 0 } imdb ? $"{{ImdbId:{imdb}}}" : string.Empty)
            + (show.ExternalIds?.TvdbId is { } tvdb ? $"{{TvdbId:{tvdb}}}" : string.Empty);

        // Season-level queries (not per episode) so packs are found too; ranking filters by episode.
        // Raw results are cached per season, so moving to the next episode doesn't search again.
        (string, string)[] queries =
        [
            ("tvsearch", $"{ids}{{Season:{season}}}"),
            .. Titles(show.Name, show.OriginalName).Select(t => ("search", t)),
        ];
        return await SearchAsync(
            new SearchPlan($"releases:tv:{tvId}:s{season}", $"TMDB tv {tvId} S{season:00}E{episode:00}", TvCategory, queries, null, new(season, episode),
                ItemIdentity.Of(tvId, show.ExternalIds?.ImdbId, show.ExternalIds?.TvdbId, show.Name, show.OriginalName)), ct).ConfigureAwait(false);
    }

    // Raw results are cached; ranking reruns per call, so preference changes apply without a new search.
    async Task<RankedRelease[]> SearchAsync(SearchPlan plan, CancellationToken ct)
    {
        var cfg = Config();
        var found = await cache.GetOrFetchAsync(plan.CacheKey, SearchTtl, () => FetchAsync(cfg, plan, CancellationToken.None), ct).ConfigureAwait(false);
        return Rank(found, plan, cfg);
    }

    async Task<ProwlarrRelease[]> FetchAsync(PluginConfiguration cfg, SearchPlan plan, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var results = await Task.WhenAll(plan.Queries.Select(async q =>
        {
            try
            {
                return await prowlarr.SearchAsync(cfg, q.Type, q.Query, plan.Category, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                // One failed strategy shouldn't hide results from the others.
                logger.LogWarning("TorrCatalog: Prowlarr {Type} search for {Item} failed: {Error}", q.Type, plan.Label, ex.Message);
                return Array.Empty<ProwlarrRelease>();
            }
        })).ConfigureAwait(false);

        var merged = results.SelectMany(r => r)
            .Where(r => string.Equals(r.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.MagnetUrl is not null || r.DownloadUrl is not null)
            .DistinctBy(r => r.InfoHash?.ToLowerInvariant() ?? r.Guid)
            .ToArray();

        logger.LogInformation("TorrCatalog: Prowlarr search for {Item} ({Queries} queries) found {Count} torrents in {Ms} ms",
            plan.Label, plan.Queries.Length, merged.Length, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        return merged;
    }

    static RankedRelease[] Rank(ProwlarrRelease[] releases, SearchPlan plan, PluginConfiguration cfg)
    {
        var prefs = ReleaseScorer.Preferences.From(cfg);
        var languages = ReleaseParser.LanguageMatchers(cfg.LanguageAliases);

        return releases
            .Where(plan.Identity.Matches)
            .Select(r => (Source: r, Parsed: ReleaseParser.Parse(r.Title, languages)))
            .Where(r => plan.Episode is not { } target || ReleaseScorer.Matches(r.Parsed, target))
            // A season pack only streams the one episode file, so its total size isn't held against it.
            .Where(r => ReleaseScorer.Allowed(IsPack(r.Parsed, plan) ? 0 : r.Source.Size, r.Source.Seeders ?? 0, prefs))
            .Select(r =>
            {
                var (score, reasons) = ReleaseScorer.Score(r.Parsed, r.Source.Seeders ?? 0, plan.Year, prefs, plan.Episode);
                return new RankedRelease(ReleaseId(r.Source), r.Source, r.Parsed, score, reasons);
            })
            // Tie-breakers: more seeders, then smaller size.
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Source.Seeders ?? 0)
            .ThenBy(r => r.Source.Size)
            .ToArray();
    }

    static bool IsPack(ParsedRelease r, SearchPlan plan) => plan.Episode is not null && r.Episode is null;

    static IEnumerable<string> Titles(params string?[] titles) =>
        titles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).Distinct(StringComparer.OrdinalIgnoreCase);

    static int? Year(string? date) =>
        date is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    /// <summary>Stable opaque id: Prowlarr guids can be tracker URLs, no need to expose them.</summary>
    static string ReleaseId(ProwlarrRelease r) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.InfoHash?.ToLowerInvariant() ?? r.Guid)).AsSpan(0, 8));
}
