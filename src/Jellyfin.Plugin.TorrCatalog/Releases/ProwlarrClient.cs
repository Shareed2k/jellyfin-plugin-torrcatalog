using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.TorrCatalog.Configuration;

namespace Jellyfin.Plugin.TorrCatalog.Releases;

/// <summary>Prowlarr v1 search API. The API key goes in the X-Api-Key header, never in URLs.</summary>
public sealed class ProwlarrClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "TorrCatalog.Prowlarr";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="type">"movie"/"tvsearch" for ID search ({ImdbId:..}{TmdbId:..}{Season:..} tokens in the query), "search" for free text.</param>
    /// <param name="category">Newznab category: 2000 movies, 5000 TV.</param>
    public async Task<ProwlarrRelease[]> SearchAsync(PluginConfiguration config, string type, string query, int category, CancellationToken ct)
    {
        var path = $"api/v1/search?type={type}&categories={category}&limit=100&query={Uri.EscapeDataString(query)}";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(config.SearchTimeoutSeconds, 5, 120)));
        try
        {
            return await GetAsync<ProwlarrRelease[]>(config, path, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Prowlarr search timed out after {config.SearchTimeoutSeconds}s");
        }
    }

    public Task PingAsync(PluginConfiguration config, CancellationToken ct) => GetAsync<JsonElement>(config, "api/v1/system/status", ct);

    async Task<T> GetAsync<T>(PluginConfiguration config, string path, CancellationToken ct)
    {
        var baseUrl = BaseUrl(config.ProwlarrUrl);
        if (string.IsNullOrWhiteSpace(config.ProwlarrApiKey))
        {
            throw new InvalidOperationException("Prowlarr API key is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUrl, path));
        request.Headers.Add("X-Api-Key", config.ProwlarrApiKey.Trim());
        using var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Prowlarr {path.Split('?')[0]} returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("Prowlarr returned an empty body");
    }

    /// <summary>Admin-configured base URL; only absolute http(s) without credentials is accepted.</summary>
    public static Uri BaseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("Prowlarr URL is not configured or invalid (expected http(s)://host:port/).");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }
}

/// <summary>Subset of Prowlarr's ReleaseResource we use. DownloadUrl embeds the Prowlarr API key: never send it to clients.</summary>
public sealed record ProwlarrRelease(
    string Guid,
    string Title,
    long Size,
    string? Indexer,
    string? Protocol,
    int? Seeders,
    int? Leechers,
    string? MagnetUrl,
    string? DownloadUrl,
    string? InfoHash,
    DateTime? PublishDate,
    int ImdbId = 0,
    int TmdbId = 0,
    int TvdbId = 0);
