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

/// <summary>A playable stream. Probe holds real codecs/tracks from ffprobe (null if probing failed).
/// Release is the release it came from (null for the dev stand-in stream).</summary>
public sealed record ResolvedStream(string Url, string Container, long Size, string ReleaseTitle, MediaInfo? Probe, RankedRelease? Release = null);

/// <summary>Playback failed for a reason worth telling the user (no releases, no video file, ...).</summary>
public sealed class StreamNotFoundException(string message) : Exception(message);

/// <summary>
/// Movie/episode → releases → TorrServer → wait for metadata → pick the video file → ffprobe.
/// Each started release is cached on its own (resume/seek/replay don't search or start it again; concurrent
/// requests share one start). Auto mode walks the best releases, skipping ones that fail to start or that the
/// requesting client can't direct play. A virtual id with a release pin (a picked "version") plays exactly that.
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
    // Analysis itself is short (ProbeAnalyzeMs); the wait is for the torrent's first pieces to arrive.
    static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);
    // Jellyfin's default -analyzeduration is 200 s (and -probesize 1G): on a torrent that means fetching
    // minutes of video before answering. A few seconds is plenty to read codecs and tracks.
    const int ProbeAnalyzeMs = 5000;
    const int MaxAttempts = 3;

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    internal Func<string, CancellationToken, Task<MediaInfo?>>? ProbeOverride { get; init; }

    public bool TorrServerEnabled => !string.IsNullOrWhiteSpace(Config().TorrServerUrl);

    /// <param name="caps">What the requesting client can direct play; null when unknown (no preference).</param>
    /// <param name="report">Progress text for the user (toasts), if anyone is watching. Only the first caller of a
    /// shared (cached) resolution gets the messages.</param>
    public async Task<ResolvedStream> ResolveAsync(VirtualId id, ClientCaps? caps, CancellationToken ct, Action<string>? report = null)
    {
        if (!id.IsPlayable)
        {
            throw new InvalidOperationException($"{id.Kind} is not playable");
        }

        if (!TorrServerEnabled)
        {
            // ponytail: dev stand-in until a TorrServer is configured.
            var dev = Catalog.DevStreamUrl ?? throw new StreamNotFoundException("TorrServer is not configured.");
            return await cache.GetOrFetchAsync($"stream:dev:{dev}", StreamTtl,
                async () => new ResolvedStream(dev, FileSelector.Container(dev) ?? "mp4", 0, "dev stream", await ProbeAsync(dev, CancellationToken.None).ConfigureAwait(false)),
                ct).ConfigureAwait(false);
        }

        report?.Invoke(Text("Ищу раздачи…", "Searching releases…"));
        var ranked = await releases.SearchAsync(id.Base, ct).ConfigureAwait(false);
        if (id.Release != 0)
        {
            // A picked version: exactly that release, the user chose it.
            var picked = ranked.FirstOrDefault(r => id.IsRelease(r.Id)) ?? throw new StreamNotFoundException("The selected release is no longer available.");
            var stream = await StartAsync(id.Base, picked, ct, report).ConfigureAwait(false);
            if (id.Kind == VirtualKind.Episode)
            {
                // Usually a season pack: keep using it for the rest of the season.
                cache.Set(SeasonPickKey(id), picked.Id, SeasonPickTtl);
            }

            return stream;
        }

        // A release picked on another episode of this season wins over Auto, as long as it has this episode.
        if (id.Kind == VirtualKind.Episode
            && cache.TryGetValue(SeasonPickKey(id), out string? seasonPick)
            && ranked.FirstOrDefault(r => r.Id == seasonPick) is { } remembered)
        {
            try
            {
                report?.Invoke(Text($"Использую выбранную раздачу: {remembered.Label}", $"Using the picked release: {remembered.Label}"));
                return await StartAsync(id.Base, remembered, ct, report).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is StreamNotFoundException or TimeoutException or HttpRequestException)
            {
                // e.g. the pack doesn't contain this episode or won't start: fall back to Auto.
                logger.LogInformation("TorrCatalog: season pick '{Release}' can't play {Id}: {Error}", remembered.Source.Title, id, ex.Message);
            }
        }

        // Auto choice is cached per client capability set, so a browser and a TV each keep their own pick
        // and releases that failed to start aren't retried on every Play.
        return await cache.GetOrFetchAsync($"stream:auto:{id.Base.ToGuid():N}:{caps?.Signature ?? "any"}", StreamTtl,
            () => AutoAsync(id.Base, ranked, caps, CancellationToken.None, report), ct).ConfigureAwait(false);
    }

    async Task<ResolvedStream> AutoAsync(VirtualId id, RankedRelease[] ranked, ClientCaps? caps, CancellationToken ct, Action<string>? report)
    {
        // Auto mode: positively scored releases. CAM/TS, dead and junk torrents score below zero; when nothing
        // better exists, a live CAM/TS is still better than refusing (with a warning), never samples/dead ones.
        var candidates = ranked.Where(r => r.Score > 0).ToArray();
        if (candidates.Length == 0)
        {
            candidates = Suspicious(ranked).ToArray();
            if (candidates.Length == 0)
            {
                throw new StreamNotFoundException(ranked.Length == 0
                    ? Text("Раздачи не найдены.", "No releases found.")
                    : Text("Нет живых раздач (все без сидов или неподходящие).", "No live releases (no seeders or unusable)."));
            }

            report?.Invoke(Text($"⚠ Доступна только {candidates[0].Parsed.Source} — качество низкое", $"⚠ Only {candidates[0].Parsed.Source} available, low quality"));
        }

        // Don't start torrents whose title already says the client can't decode them (unless that's all there is).
        var plausible = caps is null ? candidates : candidates.Where(r => caps.MaybePlays(r.Parsed)).ToArray();
        ResolvedStream? unplayable = null;
        foreach (var release in (plausible.Length > 0 ? plausible : candidates).Take(MaxAttempts))
        {
            try
            {
                var stream = await StartAsync(id, release, ct, report).ConfigureAwait(false);
                var verdict = caps?.Check(stream, release.Parsed) ?? Playability.Playable;
                logger.LogInformation("TorrCatalog: {Id} '{Release}' ({Container}, {Streams} probed streams) for this client: {Verdict}",
                    id, release.Source.Title, stream.Container, stream.Probe?.MediaStreams?.Count ?? 0, verdict);
                switch (verdict)
                {
                    case Playability.Playable:
                        return stream;
                    case Playability.Unknown:
                        // Unverifiable: trying more releases costs a torrent start + probe each; let the caller
                        // decide (Jellyfin fallback, which probes and transcodes, or an optimistic direct play).
                        return stream;
                    default:
                        unplayable ??= stream;
                        break;
                }
            }
            catch (Exception ex) when (ex is TimeoutException or StreamNotFoundException or HttpRequestException)
            {
                logger.LogWarning("TorrCatalog: release '{Release}' failed for {Id}: {Error}", release.Source.Title, id, ex.Message);
            }
        }

        if (caps is not null)
        {
            logger.LogInformation("TorrCatalog: no directly playable release for {Id}; client direct-plays: {Profile}", id, caps.Describe());
        }

        // The caller hands unplayable streams to Jellyfin when the fallback is on; otherwise best effort.
        return unplayable ?? throw new StreamNotFoundException(Text(
            $"Не удалось запустить ни одну из {Math.Min(MaxAttempts, candidates.Length)} лучших раздач.",
            $"None of the top {Math.Min(MaxAttempts, candidates.Length)} releases could be started."));
    }

    static readonly TimeSpan SeasonPickTtl = TimeSpan.FromHours(12);

    static string SeasonPickKey(VirtualId episode) => $"season-pick:{episode.TmdbId}:{episode.Season}";

    /// <summary>Low-quality but watchable releases (CAM/TS/TC with live seeders), best first. Never samples or dead torrents.</summary>
    public static IEnumerable<RankedRelease> Suspicious(IEnumerable<RankedRelease> ranked) =>
        ranked.Where(r => r.Score <= 0 && (r.Source.Seeders ?? 0) > 0 && !r.Parsed.IsJunk);

    /// <summary>
    /// Remembers which release a fallback playback is for, so the .strm endpoint (one URL per item, hit by
    /// Jellyfin's own player) streams the version the user picked rather than the auto choice.
    /// </summary>
    public void SetFallbackRelease(VirtualId id)
    {
        if (id.Release != 0)
        {
            cache.Set($"fallback-pin:{id.Base.ToGuid():N}", id, StreamTtl);
        }
        else
        {
            cache.Remove($"fallback-pin:{id.Base.ToGuid():N}");
        }
    }

    /// <summary>The release the .strm endpoint should stream for an item: the remembered pick, else the item itself (auto).</summary>
    public VirtualId FallbackTarget(VirtualId item) =>
        cache.TryGetValue($"fallback-pin:{item.Base.ToGuid():N}", out VirtualId pinned) ? pinned : item.Base;

    /// <summary>Releases for the version picker, if the search finishes quickly; null otherwise (it keeps running and gets cached).</summary>
    public async Task<RankedRelease[]?> QuickReleasesAsync(VirtualId id, TimeSpan wait, CancellationToken ct)
    {
        if (!TorrServerEnabled)
        {
            return null;
        }

        try
        {
            return await releases.SearchAsync(id.Base, CancellationToken.None).WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or HttpRequestException or InvalidOperationException)
        {
            logger.LogDebug("TorrCatalog: releases for {Id} not ready: {Error}", id, ex.Message);
            return null;
        }
    }

    /// <summary>One release started in TorrServer, cached per (item, release).</summary>
    Task<ResolvedStream> StartAsync(VirtualId item, RankedRelease release, CancellationToken ct, Action<string>? report = null) =>
        cache.GetOrFetchAsync($"stream:{item.WithRelease(release.Id).ToGuid():N}", StreamTtl,
            () => StartCoreAsync(item, release, CancellationToken.None, report), ct);

    async Task<ResolvedStream> StartCoreAsync(VirtualId id, RankedRelease release, CancellationToken ct, Action<string>? report)
    {
        var cfg = Config();
        var (title, poster) = await TitleAndPosterAsync(id, ct).ConfigureAwait(false);
        string? hash = null;
        try
        {
            report?.Invoke(Text($"Запускаю: {release.Label}", $"Starting: {release.Label}"));
            var added = await torrServer.AddAsync(cfg, Link(release.Source), title, poster, ct).ConfigureAwait(false);
            hash = added.Hash ?? throw new HttpRequestException("TorrServer did not return a torrent hash");

            var status = await torrServer.WaitForFilesAsync(cfg, hash, TimeSpan.FromSeconds(Math.Clamp(cfg.MetadataTimeoutSeconds, 5, 90)), ct,
                s => report?.Invoke(Text($"Подключаюсь к раздаче… пиров: {s.ActivePeers ?? 0}", $"Connecting… peers: {s.ActivePeers ?? 0}")))
                .ConfigureAwait(false);
            var file = (id.Kind == VirtualKind.Episode ? FileSelector.Episode(status.FileStats!, id.Season, id.Episode) : FileSelector.Movie(status.FileStats!))
                ?? throw new StreamNotFoundException(id.Kind == VirtualKind.Episode
                    ? $"No file for S{id.Season:00}E{id.Episode:00} in the torrent."
                    : "No playable video file in the torrent.");

            var url = TorrServerClient.StreamUrl(cfg, hash, file);
            MediaInfo? probe;
            using (var buffering = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                // The probe is what waits on the first pieces: show how the download is going meanwhile.
                var ticker = report is null ? Task.CompletedTask : ReportBufferingAsync(cfg, hash, report, buffering.Token);
                probe = await ProbeAsync(url, ct).ConfigureAwait(false);
                await buffering.CancelAsync().ConfigureAwait(false);
                await ticker.ConfigureAwait(false);
            }
            logger.LogInformation("TorrCatalog: {Item} resolved to '{Release}' file '{File}' ({Size} bytes, {Streams} streams)",
                title, release.Source.Title, file.Path, file.Length, probe?.MediaStreams?.Count ?? 0);
            return new ResolvedStream(url, FileSelector.Container(file.Path)!, file.Length, release.Source.Title, probe, release);
        }
        catch (Exception ex) when (ex is TimeoutException or StreamNotFoundException or HttpRequestException)
        {
            if (hash is not null)
            {
                await DropQuietlyAsync(cfg, hash).ConfigureAwait(false);
            }

            throw;
        }
    }

    async Task ReportBufferingAsync(PluginConfiguration cfg, string hash, Action<string> report, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var s = await torrServer.GetAsync(cfg, hash, ct).ConfigureAwait(false);
                var mb = (s.LoadedSize ?? s.PreloadedBytes ?? 0) / 1e6;
                var speed = (s.DownloadSpeed ?? 0) / 1e6;
                report(Text(
                    $"Буферизация: {mb:0.0} МБ · {speed:0.0} МБ/с · пиров {s.ActivePeers ?? 0}",
                    $"Buffering: {mb:0.0} MB · {speed:0.0} MB/s · {s.ActivePeers ?? 0} peers"));
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or InvalidOperationException)
        {
            // Stops with the probe, or quietly when TorrServer stats aren't available.
        }
    }

    // ponytail: two languages inline; a resource file if more appear.
    string Text(string ru, string en) => Config().Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase) ? ru : en;

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
                new MediaInfoRequest
                {
                    MediaSource = new MediaSourceInfo { Path = url, Protocol = MediaProtocol.Http, AnalyzeDurationMs = ProbeAnalyzeMs },
                    MediaType = DlnaProfileType.Video,
                },
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

    async Task<(string Title, string? Poster)> TitleAndPosterAsync(VirtualId id, CancellationToken ct)
    {
        if (id.Kind == VirtualKind.Episode)
        {
            var (show, _) = await catalog.GetEpisodeAsync(id.TmdbId, id.Season, id.Episode, ct).ConfigureAwait(false);
            return ($"{show.Name} S{id.Season:00}E{id.Episode:00}", Poster(show.PosterPath));
        }

        var movie = await catalog.GetMovieAsync(id.TmdbId, ct).ConfigureAwait(false);
        return (movie.Title ?? $"TMDB {movie.Id}", Poster(movie.PosterPath));
    }

    static string? Poster(string? tmdbPath) => Catalog.ImageUrl(ImageType.Primary, Catalog.ImageTag(tmdbPath));
}
