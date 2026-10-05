using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog.TorrServer;

/// <summary>A playable stream for a movie. Probe holds real codecs/tracks from ffprobe (null if probing failed).</summary>
public sealed record ResolvedStream(string Url, string Container, long Size, string ReleaseTitle, MediaInfo? Probe);

/// <summary>Playback failed for a reason worth telling the user (no releases, no video file, ...).</summary>
public sealed class StreamNotFoundException(string message) : Exception(message);

/// <summary>
/// Movie → best releases → TorrServer → wait for metadata → pick the video file → ffprobe.
/// Falls through to the next release on timeout or a torrent without video, and caches the result
/// so resume/seek/replay don't search again. Concurrent requests for one movie share a single resolution.
/// </summary>
public sealed class StreamResolver(
    Catalog catalog,
    ReleaseService releases,
    TorrServerClient torrServer,
    IMediaEncoder mediaEncoder,
    IMemoryCache cache,
    ILogger<StreamResolver> logger)
{
    static readonly TimeSpan StreamTtl = TimeSpan.FromHours(6);
    // Probe is optional (playback works without it) but blocks PlaybackInfo, so keep it short.
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);
    const int MaxAttempts = 3;

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    internal Func<string, CancellationToken, Task<MediaInfo?>>? ProbeOverride { get; init; }

    public bool TorrServerEnabled => !string.IsNullOrWhiteSpace(Config().TorrServerUrl);

    public Task<ResolvedStream> ResolveMovieAsync(int tmdbId, CancellationToken ct) =>
        cache.GetOrFetchAsync($"stream:movie:{tmdbId}", StreamTtl, () => ResolveAsync(tmdbId, CancellationToken.None), ct);

    /// <summary>Starts the (cached) release search early, e.g. when a details page opens, so Play doesn't wait for Prowlarr.</summary>
    // ponytail: fires one Prowlarr search per opened movie; add a toggle if that load matters.
    public void WarmUp(int tmdbId)
    {
        if (TorrServerEnabled)
        {
            _ = WarmUpAsync(tmdbId);
        }
    }

    async Task WarmUpAsync(int tmdbId)
    {
        try
        {
            await releases.SearchMovieAsync(tmdbId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogDebug("TorrCatalog: warm-up search for TMDB {TmdbId} failed: {Error}", tmdbId, ex.Message);
        }
    }

    async Task<ResolvedStream> ResolveAsync(int tmdbId, CancellationToken ct)
    {
        var cfg = Config();
        var movie = await catalog.GetMovieAsync(tmdbId, ct).ConfigureAwait(false);

        if (!TorrServerEnabled)
        {
            // ponytail: dev stand-in until a TorrServer is configured.
            var dev = Catalog.DevStreamUrl ?? throw new StreamNotFoundException("TorrServer is not configured.");
            return new ResolvedStream(dev, FileSelector.Container(dev) ?? "mp4", 0, "dev stream", await ProbeAsync(dev, ct).ConfigureAwait(false));
        }

        var ranked = await releases.SearchMovieAsync(tmdbId, ct).ConfigureAwait(false);
        // Auto mode: only positively scored releases (CAM/TS, dead or junk torrents score below zero).
        var candidates = ranked.Where(r => r.Score > 0).Take(MaxAttempts).ToArray();
        if (candidates.Length == 0)
        {
            throw new StreamNotFoundException(ranked.Length == 0 ? "No releases found." : "No release met the quality and seed requirements.");
        }

        foreach (var release in candidates)
        {
            string? hash = null;
            try
            {
                var added = await torrServer.AddAsync(cfg, Link(release.Source), movie.Title ?? release.Source.Title, PosterUrl(movie), ct).ConfigureAwait(false);
                hash = added.Hash ?? throw new HttpRequestException("TorrServer did not return a torrent hash");

                var status = await torrServer.WaitForFilesAsync(cfg, hash, TimeSpan.FromSeconds(Math.Clamp(cfg.MetadataTimeoutSeconds, 5, 90)), ct)
                    .ConfigureAwait(false);
                var file = FileSelector.Movie(status.FileStats!) ?? throw new StreamNotFoundException("No playable video file in the torrent.");

                var url = TorrServerClient.StreamUrl(cfg, hash, file);
                var probe = await ProbeAsync(url, ct).ConfigureAwait(false);
                logger.LogInformation("TorrCatalog: TMDB {TmdbId} resolved to '{Release}' file '{File}' ({Size} bytes, {Streams} streams)",
                    tmdbId, release.Source.Title, file.Path, file.Length, probe?.MediaStreams?.Count ?? 0);
                return new ResolvedStream(url, FileSelector.Container(file.Path)!, file.Length, release.Source.Title, probe);
            }
            catch (Exception ex) when (ex is TimeoutException or StreamNotFoundException or HttpRequestException)
            {
                logger.LogWarning("TorrCatalog: release '{Release}' failed for TMDB {TmdbId}: {Error}", release.Source.Title, tmdbId, ex.Message);
                if (hash is not null)
                {
                    await DropQuietlyAsync(cfg, hash).ConfigureAwait(false);
                }
            }
        }

        throw new StreamNotFoundException($"None of the top {candidates.Length} releases could be started.");
    }

    /// <summary>Real codecs and audio tracks via Jellyfin's ffprobe. Best effort: playback still works without it.</summary>
    async Task<MediaInfo?> ProbeAsync(string url, CancellationToken ct)
    {
        if (ProbeOverride is not null)
        {
            return await ProbeOverride(url, ct).ConfigureAwait(false);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            return await mediaEncoder.GetMediaInfo(
                new MediaInfoRequest { MediaSource = new MediaSourceInfo { Path = url, Protocol = MediaProtocol.Http }, MediaType = DlnaProfileType.Video },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning("TorrCatalog: ffprobe of {Url} failed: {Error}", url, ex.Message);
            return null;
        }
    }

    async Task DropQuietlyAsync(PluginConfiguration cfg, string hash)
    {
        try
        {
            await torrServer.DropAsync(cfg, hash, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            logger.LogDebug("TorrCatalog: dropping {Hash} failed: {Error}", hash, ex.Message);
        }
    }

    // Prefer the magnet; a bare infohash also works (DHT). The Prowlarr download URL is a last resort:
    // it embeds the Prowlarr key, but it only ever goes server-to-server to TorrServer.
    static string Link(ProwlarrRelease r) =>
        r.MagnetUrl ?? (r.InfoHash is { Length: 40 } hash ? hash : r.DownloadUrl ?? throw new StreamNotFoundException("Release has no magnet or torrent link."));

    static string? PosterUrl(TmdbMovie m) => Catalog.ImageUrl(ImageType.Primary, Catalog.ImageTag(m.PosterPath));
}
