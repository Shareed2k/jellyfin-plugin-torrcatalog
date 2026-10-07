using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Optional replacement for the JELLYFIN_FFmpeg__analyzeduration/probesize env vars: Jellyfin probes .strm targets
/// (the transcoding fallback) with -analyzeduration 200M / -probesize 1G, which on a torrent means minutes of download.
/// Jellyfin reads these keys from IConfiguration on every probe, so overriding them in memory takes effect at once.
/// Server-wide (every library), hence opt-in; turning it off restores the previous values.
/// </summary>
public sealed class StrmProbeTuning(IConfiguration configuration, ILogger<StrmProbeTuning> logger) : IHostedService
{
    const string AnalyzeDurationKey = "FFmpeg:analyzeduration";
    const string ProbeSizeKey = "FFmpeg:probesize";

    string? _originalAnalyzeDuration;
    string? _originalProbeSize;
    bool _applied;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _originalAnalyzeDuration = configuration[AnalyzeDurationKey];
        _originalProbeSize = configuration[ProbeSizeKey];
        Apply();
        if (Plugin.Instance is { } plugin)
        {
            plugin.ConfigurationChanged += (_, _) => Apply();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    void Apply()
    {
        var enabled = Plugin.Instance?.Configuration.FastStrmProbing == true;
        if (enabled == _applied)
        {
            return;
        }

        configuration[AnalyzeDurationKey] = enabled ? "5M" : _originalAnalyzeDuration;
        configuration[ProbeSizeKey] = enabled ? "50M" : _originalProbeSize;
        _applied = enabled;
        logger.LogInformation("TorrCatalog: fast ffprobe {State} (analyzeduration={Analyze}, probesize={Size})",
            enabled ? "on" : "off", configuration[AnalyzeDurationKey], configuration[ProbeSizeKey]);
    }
}
