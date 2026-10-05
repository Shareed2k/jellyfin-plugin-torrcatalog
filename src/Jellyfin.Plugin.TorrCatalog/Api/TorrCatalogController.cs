using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.TorrCatalog.Api;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("Plugins/TorrCatalog")]
public class TorrCatalogController(Catalog catalog) : ControllerBase
{
    /// <summary>Admin connectivity check used by the config page's "Test TMDB" button.</summary>
    [HttpGet("Health")]
    public async Task<ActionResult<HealthResponse>> Health(CancellationToken ct)
    {
        try
        {
            await catalog.PingAsync(ct);
            return new HealthResponse("ok");
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            return new HealthResponse(ex.Message);
        }
    }

    public sealed record HealthResponse(string Tmdb);
}
