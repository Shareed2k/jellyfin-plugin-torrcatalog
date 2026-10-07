using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.TorrCatalog.Releases;

namespace Jellyfin.Plugin.TorrCatalog.TorrServer;

/// <summary>Picks the playable file inside a torrent. Never assumes file 1.</summary>
public static partial class FileSelector
{
    static readonly Dictionary<string, string> Containers = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mkv"] = "mkv",
        [".mp4"] = "mp4",
        [".m4v"] = "mp4",
        [".avi"] = "avi",
        [".ts"] = "ts",
        [".m2ts"] = "ts",
        [".mov"] = "mov",
        [".webm"] = "webm",
    };

    static readonly Dictionary<string, Regex> NoLanguages = [];

    /// <summary>Movie: the largest video file that isn't a sample/trailer/extra. Null if the torrent has none.</summary>
    public static TorrentFile? Movie(IEnumerable<TorrentFile> files) => Videos(files).MaxBy(f => f.Length);

    /// <summary>
    /// Episode: a single-video torrent is the episode; in packs, the file named for it (S01E04, 1x04, "04. Title",
    /// "[04]") with the season taken from the name or its folder ("Season 1/"). Files without a season are trusted
    /// to belong to the requested one (the release already matched it).
    /// </summary>
    public static TorrentFile? Episode(IEnumerable<TorrentFile> files, int season, int episode)
    {
        var videos = Videos(files).ToArray();
        if (videos.Length == 1)
        {
            return videos[0];
        }

        var numbered = videos.Select(f => (File: f, Id: EpisodeOf(f.Path))).Where(f => f.Id.Episode == episode).ToArray();
        return numbered.Where(f => f.Id.Season == season).Select(f => f.File).MaxBy(f => f.Length)
            ?? numbered.Where(f => f.Id.Season is null).Select(f => f.File).MaxBy(f => f.Length);
    }

    public static string? Container(string path) => Containers.GetValueOrDefault(Path.GetExtension(path));

    static IEnumerable<TorrentFile> Videos(IEnumerable<TorrentFile> files) =>
        files.Where(f => Container(f.Path) is not null && !Junk().IsMatch(WithoutRoot(f.Path)));

    static (int? Season, int? Episode) EpisodeOf(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var parsed = ReleaseParser.Parse(name, NoLanguages);
        var season = parsed.Seasons is [var s] ? s : FolderSeason(path);
        if (parsed.Episode is { } e)
        {
            return (season, e);
        }

        return EpisodeNumber().Match(name) is { Success: true } m
            ? (season, int.Parse((m.Groups[1].Success ? m.Groups[1] : m.Groups[2]).Value, CultureInfo.InvariantCulture))
            : (season, null);
    }

    static int? FolderSeason(string path) =>
        Path.GetDirectoryName(path)?.Split('/', '\\').Reverse()
            .Select(dir => ReleaseParser.Parse(dir, NoLanguages).Seasons)
            .FirstOrDefault(s => s.Length == 1) is [var season] ? season : null;

    // Multi-file paths start with the torrent name ("Movie.2024.with.Extras/Movie.mkv"); only judge what's inside it.
    static string WithoutRoot(string path) => path.IndexOf('/') is var i and >= 0 ? path[(i + 1)..] : path;

    // Matches junk folder or file names, e.g. "Sample/x.mkv", "movie-trailer.mp4", "Extras/".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(sample|trailer|featurette|extras?|bonus|behind[ ._-]the[ ._-]scenes)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();

    // "04. Title", "04 - Title", "[04] Title", "E04", "Ep 4", "Серия 4" (leading or tagged numbers only, so "1080p" never counts).
    [GeneratedRegex(@"^\s*[\[(]?(\d{1,3})(?=[\])]|\s*[.\-_ ]|$)|(?<![\p{L}\p{N}])(?:e|ep|episode|серия)[ ._-]?0*(\d{1,3})(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex EpisodeNumber();
}
