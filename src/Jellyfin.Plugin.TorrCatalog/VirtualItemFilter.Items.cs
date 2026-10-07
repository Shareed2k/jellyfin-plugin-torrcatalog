using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>Item routes: details, images, playback (with release versions), seasons and episodes.</summary>
public sealed partial class VirtualItemFilter
{
    // Version picker: how long a details page may wait for the release search (Prowlarr takes ~3-7 s across
    // indexers; later opens hit the 30 min cache), and how many versions to offer.
    // ponytail: fixed wait; make it a setting if slow indexers make details pages feel sluggish.
    static readonly TimeSpan VersionsWait = TimeSpan.FromSeconds(8);
    const int MaxVersions = 10;

    async Task<IActionResult?> HandleItemAsync(Call call, VirtualId id)
    {
        if (Empty(call.Route) is { } empty)
        {
            return empty;
        }

        if (await HandleUserDataRouteAsync(call, id) is { } userData)
        {
            return userData;
        }

        switch (call.Route)
        {
            case ("UserLibrary", "GetItem" or "GetItemLegacy"):
                return await ItemDtoAsync(id, call.Ct, withVersions: true) is { } dto ? new OkObjectResult(dto) : new NotFoundResult();

            case ("Image", _):
                return await ImageAsync(call, id);

            case ("MediaInfo", "GetPlaybackInfo" or "GetPostedPlaybackInfo") when id.IsPlayable:
                return await PlaybackInfoAsync(call, id);

            case ("Videos", "GetVideoStream" or "GetVideoStreamByContainer") when id.IsPlayable:
                // Normally a cache hit from PlaybackInfo; resolves again if the cache expired.
                try
                {
                    // PlaybackInfo hands out the started release's id as mediaSourceId, so this is that exact stream.
                    return new RedirectResult((await streams.ResolveAsync(PickedVersion(call, id), null, call.Ct)).Url);
                }
                catch (StreamNotFoundException ex)
                {
                    logger.LogWarning("TorrCatalog: stream for {Id} unavailable: {Error}", id, ex.Message);
                    return new NotFoundResult();
                }

            default:
                return null;
        }
    }

    /// <summary>The DTO for any virtual id. A version id (release pin) returns the base item under the version's id,
    /// which is how the web client loads a picked version.</summary>
    async Task<BaseItemDto?> ItemDtoAsync(VirtualId id, CancellationToken ct, bool withVersions = false)
    {
        switch (id.Kind)
        {
            case VirtualKind.Section:
                return catalog.FindSection(id) is { } section ? catalog.ToDto(section) : null;
            case VirtualKind.Series:
                return catalog.ToDto(await catalog.GetShowAsync(id.TmdbId, ct));
            case VirtualKind.Season:
                var show = await catalog.GetShowAsync(id.TmdbId, ct);
                return Catalog.Seasons(show).FirstOrDefault(s => s.SeasonNumber == id.Season) is { } info ? catalog.ToDto(show, info) : null;
        }

        var dto = await PlayableDtoAsync(id, ct);
        if (withVersions && await streams.QuickReleasesAsync(id, VersionsWait, ct) is { Length: > 0 } ranked)
        {
            if (Versions(id.Base, ranked, dto.RunTimeTicks) is { Length: > 0 } versions)
            {
                dto.MediaSources = versions;
                dto.EnableMediaSourceDisplay = true;
            }
        }

        if (id.Release != 0)
        {
            dto.Id = id.ToGuid();
        }

        return dto;
    }

    async Task<BaseItemDto> PlayableDtoAsync(VirtualId id, CancellationToken ct)
    {
        if (id.Kind == VirtualKind.Episode)
        {
            var (show, episode) = await catalog.GetEpisodeAsync(id.TmdbId, id.Season, id.Episode, ct);
            return catalog.ToDto(show, episode);
        }

        return catalog.ToDto(await catalog.GetMovieAsync(id.TmdbId, ct));
    }

    /// <summary>
    /// Release picker as native Jellyfin "versions": first entry is Auto (best release, with fallback), then the
    /// top releases, each with its own virtual id (base item + release pin) so clients can load and play it.
    /// </summary>
    static MediaSourceInfo[] Versions(VirtualId baseId, RankedRelease[] ranked, long? runtimeTicks)
    {
        // Good releases first, then low-quality ones (CAM/TS) marked ⚠, so a user can still choose them.
        var good = ranked.Where(r => r.Score > 0).Take(MaxVersions).ToArray();
        var poor = StreamResolver.Suspicious(ranked).Take(5).ToArray();
        MediaSourceInfo[] versions =
        [
            .. good.Length > 0 ? [Version(baseId.ToGuid(), "Auto · " + good[0].Label, good[0], runtimeTicks)] : Array.Empty<MediaSourceInfo>(),
            .. good.Select(r => Version(baseId.WithRelease(r.Id).ToGuid(), r.Label, r, runtimeTicks)),
            .. poor.Select(r => Version(baseId.WithRelease(r.Id).ToGuid(), $"⚠ {r.Parsed.Source} · {r.Label}", r, runtimeTicks)),
        ];

        // Auto plus a single release (or one lone release) leaves nothing to choose: no picker, Play handles it.
        return versions.Length - (good.Length > 0 ? 1 : 0) >= 2 ? versions : [];
    }

    static MediaSourceInfo Version(Guid id, string name, RankedRelease release, long? runtimeTicks) => new()
    {
        Id = id.ToString("N"),
        Name = name,
        Protocol = MediaProtocol.File,
        Type = MediaSourceType.Default,
        Size = release.Source.Size,
        IsRemote = false,
        SupportsDirectPlay = true,
        SupportsDirectStream = false,
        SupportsTranscoding = false,
        SupportsProbing = false,
        RunTimeTicks = runtimeTicks,
        MediaStreams = [],
    };

    async Task<IActionResult> PlaybackInfoAsync(Call call, VirtualId id)
    {
        // Resolution (search + torrent metadata + probe) happens here, so the client gets real
        // container/codecs/tracks and the stream request is just a cached redirect.
        var target = PickedVersion(call, id);
        var caps = Body<DeviceProfile>(call.Args, "DeviceProfile") is { } profile ? new ClientCaps(profile) : null;
        var report = notifier.For(call.Context.HttpContext, call.UserId);
        try
        {
            var stream = await streams.ResolveAsync(target, caps, call.Ct, report);
            // Auto or a picked version alike: what this client can't verifiably direct play goes to Jellyfin.
            if (caps is not null && stream.Release is { } started && persistence.Enabled
                && caps.Check(stream, started.Parsed) != Playability.Playable)
            {
                streams.SetFallbackRelease(target);
                report?.Invoke(catalog.Config().Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                    ? "Формат не поддерживается напрямую, готовлю перекодирование…"
                    : "Not directly playable here, preparing transcoding…");
                return await HandOverToJellyfinAsync(call, id);
            }

            var dto = await PlayableDtoAsync(id.Base, call.Ct);
            // Report the release actually started, so the stream request (mediaSourceId) gets the same one.
            var sourceId = stream.Release is { } release ? id.Base.WithRelease(release.Id) : target;
            return new OkObjectResult(new PlaybackInfoResponse
            {
                MediaSources = [Catalog.MediaSource(sourceId.ToGuid(), dto.RunTimeTicks, stream, ReleaseScorer.Preferences.From(catalog.Config()).Languages)],
                PlaySessionId = Guid.NewGuid().ToString("N"),
            });
        }
        catch (Exception ex) when (ex is StreamNotFoundException or HttpRequestException or InvalidOperationException or TimeoutException)
        {
            // Clients only say "not compatible"; tell the user the actual reason, details go to the server log.
            logger.LogWarning("TorrCatalog: playback of {Id} unavailable: {Error}", target, ex.Message);
            report?.Invoke(ex is StreamNotFoundException
                ? ex.Message
                : catalog.Config().Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase)
                    ? "Не удалось подготовить воспроизведение, подробности в логе сервера."
                    : "Couldn't prepare playback, see the server log.");
            return new OkObjectResult(new PlaybackInfoResponse { MediaSources = [], ErrorCode = PlaybackErrorCode.NoCompatibleStream });
        }
    }

    /// <summary>
    /// Phase 7 fallback for clients that can't direct play any release: persist the item as a real library item
    /// (.strm) and rewrite this PlaybackInfo call to it, so Jellyfin answers, with transcoding if needed.
    /// The client keeps the virtual item id (progress still lands in our store); stream URLs it gets point at the real item.
    /// </summary>
    async Task<IActionResult> HandOverToJellyfinAsync(Call call, VirtualId id)
    {
        var realId = await persistence.EnsureAsync(id.Base, call.Ct);
        logger.LogInformation("TorrCatalog: {Id} isn't direct-playable by this client; Jellyfin plays it as item {RealId}", id, realId);
        call.Args["itemId"] = realId;
        call.Args["mediaSourceId"] = null;
        if (Get(call.Args, "playbackInfoDto") is { } body)
        {
            body.GetType().GetProperty("MediaSourceId")?.SetValue(body, null);
        }

        return PassThroughResult.Instance;
    }

    /// <summary>The version to play: the item id itself, or a version passed as mediaSourceId for the same item.</summary>
    static VirtualId PickedVersion(Call call, VirtualId id) =>
        VirtualId.TryParse(MediaSourceId(call.Args), out var source) && source.Base == id.Base ? source : id;

    static string? MediaSourceId(IDictionary<string, object?> args) =>
        Get(args, "mediaSourceId") as string ?? Body<string>(args, "MediaSourceId");

    // POST PlaybackInfo carries options in its body DTO, whose type lives in Jellyfin.Api (not published
    // to NuGet), hence this one reflective read.
    static T? Body<T>(IDictionary<string, object?> args, string property)
        where T : class
        => Get(args, "playbackInfoDto") is { } body ? body.GetType().GetProperty(property)?.GetValue(body) as T : null;

    async Task<IActionResult> ImageAsync(Call call, VirtualId id)
    {
        if (id.Kind == VirtualKind.Section)
        {
            return new NotFoundResult();
        }

        var type = Arg<ImageType>(call.Args, "imageType") ?? ImageType.Primary;
        var url = Catalog.ImageUrl(type, call.Args.TryGetValue("tag", out var t) ? t as string : null);
        if (url is null && type is ImageType.Primary or ImageType.Backdrop)
        {
            // Tag missing (some clients omit it): look the path up; details are cached.
            var (primary, backdrop) = await ImagePathsAsync(id, call.Ct);
            url = Catalog.ImageUrl(type, Catalog.ImageTag(type == ImageType.Primary ? primary : backdrop));
        }

        return url is null ? new NotFoundResult() : new RedirectResult(url);
    }

    async Task<(string? Primary, string? Backdrop)> ImagePathsAsync(VirtualId id, CancellationToken ct)
    {
        if (id.Kind == VirtualKind.Movie)
        {
            var movie = await catalog.GetMovieAsync(id.TmdbId, ct);
            return (movie.PosterPath, movie.BackdropPath);
        }

        var show = await catalog.GetShowAsync(id.TmdbId, ct);
        return id.Kind switch
        {
            VirtualKind.Season => (Catalog.Seasons(show).FirstOrDefault(s => s.SeasonNumber == id.Season)?.PosterPath ?? show.PosterPath, show.BackdropPath),
            VirtualKind.Episode => ((await catalog.GetEpisodeAsync(id.TmdbId, id.Season, id.Episode, ct)).Episode.StillPath ?? show.PosterPath, show.BackdropPath),
            _ => (show.PosterPath, show.BackdropPath),
        };
    }

    /// <summary>/Shows/{seriesId}/... routes.</summary>
    async Task<IActionResult?> HandleSeriesRouteAsync(Call call, VirtualId series)
    {
        switch (call.Route)
        {
            case ("TvShows", "GetSeasons"):
                return Page(call, await SeasonDtosAsync(series.TmdbId, call.Ct));

            case ("TvShows", "GetEpisodes"):
                int? season = VirtualArg(call.Args, "seasonId") is { Kind: VirtualKind.Season } s ? s.Season : Arg<int>(call.Args, "season");
                return Page(call, await EpisodesAsync(series.TmdbId, season, call.Ct));

            case ("TvShows", "GetNextUp"):
                return await NextUpAsync(call, series) is { } next
                    ? new OkObjectResult(new QueryResult<BaseItemDto>([next]))
                    : new OkObjectResult(new QueryResult<BaseItemDto>());

            default:
                return null;
        }
    }

    async Task<BaseItemDto[]> SeasonDtosAsync(int tvId, CancellationToken ct)
    {
        var show = await catalog.GetShowAsync(tvId, ct);
        return Catalog.Seasons(show).Select(s => catalog.ToDto(show, s)).ToArray();
    }

    /// <summary>Episodes of one season, or of every season (in order) when season is null.</summary>
    async Task<BaseItemDto[]> EpisodesAsync(int tvId, int? season, CancellationToken ct)
    {
        var show = await catalog.GetShowAsync(tvId, ct);
        var numbers = season is { } n ? [n] : Catalog.Seasons(show).Select(s => s.SeasonNumber).Where(s => s > 0).ToArray();
        var seasons = await Task.WhenAll(numbers.Select(n => catalog.GetSeasonAsync(tvId, n, ct)));
        return seasons.SelectMany(s => s.Episodes).Select(e => catalog.ToDto(show, e)).ToArray();
    }

    /// <summary>The episode after the furthest one the user played (or is playing); S01E01 if nothing yet.</summary>
    async Task<BaseItemDto?> NextUpAsync(Call call, VirtualId series)
    {
        var episodes = await EpisodesAsync(series.TmdbId, null, call.Ct);
        var lastPlayed = Array.FindLastIndex(episodes, e => VirtualId.TryParse(e.Id, out var v) && watch.Get(call.UserId, v) is { } entry
            && (entry.Played || entry.PositionTicks > 0));
        var next = lastPlayed >= 0 && watch.Get(call.UserId, EpisodeId(episodes[lastPlayed]))?.Played != true
            ? episodes[lastPlayed] // still in progress: continue it
            : episodes.Skip(lastPlayed + 1).FirstOrDefault();
        return next is { LocationType: not LocationType.Virtual } ? next : null;
    }

    static VirtualId EpisodeId(BaseItemDto dto) => VirtualId.TryParse(dto.Id, out var v) ? v : default;
}
