using System.Text.RegularExpressions;

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

    /// <summary>Movie: the largest video file that isn't a sample/trailer/extra. Null if the torrent has none.</summary>
    public static TorrentFile? Movie(IEnumerable<TorrentFile> files) =>
        files.Where(f => Container(f.Path) is not null && !Junk().IsMatch(WithoutRoot(f.Path)))
            .MaxBy(f => f.Length);

    // Multi-file paths start with the torrent name ("Movie.2024.with.Extras/Movie.mkv"); only judge what's inside it.
    static string WithoutRoot(string path) => path.IndexOf('/') is var i and >= 0 ? path[(i + 1)..] : path;

    public static string? Container(string path) => Containers.GetValueOrDefault(Path.GetExtension(path));

    // Matches junk folder or file names, e.g. "Sample/x.mkv", "movie-trailer.mp4", "Extras/".
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(sample|trailer|featurette|extras?|bonus|behind[ ._-]the[ ._-]scenes)(?![\p{L}\p{N}])", RegexOptions.IgnoreCase)]
    private static partial Regex Junk();
}
