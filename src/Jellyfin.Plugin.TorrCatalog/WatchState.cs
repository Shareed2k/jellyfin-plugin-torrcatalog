using System.Text.Json;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Per-user playback progress for virtual items (Jellyfin's own user data needs a DB item).
/// Keyed by the item's base virtual id, so all versions of a movie/episode share progress.
/// </summary>
// ponytail: one JSON file rewritten on every change, fine for a household; move to SQLite if it grows to many users.
public sealed class WatchState
{
    // Jellyfin defaults: below 5% isn't worth resuming, past 90% counts as watched.
    const double MinResumeFraction = 0.05;
    const double MaxResumeFraction = 0.90;

    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    readonly Lock _lock = new();
    readonly string _path;
    Dictionary<string, Entry>? _entries;

    public WatchState()
        : this(Path.Combine(Plugin.Instance!.DataFolderPath, "watch-state.json"))
    {
    }

    internal WatchState(string path) => _path = path;

    public sealed record Entry(Guid UserId, Guid ItemId, long PositionTicks, bool Played, DateTime LastPlayed);

    public Entry? Get(Guid userId, VirtualId item)
    {
        lock (_lock)
        {
            return Entries().GetValueOrDefault(Key(userId, item));
        }
    }

    /// <summary>Records a playback position; applies Jellyfin's resume thresholds when the runtime is known.</summary>
    public void ReportPosition(Guid userId, VirtualId item, long positionTicks, long? runtimeTicks)
    {
        var played = false;
        if (runtimeTicks > 0)
        {
            var fraction = (double)positionTicks / runtimeTicks.Value;
            if (fraction >= MaxResumeFraction)
            {
                (played, positionTicks) = (true, 0);
            }
            else if (fraction < MinResumeFraction)
            {
                positionTicks = 0;
            }
        }

        lock (_lock)
        {
            var key = Key(userId, item);
            // Watched stays watched while rewatching (as in Jellyfin); only "mark unplayed" clears it.
            played |= Entries().GetValueOrDefault(key)?.Played == true;
            Entries()[key] = new Entry(userId, item.Base.ToGuid(), positionTicks, played, DateTime.UtcNow);
            Save();
        }
    }

    public void SetPlayed(Guid userId, VirtualId item, bool played)
    {
        lock (_lock)
        {
            Entries()[Key(userId, item)] = new Entry(userId, item.Base.ToGuid(), 0, played, DateTime.UtcNow);
            Save();
        }
    }

    /// <summary>In-progress items for "Continue watching", most recent first.</summary>
    public Entry[] Resumable(Guid userId)
    {
        lock (_lock)
        {
            return Entries().Values.Where(e => e.UserId == userId && e.PositionTicks > 0 && !e.Played)
                .OrderByDescending(e => e.LastPlayed).ToArray();
        }
    }

    /// <summary>Jellyfin user data for a virtual item DTO (dtoId may be a version id; progress lives on the base item).</summary>
    public UserItemDataDto UserData(Guid userId, Guid dtoId, long? runtimeTicks)
    {
        var entry = VirtualId.TryParse(dtoId, out var id) ? Get(userId, id) : null;
        var position = entry?.PositionTicks ?? 0;
        return new UserItemDataDto
        {
            Key = (VirtualId.TryParse(dtoId, out var v) ? v.Base.ToGuid() : dtoId).ToString("N"),
            ItemId = dtoId,
            PlaybackPositionTicks = position,
            Played = entry?.Played ?? false,
            PlayCount = entry?.Played == true ? 1 : 0,
            LastPlayedDate = entry?.LastPlayed,
            PlayedPercentage = position > 0 && runtimeTicks > 0 ? 100.0 * position / runtimeTicks.Value : null,
        };
    }

    static string Key(Guid userId, VirtualId item) => $"{userId:N}:{item.Base.ToGuid():N}";

    Dictionary<string, Entry> Entries()
    {
        if (_entries is null)
        {
            _entries = [];
            if (File.Exists(_path))
            {
                foreach (var e in JsonSerializer.Deserialize<Entry[]>(File.ReadAllText(_path), Json) ?? [])
                {
                    _entries[$"{e.UserId:N}:{e.ItemId:N}"] = e;
                }
            }
        }

        return _entries;
    }

    // Write to a temp file and rename, so a crash mid-write never leaves a truncated state file.
    void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries!.Values, Json));
        File.Move(tmp, _path, overwrite: true);
    }
}
