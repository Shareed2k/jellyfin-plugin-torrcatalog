using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Shows "preparing playback" progress on the client that pressed Play, via Jellyfin's DisplayMessage session
/// command (a toast in Web/Android/Tizen). Clients can't render custom UI while PlaybackInfo is pending.
/// </summary>
public sealed class PlaybackNotifier(ISessionManager sessions, ILogger<PlaybackNotifier> logger)
{
    // Toasts stack up on the client; one per 1.5 s keeps them readable.
    static readonly TimeSpan MinInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>A reporter for the session behind this request, or null if it has no live session.</summary>
    public Action<string>? For(HttpContext http, Guid userId)
    {
        var deviceId = http.User.FindFirst("Jellyfin-DeviceId")?.Value;
        var session = sessions.Sessions.FirstOrDefault(s => s.DeviceId == deviceId && s.UserId == userId);
        if (session is not null && !session.SessionControllers.Any(c => c.IsSessionActive))
        {
            // Some clients (seen: Jellyfin for Tizen) keep their socket on another session record; messages only
            // travel over a live socket, so use the user's socket-holding session from the same address.
            var mine = sessions.Sessions.Where(s => s.UserId == userId).ToArray();
            logger.LogInformation("TorrCatalog: session {Session} has no live socket; user sessions: {Sessions}", session.Id,
                string.Join(" | ", mine.Select(s => $"{s.Client}/{s.DeviceName}@{s.RemoteEndPoint} controllers={s.SessionControllers.Count(c => c.IsSessionActive)}")));
            session = mine.FirstOrDefault(s => s.RemoteEndPoint == session.RemoteEndPoint && s.SessionControllers.Any(c => c.IsSessionActive)) ?? session;
        }
        if (session is null)
        {
            logger.LogInformation("TorrCatalog: no session for device {DeviceId} / user {UserId}; status messages off ({Count} sessions)",
                deviceId, userId, sessions.Sessions.Count());
            return null;
        }

        logger.LogInformation("TorrCatalog: status messages to session {Session} ({Client} on {Device}, remote control: {Remote}, controllers: {Controllers})",
            session.Id, session.Client, session.DeviceName, session.SupportsRemoteControl,
            string.Join(", ", session.SessionControllers.Select(c => $"{c.GetType().Name}(active={c.IsSessionActive})")));

        var gate = new Lock();
        var last = DateTime.MinValue;
        string? lastText = null;
        return text =>
        {
            lock (gate)
            {
                // Progress is throttled; warnings (⚠) and final error texts must always get through.
                var important = text.StartsWith('⚠') || text.EndsWith('.');
                var now = DateTime.UtcNow;
                if (text == lastText || (!important && now - last < MinInterval))
                {
                    return;
                }

                (last, lastText) = (now, text);
            }

            _ = SendAsync(session.Id, text);
        };
    }

    async Task SendAsync(string sessionId, string text)
    {
        try
        {
            await sessions.SendMessageCommand(null!, sessionId, new MessageCommand { Header = "TorrCatalog", Text = text, TimeoutMs = 4000 }, CancellationToken.None)
                .ConfigureAwait(false);
            logger.LogDebug("TorrCatalog: status to {Session}: {Text}", sessionId, text);
        }
        catch (Exception ex)
        {
            // Purely cosmetic: never let a status toast break playback.
            logger.LogWarning("TorrCatalog: status message to session {Session} failed: {Error}", sessionId, ex.Message);
        }
    }
}
