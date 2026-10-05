using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Global MVC filter that answers Jellyfin API calls for virtual IDs (catalog sections and TMDB movies),
/// none of which exist in the library DB, and injects the sections into the user's library views.
/// Unhandled routes for a virtual ID are logged so client compatibility gaps show up in the server log.
/// </summary>
public sealed class VirtualItemFilter(Catalog catalog, ILogger<VirtualItemFilter> logger) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            await next();
            return;
        }

        var route = (action.ControllerName, action.ActionName);
        var args = context.ActionArguments;
        var ct = context.HttpContext.RequestAborted;

        var isVirtual = false;
        IActionResult? result = null;
        try
        {
            if (Arg<Guid>(args, "itemId") is { } itemId && VirtualId.TryParse(itemId, out var vid))
            {
                isVirtual = true;
                result = await HandleItemAsync(route, vid, args, ct);
            }
            else if (Arg<Guid>(args, "parentId") is { } parentId && VirtualId.TryParse(parentId, out var pid))
            {
                isVirtual = true;
                result = await HandleParentAsync(route, pid, args, ct);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogError(ex, "TorrCatalog: {Controller}.{Action} failed", route.ControllerName, route.ActionName);
            context.Result = new ObjectResult("Catalog provider is unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        if (isVirtual)
        {
            if (result is null)
            {
                // Let Jellyfin answer (usually 404) but leave a trace of what the client asked for.
                logger.LogWarning("TorrCatalog: unhandled virtual route {Controller}.{Action} {Method} {Path}",
                    route.ControllerName, route.ActionName, context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                await next();
            }
            else
            {
                context.Result = result;
            }

            return;
        }

        switch (route)
        {
            case ("UserViews", "GetUserViews" or "GetUserViewsLegacy"):
                Append(await next(), catalog.Sections.Select(catalog.ToDto).ToArray());
                return;
            case ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                await HandleItemsQueryAsync(context, next, ct);
                return;
            default:
                await next();
                return;
        }
    }

    async Task<IActionResult?> HandleItemAsync((string, string) route, VirtualId id, IDictionary<string, object?> args, CancellationToken ct)
    {
        if (Empty(route) is { } empty)
        {
            return empty;
        }

        var guid = id.ToGuid();
        if (route is ("Items", "GetItemUserData" or "GetItemUserDataLegacy"))
        {
            return new OkObjectResult(Catalog.UserData(guid));
        }

        if (id.Kind == VirtualKind.Section)
        {
            if (catalog.FindSection(id) is not { } section)
            {
                return new NotFoundResult();
            }

            return route switch
            {
                ("UserLibrary", "GetItem" or "GetItemLegacy") => new OkObjectResult(catalog.ToDto(section)),
                ("Image", _) => new NotFoundResult(),
                _ => null,
            };
        }

        var tmdbId = (int)id.Value;
        switch (route)
        {
            case ("UserLibrary", "GetItem" or "GetItemLegacy"):
                return new OkObjectResult(catalog.ToDto(await catalog.GetMovieAsync(tmdbId, ct)));

            case ("Image", _):
                var type = Arg<ImageType>(args, "imageType") ?? ImageType.Primary;
                var url = Catalog.ImageUrl(type, args.TryGetValue("tag", out var t) ? t as string : null);
                if (url is null && type is ImageType.Primary or ImageType.Backdrop)
                {
                    // Tag missing (some clients omit it): look the path up once; details are cached.
                    var m = await catalog.GetMovieAsync(tmdbId, ct);
                    url = Catalog.ImageUrl(type, Catalog.ImageTag(type == ImageType.Primary ? m.PosterPath : m.BackdropPath));
                }

                return url is null ? new NotFoundResult() : new RedirectResult(url);

            case ("MediaInfo", "GetPlaybackInfo" or "GetPostedPlaybackInfo"):
                if (Catalog.DevStreamUrl is not { } stream)
                {
                    return new OkObjectResult(new PlaybackInfoResponse { MediaSources = [], ErrorCode = PlaybackErrorCode.NoCompatibleStream });
                }

                var movie = await catalog.GetMovieAsync(tmdbId, ct);
                return new OkObjectResult(new PlaybackInfoResponse
                {
                    MediaSources = [Catalog.MediaSource(guid, movie, stream)],
                    PlaySessionId = Guid.NewGuid().ToString("N"),
                });

            case ("Videos", "GetVideoStream" or "GetVideoStreamByContainer"):
                return Catalog.DevStreamUrl is { } devUrl ? new RedirectResult(devUrl) : new NotFoundResult();

            default:
                return null;
        }
    }

    async Task<IActionResult?> HandleParentAsync((string, string) route, VirtualId id, IDictionary<string, object?> args, CancellationToken ct)
    {
        if (id.Kind != VirtualKind.Section)
        {
            return null;
        }

        if (catalog.FindSection(id) is not { } section)
        {
            return new NotFoundResult();
        }

        switch (route)
        {
            case ("UserLibrary", "GetLatestMedia" or "GetLatestMediaLegacy"):
                var (latest, _) = await catalog.GetSectionItemsAsync(section, 0, Arg<int>(args, "limit") ?? 20, ct);
                return new OkObjectResult(latest);

            case ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                // Favorites/played/resume filters and non-movie tabs (collections, etc.) have nothing virtual to show yet.
                if (Arg<bool>(args, "isFavorite") == true || Get(args, "filters") is ItemFilter[] { Length: > 0 }
                    || !IncludesMovies(args))
                {
                    return new OkObjectResult(new QueryResult<BaseItemDto>());
                }

                var start = Arg<int>(args, "startIndex") ?? 0;
                var (items, total) = await catalog.GetSectionItemsAsync(section, start, Arg<int>(args, "limit") ?? 100, ct);
                return new OkObjectResult(new QueryResult<BaseItemDto>(start, total, items));

            default:
                return null;
        }
    }

    async Task HandleItemsQueryAsync(ActionExecutingContext context, ActionExecutionDelegate next, CancellationToken ct)
    {
        var args = context.ActionArguments;

        // Play button and some lists resolve items via /Items?ids=...; serve virtual ones, pass the rest through.
        if (Get(args, "ids") is Guid[] { Length: > 0 } ids && ids.Any(i => VirtualId.TryParse(i, out _)))
        {
            BaseItemDto[] virtualItems;
            try
            {
                virtualItems = await Task.WhenAll(ids
                    .Select(i => VirtualId.TryParse(i, out var v) && v.Kind == VirtualKind.Movie ? (int?)v.Value : null)
                    .OfType<int>()
                    .Select(async tmdbId => catalog.ToDto(await catalog.GetMovieAsync(tmdbId, ct))));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                logger.LogError(ex, "TorrCatalog: resolving virtual ids failed");
                context.Result = new ObjectResult("Catalog provider is unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
                return;
            }

            var realIds = ids.Where(i => !VirtualId.TryParse(i, out _)).ToArray();
            if (realIds.Length == 0)
            {
                context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(virtualItems));
                return;
            }

            args["ids"] = realIds;
            Append(await next(), virtualItems);
            return;
        }

        var executed = await next();

        // Global search: append TMDB matches to Jellyfin's own results (first page only).
        if (Get(args, "searchTerm") is string { Length: > 1 } term && IncludesMovies(args) && (Arg<int>(args, "startIndex") ?? 0) == 0)
        {
            try
            {
                Append(executed, await catalog.SearchAsync(term, Math.Min(Arg<int>(args, "limit") ?? 20, 20), ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                // Search must keep working without TMDB.
                logger.LogWarning(ex, "TorrCatalog: TMDB search failed");
            }
        }
    }

    static IActionResult? Empty((string, string) route) => route switch
    {
        ("UserLibrary", "GetIntros" or "GetIntrosLegacy") => new OkObjectResult(new QueryResult<BaseItemDto>()),
        ("UserLibrary", "GetLocalTrailers" or "GetLocalTrailersLegacy" or "GetSpecialFeatures" or "GetSpecialFeaturesLegacy")
            => new OkObjectResult(Array.Empty<BaseItemDto>()),
        ("Library", "GetSimilarItems" or "GetItemCollections") => new OkObjectResult(new QueryResult<BaseItemDto>()),
        ("Library", "GetAncestors") => new OkObjectResult(Array.Empty<BaseItemDto>()),
        ("Library", "GetThemeMedia") => new OkObjectResult(new AllThemeMediaResult()),
        ("Library", "GetThemeSongs" or "GetThemeVideos") => new OkObjectResult(new ThemeMediaResult()),
        ("MediaSegments", "GetItemSegments") => new OkObjectResult(new QueryResult<MediaSegmentDto>()),
        _ => null,
    };

    static void Append(ActionExecutedContext executed, BaseItemDto[] extra)
    {
        if (extra.Length > 0 && executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> found } result)
        {
            result.Value = new QueryResult<BaseItemDto>(found.StartIndex, found.TotalRecordCount + extra.Length, [.. found.Items, .. extra]);
        }
    }

    static bool IncludesMovies(IDictionary<string, object?> args) =>
        Get(args, "includeItemTypes") is not BaseItemKind[] { Length: > 0 } kinds || kinds.Contains(BaseItemKind.Movie);

    static T? Arg<T>(IDictionary<string, object?> args, string name)
        where T : struct
        => Get(args, name) is T value ? value : null;

    static object? Get(IDictionary<string, object?> args, string name) => args.TryGetValue(name, out var v) ? v : null;
}
