using System.Security.Claims;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaSegments;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Global MVC filter that answers Jellyfin API calls for virtual IDs (catalog sections, movies, series,
/// seasons, episodes), none of which exist in the library DB, and injects them into views, search and
/// "Continue watching". Unhandled routes for a virtual ID are logged so client gaps show up in the server log.
/// Split by concern: this file (dispatch, lists), .Items.cs (item routes, playback), .UserData.cs (progress).
/// </summary>
public sealed partial class VirtualItemFilter(
    Catalog catalog,
    StreamResolver streams,
    WatchState watch,
    Persistence persistence,
    PlaybackNotifier notifier,
    ILogger<VirtualItemFilter> logger)
    : IAsyncActionFilter
{
    /// <summary>One intercepted API call.</summary>
    sealed record Call(ActionExecutingContext Context, (string Controller, string Action) Route, IDictionary<string, object?> Args, Guid UserId)
    {
        public CancellationToken Ct => Context.HttpContext.RequestAborted;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor action)
        {
            await next();
            return;
        }

        var call = new Call(context, (action.ControllerName, action.ActionName), context.ActionArguments, UserId(context));

        var isVirtual = true;
        IActionResult? result;
        try
        {
            if (VirtualArg(call.Args, "itemId") is { } item)
            {
                result = await HandleItemAsync(call, item);
            }
            else if (VirtualArg(call.Args, "seriesId") is { } series)
            {
                result = await HandleSeriesRouteAsync(call, series);
            }
            else if (VirtualArg(call.Args, "parentId") is { } parent)
            {
                result = await HandleParentAsync(call, parent);
            }
            else
            {
                isVirtual = false;
                result = null;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogError(ex, "TorrCatalog: {Controller}.{Action} failed", call.Route.Controller, call.Route.Action);
            context.Result = new ObjectResult("Catalog provider is unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }

        if (isVirtual)
        {
            if (result is PassThroughResult)
            {
                // Arguments were rewritten to a real item: let Jellyfin's own action handle the call.
                await next();
            }
            else if (result is null)
            {
                // Let Jellyfin answer (usually 404) but leave a trace of what the client asked for.
                logger.LogWarning("TorrCatalog: unhandled virtual route {Controller}.{Action} {Method} {Path}",
                    call.Route.Controller, call.Route.Action, context.HttpContext.Request.Method, context.HttpContext.Request.Path);
                await next();
            }
            else
            {
                context.Result = Personalize(result, call.UserId);
            }

            return;
        }

        switch (call.Route)
        {
            case ("UserViews", "GetUserViews" or "GetUserViewsLegacy"):
                Append(await next(), catalog.Sections.Select(catalog.ToDto).ToArray());
                return;
            case ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                await HandleItemsQueryAsync(call, next);
                return;
            case ("Items", "GetResumeItems" or "GetResumeItemsLegacy"):
                await HandleResumeAsync(call, next);
                return;
            case ("Playstate", "ReportPlaybackStart" or "ReportPlaybackProgress" or "ReportPlaybackStopped"):
                await RecordReportedProgressAsync(call);
                await next(); // Jellyfin still tracks the session ("now playing")
                return;
            default:
                await next();
                return;
        }
    }

    async Task<IActionResult?> HandleParentAsync(Call call, VirtualId id)
    {
        switch (id.Kind)
        {
            case VirtualKind.Section:
                return await HandleSectionAsync(call, id);
            case VirtualKind.Series when call.Route is ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                // Some clients list a series' children (or all its episodes) through /Items?parentId=.
                return IncludesKind(call.Args, BaseItemKind.Episode) && !IncludesKind(call.Args, BaseItemKind.Season, emptyMeansAll: false)
                    ? Page(call, await EpisodesAsync(id.TmdbId, null, call.Ct))
                    : Page(call, await SeasonDtosAsync(id.TmdbId, call.Ct));
            case VirtualKind.Season when call.Route is ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                return Page(call, await EpisodesAsync(id.TmdbId, id.Season, call.Ct));
            default:
                return null;
        }
    }

    async Task<IActionResult?> HandleSectionAsync(Call call, VirtualId id)
    {
        if (catalog.FindSection(id) is not { } section)
        {
            return new NotFoundResult();
        }

        var kind = Catalog.IsTv(section) ? BaseItemKind.Series : BaseItemKind.Movie;
        switch (call.Route)
        {
            // Library page tabs/filters: nothing virtual to aggregate (Jellyfin would throw "Invalid parent id").
            case ("Filter", "GetQueryFiltersLegacy"):
                return new OkObjectResult(new QueryFiltersLegacy());
            case ("Filter", "GetQueryFilters"):
                return new OkObjectResult(new QueryFilters());
            case ("Studios", "GetStudios") or ("Genres", "GetGenres") or ("Persons", "GetPersons"):
                return new OkObjectResult(new QueryResult<BaseItemDto>());

            case ("UserLibrary", "GetLatestMedia" or "GetLatestMediaLegacy"):
                var (latest, _) = await catalog.GetSectionItemsAsync(section, 0, Arg<int>(call.Args, "limit") ?? 20, call.Ct);
                return new OkObjectResult(latest);

            case ("Items", "GetItems" or "GetItemsByUserIdLegacy"):
                // Favorites/played filters and other tabs (collections, genres, ...) have nothing virtual to show yet.
                if (Arg<bool>(call.Args, "isFavorite") == true || Get(call.Args, "filters") is ItemFilter[] { Length: > 0 }
                    || !IncludesKind(call.Args, kind))
                {
                    return new OkObjectResult(new QueryResult<BaseItemDto>());
                }

                var start = Arg<int>(call.Args, "startIndex") ?? 0;
                var (items, total) = await catalog.GetSectionItemsAsync(section, start, Arg<int>(call.Args, "limit") ?? 100, call.Ct);
                return new OkObjectResult(new QueryResult<BaseItemDto>(start, total, items));

            default:
                return null;
        }
    }

    async Task HandleItemsQueryAsync(Call call, ActionExecutionDelegate next)
    {
        var args = call.Args;

        // Play button and some lists resolve items via /Items?ids=...; serve virtual ones, pass the rest through.
        if (Get(args, "ids") is Guid[] { Length: > 0 } ids && ids.Any(i => VirtualId.TryParse(i, out _)))
        {
            BaseItemDto[] virtualItems;
            try
            {
                virtualItems = (await Task.WhenAll(ids
                    .Select(i => VirtualId.TryParse(i, out var v) ? v : (VirtualId?)null)
                    .OfType<VirtualId>()
                    .Select(v => ItemDtoAsync(v, call.Ct)))).OfType<BaseItemDto>().ToArray();
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                logger.LogError(ex, "TorrCatalog: resolving virtual ids failed");
                call.Context.Result = new ObjectResult("Catalog provider is unavailable.") { StatusCode = StatusCodes.Status503ServiceUnavailable };
                return;
            }

            Personalize(virtualItems, call.UserId);
            var realIds = ids.Where(i => !VirtualId.TryParse(i, out _)).ToArray();
            if (realIds.Length == 0)
            {
                call.Context.Result = new OkObjectResult(new QueryResult<BaseItemDto>(virtualItems));
                return;
            }

            args["ids"] = realIds;
            Append(await next(), virtualItems);
            return;
        }

        var executed = await next();

        // Global search: append TMDB matches to Jellyfin's own results (first page only).
        if (Get(args, "searchTerm") is string { Length: > 1 } term && (Arg<int>(args, "startIndex") ?? 0) == 0)
        {
            var limit = Math.Min(Arg<int>(args, "limit") ?? 20, 20);
            try
            {
                var found = new List<BaseItemDto>();
                if (IncludesKind(args, BaseItemKind.Movie))
                {
                    found.AddRange(await catalog.SearchMoviesAsync(term, limit, call.Ct));
                }

                if (IncludesKind(args, BaseItemKind.Series))
                {
                    found.AddRange(await catalog.SearchShowsAsync(term, limit, call.Ct));
                }

                Append(executed, Personalize(found.ToArray(), call.UserId));
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

    /// <summary>Marker result: "the call now targets a real item, run Jellyfin's action".</summary>
    sealed class PassThroughResult : IActionResult
    {
        public static readonly PassThroughResult Instance = new();

        public Task ExecuteResultAsync(ActionContext context) => throw new InvalidOperationException("Pass-through marker must not be executed.");
    }

    /// <summary>Pages a fully built list using the call's startIndex/limit.</summary>
    static OkObjectResult Page(Call call, BaseItemDto[] all)
    {
        var start = Math.Max(0, Arg<int>(call.Args, "startIndex") ?? 0);
        var limit = Arg<int>(call.Args, "limit") ?? all.Length;
        return new OkObjectResult(new QueryResult<BaseItemDto>(start, all.Length, all.Skip(start).Take(limit).ToArray()));
    }

    static void Append(ActionExecutedContext executed, BaseItemDto[] extra)
    {
        if (extra.Length > 0 && executed.Result is ObjectResult { Value: QueryResult<BaseItemDto> found } result)
        {
            result.Value = new QueryResult<BaseItemDto>(found.StartIndex, found.TotalRecordCount + extra.Length, [.. found.Items, .. extra]);
        }
    }

    static bool IncludesKind(IDictionary<string, object?> args, BaseItemKind kind, bool emptyMeansAll = true) =>
        Get(args, "includeItemTypes") is BaseItemKind[] { Length: > 0 } kinds ? kinds.Contains(kind) : emptyMeansAll;

    static VirtualId? VirtualArg(IDictionary<string, object?> args, string name) =>
        Arg<Guid>(args, name) is { } g && VirtualId.TryParse(g, out var v) ? v : null;

    static T? Arg<T>(IDictionary<string, object?> args, string name)
        where T : struct
        => Get(args, name) is T value ? value : null;

    static object? Get(IDictionary<string, object?> args, string name) => args.TryGetValue(name, out var v) ? v : null;

    /// <summary>
    /// The caller from the auth claim. An explicit userId is honoured only for admins, so one user can't
    /// read or change another user's progress (Jellyfin's own check lives inside the actions we bypass).
    /// </summary>
    static Guid UserId(ActionExecutingContext context)
    {
        var user = context.HttpContext.User;
        var self = Guid.TryParse(user.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;
        return Arg<Guid>(context.ActionArguments, "userId") is { } requested && requested != Guid.Empty && user.IsInRole("Administrator")
            ? requested
            : self;
    }
}
