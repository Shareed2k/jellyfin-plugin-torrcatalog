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

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Searches Prowlarr for a TMDB movie and returns allowed releases, best first.</summary>
    public async Task<RankedRelease[]> SearchMovieAsync(int tmdbId, CancellationToken ct)
    {
        var cfg = Config();
        var movie = await catalog.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);
        var found = await cache.GetOrFetchAsync($"releases:movie:{tmdbId}", SearchTtl,
            () => SearchAllAsync(cfg, movie, CancellationToken.None), ct).ConfigureAwait(false);
        return Rank(found, movie, cfg);
    }

    // Raw results are cached; ranking reruns per call, so preference changes apply without a new search.
    async Task<ProwlarrRelease[]> SearchAllAsync(PluginConfiguration cfg, TmdbMovie movie, CancellationToken ct)
    {
        var year = Year(movie);
        var queries = new List<(string Type, string Query)>();
        if (!string.IsNullOrEmpty(movie.ImdbId))
        {
            queries.Add(("movie", $"{{ImdbId:{movie.ImdbId}}}{{TmdbId:{movie.Id}}}"));
        }

        // Title searches cover indexers without ID support (most Russian trackers): localized and original title.
        foreach (var title in new[] { movie.Title, movie.OriginalTitle }.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            queries.Add(("search", year is null ? title! : $"{title} {year}"));
        }

        var started = DateTime.UtcNow;
        var results = await Task.WhenAll(queries.Select(async q =>
        {
            try
            {
                return await prowlarr.SearchAsync(cfg, q.Type, q.Query, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
            {
                // One failed strategy shouldn't hide results from the others.
                logger.LogWarning("TorrCatalog: Prowlarr {Type} search for TMDB {TmdbId} failed: {Error}", q.Type, movie.Id, ex.Message);
                return Array.Empty<ProwlarrRelease>();
            }
        })).ConfigureAwait(false);

        var merged = results.SelectMany(r => r)
            .Where(r => string.Equals(r.Protocol, "torrent", StringComparison.OrdinalIgnoreCase))
            .Where(r => r.MagnetUrl is not null || r.DownloadUrl is not null)
            .DistinctBy(r => r.InfoHash?.ToLowerInvariant() ?? r.Guid)
            .ToArray();

        logger.LogInformation("TorrCatalog: Prowlarr search for TMDB {TmdbId} ({Queries} queries) found {Count} torrents in {Ms} ms",
            movie.Id, queries.Count, merged.Length, (int)(DateTime.UtcNow - started).TotalMilliseconds);
        return merged;
    }

    static RankedRelease[] Rank(ProwlarrRelease[] releases, TmdbMovie movie, PluginConfiguration cfg)
    {
        var prefs = ReleaseScorer.Preferences.From(cfg);
        var languages = ReleaseParser.LanguageMatchers(cfg.LanguageAliases);
        var year = Year(movie);

        return releases
            .Where(r => ReleaseScorer.Allowed(r.Size, r.Seeders ?? 0, prefs))
            .Select(r =>
            {
                var parsed = ReleaseParser.Parse(r.Title, languages);
                var (score, reasons) = ReleaseScorer.Score(parsed, r.Seeders ?? 0, year, prefs);
                return new RankedRelease(ReleaseId(r), r, parsed, score, reasons);
            })
            // Tie-breakers: more seeders, then smaller size.
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.Source.Seeders ?? 0)
            .ThenBy(r => r.Source.Size)
            .ToArray();
    }

    static int? Year(TmdbMovie m) =>
        m.ReleaseDate is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    /// <summary>Stable opaque id: Prowlarr guids can be tracker URLs, no need to expose them.</summary>
    static string ReleaseId(ProwlarrRelease r) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(r.InfoHash?.ToLowerInvariant() ?? r.Guid)).AsSpan(0, 8));
}
