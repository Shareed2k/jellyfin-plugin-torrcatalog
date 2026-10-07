using Jellyfin.Data.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>Per-user progress for virtual items: playback reports, played marks, "Continue watching", DTO user data.</summary>
public sealed partial class VirtualItemFilter
{
    /// <summary>Item routes that read or change user data. Null when the route isn't one of them.</summary>
    async Task<IActionResult?> HandleUserDataRouteAsync(Call call, VirtualId id)
    {
        switch (call.Route)
        {
            case ("Items", "GetItemUserData" or "GetItemUserDataLegacy"):
            case ("Items", "UpdateItemUserData" or "UpdateItemUserDataLegacy"): // ponytail: edits ignored, current state returned
            case ("UserLibrary", "MarkFavoriteItem" or "MarkFavoriteItemLegacy" or "UnmarkFavoriteItem" or "UnmarkFavoriteItemLegacy"):
                return new OkObjectResult(watch.UserData(call.UserId, id.ToGuid(), null));

            case ("Playstate", "MarkPlayedItem" or "MarkPlayedItemLegacy" or "MarkUnplayedItem" or "MarkUnplayedItemLegacy") when id.IsPlayable:
                watch.SetPlayed(call.UserId, id, call.Route.Action.StartsWith("MarkPlayed", StringComparison.Ordinal));
                return new OkObjectResult(watch.UserData(call.UserId, id.ToGuid(), null));

            // Legacy per-item playstate routes carry the position in the query.
            case ("Playstate", "OnPlaybackStart" or "OnPlaybackStartLegacy"):
                return new NoContentResult();
            case ("Playstate", "OnPlaybackProgress" or "OnPlaybackProgressLegacy" or "OnPlaybackStopped" or "OnPlaybackStoppedLegacy") when id.IsPlayable:
                if (Arg<long>(call.Args, "positionTicks") is { } position)
                {
                    await RecordAsync(call.UserId, id, position, call.Ct);
                }

                return new NoContentResult();

            default:
                return null;
        }
    }

    /// <summary>/Sessions/Playing[/Progress|/Stopped]: the body says which item and where.</summary>
    async Task RecordReportedProgressAsync(Call call)
    {
        var (itemId, position) = (Get(call.Args, "playbackStartInfo") ?? Get(call.Args, "playbackProgressInfo") ?? Get(call.Args, "playbackStopInfo")) switch
        {
            PlaybackProgressInfo p => (p.ItemId, p.PositionTicks), // also PlaybackStartInfo
            PlaybackStopInfo s => (s.ItemId, s.PositionTicks),
            _ => (Guid.Empty, null),
        };

        if (position is { } ticks && VirtualId.TryParse(itemId, out var id) && id.IsPlayable)
        {
            await RecordAsync(call.UserId, id, ticks, call.Ct);
        }
    }

    async Task RecordAsync(Guid userId, VirtualId id, long positionTicks, CancellationToken ct)
    {
        long? runtime = null;
        try
        {
            runtime = (await PlayableDtoAsync(id.Base, ct)).RunTimeTicks;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            // Without a runtime the position is still stored, just without played/too-early thresholds.
            logger.LogDebug("TorrCatalog: no runtime for {Id}: {Error}", id, ex.Message);
        }

        watch.ReportPosition(userId, id, positionTicks, runtime);
    }

    /// <summary>"Continue watching": Jellyfin's own in-progress items plus virtual ones, newest first.</summary>
    async Task HandleResumeAsync(Call call, ActionExecutionDelegate next)
    {
        var executed = await next();
        var wantsVideo = Get(call.Args, "mediaTypes") is not MediaType[] { Length: > 0 } types || types.Contains(MediaType.Video);
        if (!wantsVideo || (Arg<int>(call.Args, "startIndex") ?? 0) != 0
            || executed.Result is not ObjectResult { Value: QueryResult<BaseItemDto> found } result)
        {
            return;
        }

        var limit = Arg<int>(call.Args, "limit") ?? 12;
        var virtualItems = new List<BaseItemDto>();
        foreach (var entry in watch.Resumable(call.UserId).Take(limit))
        {
            if (!VirtualId.TryParse(entry.ItemId, out var id)
                || !IncludesKind(call.Args, id.Kind == VirtualKind.Episode ? BaseItemKind.Episode : BaseItemKind.Movie))
            {
                continue;
            }

            try
            {
                virtualItems.Add(await PlayableDtoAsync(id, call.Ct));
            }
            catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
            {
                logger.LogDebug("TorrCatalog: skipping resume item {Id}: {Error}", id, ex.Message);
            }
        }

        if (virtualItems.Count > 0)
        {
            var merged = found.Items.Concat(Personalize(virtualItems.ToArray(), call.UserId))
                .OrderByDescending(d => d.UserData?.LastPlayedDate)
                .Take(limit)
                .ToArray();
            result.Value = new QueryResult<BaseItemDto>(found.StartIndex, found.TotalRecordCount + virtualItems.Count, merged);
        }
    }

    /// <summary>Fills the caller's progress into every virtual DTO a result carries.</summary>
    IActionResult Personalize(IActionResult result, Guid userId)
    {
        if (result is ObjectResult { Value: { } value })
        {
            switch (value)
            {
                case BaseItemDto dto:
                    Personalize([dto], userId);
                    break;
                case IEnumerable<BaseItemDto> dtos:
                    Personalize(dtos.ToArray(), userId);
                    break;
                case QueryResult<BaseItemDto> page:
                    Personalize(page.Items.ToArray(), userId);
                    break;
            }
        }

        return result;
    }

    BaseItemDto[] Personalize(BaseItemDto[] dtos, Guid userId)
    {
        foreach (var dto in dtos)
        {
            if (VirtualId.TryParse(dto.Id, out var id) && id.IsPlayable)
            {
                dto.UserData = watch.UserData(userId, dto.Id, dto.RunTimeTicks);
            }
        }

        return dtos;
    }
}
