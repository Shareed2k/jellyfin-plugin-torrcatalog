using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TorrCatalog.Configuration;

namespace Jellyfin.Plugin.TorrCatalog.TorrServer;

/// <summary>TorrServer MatriX HTTP API (POST /torrents actions, /stream). Verified against MatriX.145.2.</summary>
public sealed partial class TorrServerClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "TorrCatalog.TorrServer";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    // Torrent "stat" values (server/torr/state/state.go).
    const int StatClosed = 4;

    /// <summary>Adds a torrent (magnet, 40-hex hash or .torrent URL). Returns at once; metadata arrives in the background.</summary>
    public Task<TorrentStatus> AddAsync(PluginConfiguration config, string link, string title, string? poster, CancellationToken ct) =>
        PostAsync(config, new { action = "add", link, title, poster = poster ?? string.Empty, save_to_db = false }, ct);

    public Task<TorrentStatus> GetAsync(PluginConfiguration config, string hash, CancellationToken ct) =>
        PostAsync(config, new { action = "get", hash = ValidHash(hash) }, ct);

    /// <summary>Drops the torrent from memory (not from disk/DB).</summary>
    public async Task DropAsync(PluginConfiguration config, string hash, CancellationToken ct)
    {
        using var response = await SendAsync(config, new { action = "drop", hash = ValidHash(hash) }, ct).ConfigureAwait(false);
    }

    /// <summary>Polls until the torrent's file list is known. Throws TimeoutException if it doesn't arrive in time.</summary>
    public async Task<TorrentStatus> WaitForFilesAsync(PluginConfiguration config, string hash, TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            var status = await GetAsync(config, hash, ct).ConfigureAwait(false);
            if (status.FileStats is { Length: > 0 })
            {
                return status;
            }

            if (status.Stat == StatClosed)
            {
                throw new TimeoutException($"TorrServer closed torrent {hash} before metadata arrived");
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException($"No torrent metadata for {hash} after {timeout.TotalSeconds:0}s");
            }

            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    internal TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<string> VersionAsync(PluginConfiguration config, CancellationToken ct)
    {
        using var response = await httpClientFactory.CreateClient(HttpClientName).GetAsync(new Uri(BaseUrl(config.TorrServerUrl), "echo"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // Also proves credentials when auth is on: "list" sits behind it.
        using var auth = await SendAsync(config, new { action = "list" }, ct).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Stream URL clients are redirected to. The file name only gives players an extension; index is TorrServer's 1-based file id.</summary>
    public static string StreamUrl(PluginConfiguration config, string hash, TorrentFile file) =>
        new Uri(BaseUrl(config.TorrServerUrl), $"stream/{Uri.EscapeDataString(Path.GetFileName(file.Path))}").AbsoluteUri
        + $"?link={ValidHash(hash)}&index={file.Id}&play";

    async Task<TorrentStatus> PostAsync(PluginConfiguration config, object body, CancellationToken ct)
    {
        using var response = await SendAsync(config, body, ct).ConfigureAwait(false);
        return await response.Content.ReadFromJsonAsync<TorrentStatus>(Json, ct).ConfigureAwait(false)
            ?? throw new HttpRequestException("TorrServer returned an empty body");
    }

    async Task<HttpResponseMessage> SendAsync(PluginConfiguration config, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUrl(config.TorrServerUrl), "torrents"))
        {
            Content = JsonContent.Create(body),
        };
        if (config.TorrServerUsername.Length > 0)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{config.TorrServerUsername}:{config.TorrServerPassword}")));
        }

        var response = await httpClientFactory.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            response.Dispose();
            throw new HttpRequestException($"TorrServer /torrents returned {(int)response.StatusCode}", null, response.StatusCode);
        }

        return response;
    }

    public static Uri BaseUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("TorrServer URL is not configured or invalid (expected http(s)://host:8090/).");
        }

        return uri.AbsoluteUri.EndsWith('/') ? uri : new Uri(uri.AbsoluteUri + "/");
    }

    // TorrServer panics (500) on malformed hashes; never forward one.
    static string ValidHash(string hash) =>
        Hex40().IsMatch(hash) ? hash.ToLowerInvariant() : throw new InvalidOperationException($"Invalid torrent hash '{hash}'");

    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex Hex40();
}

/// <summary>Subset of TorrServer's TorrentStatus. Most fields are omitempty, hence nullable.</summary>
public sealed record TorrentStatus(string? Hash, int Stat, string? StatString, string? Title, TorrentFile[]? FileStats);

/// <summary>A file inside a torrent. Id is 1-based and is what /stream?index= expects.</summary>
public sealed record TorrentFile(int Id, string Path, long Length);
