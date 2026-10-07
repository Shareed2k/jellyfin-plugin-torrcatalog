using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog.Api;

[ApiController]
[Authorize]
[Route("Plugins/TorrCatalog")]
public class TorrCatalogController(
    Catalog catalog,
    ReleaseService releases,
    ProwlarrClient prowlarr,
    TorrServerClient torrServer,
    StreamResolver streams,
    Persistence persistence,
    PlaybackNotifier notifier,
    ILogger<TorrCatalogController> logger) : ControllerBase
{
    /// <summary>
    /// Target of persisted .strm files: redirects to the best release's TorrServer stream. Anonymous because
    /// Jellyfin's ffmpeg can't authenticate; the secret key in the .strm URL guards it instead.
    /// </summary>
    [HttpGet("Stream/{id}")]
    [HttpHead("Stream/{id}")]
    [AllowAnonymous]
    public async Task<ActionResult> Stream(Guid id, [FromQuery] string? key, CancellationToken ct)
    {
        if (!persistence.KeyMatches(key) || !VirtualId.TryParse(id, out var item) || !item.IsPlayable)
        {
            return NotFound();
        }

        try
        {
            // The version the user picked (if the fallback was for one), else the best release; no client
            // preference here: Jellyfin transcodes whatever its client can't play.
            return Redirect((await streams.ResolveAsync(streams.FallbackTarget(item), null, ct)).Url);
        }
        catch (Exception ex) when (ex is StreamNotFoundException or HttpRequestException or InvalidOperationException or TimeoutException)
        {
            logger.LogWarning("TorrCatalog: .strm stream for {Id} unavailable: {Error}", item, ex.Message);
            return NotFound();
        }
    }

    /// <summary>Sends a status toast to the caller's own session: checks that playback progress messages can reach it.</summary>
    [HttpPost("TestStatus")]
    public ActionResult<string> TestStatus()
    {
        var userId = Guid.TryParse(User.FindFirst("Jellyfin-UserId")?.Value, out var id) ? id : Guid.Empty;
        if (notifier.For(HttpContext, userId) is not { } report)
        {
            return "No live session found for this device; see the server log.";
        }

        report("TorrCatalog: status messages work.");
        return "Sent. A toast should appear now; if not, check the server log.";
    }

    /// <summary>Admin connectivity check used by the config page's test buttons.</summary>
    [HttpGet("Health")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        return new HealthResponse(
            await Check(() => catalog.PingAsync(ct)),
            await Check(() => prowlarr.PingAsync(config, ct)),
            await Check(() => torrServer.VersionAsync(config, ct)));
    }

    /// <summary>Ranked torrent releases for a TMDB movie. No download/magnet URLs are returned.</summary>
    [HttpGet("Releases/Movie/{tmdbId:int}")]
    public async Task<ActionResult<ReleaseDto[]>> MovieReleases(int tmdbId, CancellationToken ct)
    {
        try
        {
            return (await releases.SearchMovieAsync(tmdbId, ct)).Select(r => r.ToDto()).ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TimeoutException)
        {
            // Any signed-in user can call this: keep upstream hosts/details in the server log only.
            logger.LogError(ex, "TorrCatalog: release search for TMDB {TmdbId} failed", tmdbId);
            var message = ex switch
            {
                InvalidOperationException => "Release search is not configured.",
                TimeoutException => "Release search timed out.",
                _ => "Release search is unavailable.",
            };
            return Problem(message, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    static async Task<string> Check(Func<Task> probe)
    {
        try
        {
            await probe();
            return "ok";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return ex.Message;
        }
    }

    public sealed record HealthResponse(string Tmdb, string Prowlarr, string TorrServer);
}
