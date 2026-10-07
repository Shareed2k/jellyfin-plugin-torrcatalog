using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TorrCatalog.Configuration;

namespace Jellyfin.Plugin.TorrCatalog.Tmdb;

public sealed partial class TmdbClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "TorrCatalog.Tmdb";

    public static readonly Uri BaseAddress = new("https://api.themoviedb.org/3/");

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // ponytail: no retries; results are cached and TMDB is reliable. Add bounded retry if 5xx show up in logs.
    public async Task<T> GetAsync<T>(PluginConfiguration config, string path, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDB API key is not configured.");
        }

        // Path comes from admin config; keep it a plain relative path so requests can't leave api.themoviedb.org.
        if (!SafePath().IsMatch(path))
        {
            throw new InvalidOperationException($"Invalid TMDB path '{path}'.");
        }

        var key = config.TmdbApiKey.Trim();
        var bearer = key.StartsWith("eyJ", StringComparison.Ordinal);
        var q = $"language={Uri.EscapeDataString(config.Language)}&region={Uri.EscapeDataString(config.Region)}"
            + $"&include_adult={(config.IncludeAdult ? "true" : "false")}";
        if (!bearer)
        {
            q += "&api_key=" + Uri.EscapeDataString(key);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            q += "&" + query.Trim().TrimStart('?', '&');
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, path + "?" + q);
        if (bearer)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }

        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Never include the request URI: it may carry api_key.
            throw new HttpRequestException($"TMDB {path} returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException($"TMDB {path} returned an empty body");
    }

    [GeneratedRegex("^[a-z0-9_]+(/[a-z0-9_]+)*$")]
    private static partial Regex SafePath();
}

public sealed record TmdbPage<T>(int Page, int TotalPages, int TotalResults, T[] Results);

public sealed record TmdbMovie(
    int Id,
    string? Title,
    string? OriginalTitle,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? ReleaseDate,
    double VoteAverage,
    int VoteCount,
    int? Runtime = null,
    string? ImdbId = null,
    string? Tagline = null,
    TmdbGenre[]? Genres = null);

public sealed record TmdbGenre(int Id, string Name);

public sealed record TmdbShow(
    int Id,
    string? Name,
    string? OriginalName,
    string? Overview,
    string? PosterPath,
    string? BackdropPath,
    string? FirstAirDate,
    double VoteAverage,
    int VoteCount,
    TmdbGenre[]? Genres = null,
    TmdbSeasonInfo[]? Seasons = null,
    int[]? EpisodeRunTime = null,
    TmdbExternalIds? ExternalIds = null);

public sealed record TmdbSeasonInfo(int SeasonNumber, string? Name, string? PosterPath, string? AirDate, int EpisodeCount);

public sealed record TmdbSeason(int SeasonNumber, string? Name, string? Overview, string? PosterPath, string? AirDate, TmdbEpisode[] Episodes);

public sealed record TmdbEpisode(int EpisodeNumber, int SeasonNumber, string? Name, string? Overview, string? StillPath, string? AirDate, int? Runtime, double VoteAverage);

public sealed record TmdbExternalIds(string? ImdbId, int? TvdbId);
