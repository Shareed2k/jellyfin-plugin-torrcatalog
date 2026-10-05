using Jellyfin.Plugin.TorrCatalog.Releases;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog.Api;

[ApiController]
[Authorize]
[Route("Plugins/TorrCatalog")]
public class TorrCatalogController(Catalog catalog, ReleaseService releases, ProwlarrClient prowlarr, ILogger<TorrCatalogController> logger) : ControllerBase
{
    /// <summary>Admin connectivity check used by the config page's test buttons.</summary>
    [HttpGet("Health")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken ct)
    {
        var config = Plugin.Instance!.Configuration;
        return new HealthResponse(
            await Check(() => catalog.PingAsync(ct)),
            await Check(() => prowlarr.PingAsync(config, ct)));
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

    public sealed record HealthResponse(string Tmdb, string Prowlarr);
}
