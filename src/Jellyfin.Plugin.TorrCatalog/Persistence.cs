using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>
/// Phase 7 fallback: turns a virtual movie/episode into a real Jellyfin item by writing a .strm file into a
/// plugin-owned library and scanning it. Jellyfin then handles playback itself, including transcoding, which
/// virtual items can't get. The .strm points back at this plugin's /Stream endpoint, which picks a torrent.
/// </summary>
public sealed class Persistence(Catalog catalog, ILibraryManager libraryManager, IFileSystem fileSystem, IServerApplicationHost host, ILogger<Persistence> logger)
{
    const string MoviesLibrary = "TorrCatalog Movies";
    const string ShowsLibrary = "TorrCatalog Shows";

    // A brand-new library's folder item only exists once Jellyfin's background scan has created it.
    static readonly TimeSpan LibraryWait = TimeSpan.FromSeconds(60);

    // ponytail: one global gate; persisting is rare (first fallback per item), so contention doesn't matter.
    readonly SemaphoreSlim _gate = new(1, 1);

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    public bool Enabled => !string.IsNullOrWhiteSpace(Config().PersistencePath);

    /// <summary>Makes sure the item exists as a real library item and returns its Jellyfin id.</summary>
    public async Task<Guid> EnsureAsync(VirtualId id, CancellationToken ct)
    {
        var cfg = Config();
        var movies = id.Kind == VirtualKind.Movie;
        var libraryDir = Path.Combine(cfg.PersistencePath, movies ? "Movies" : "Shows");
        var path = Path.Combine(libraryDir, await RelativePathAsync(id, ct).ConfigureAwait(false));
        if (libraryManager.FindByPath(path, false) is { } existing)
        {
            return existing.Id;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (libraryManager.FindByPath(path, false) is { } raced)
            {
                return raced.Id;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, StrmUrl(cfg, id), ct).ConfigureAwait(false);

            var library = await EnsureLibraryAsync(cfg, libraryDir, movies, ct).ConfigureAwait(false);
            // New files are found by validating the physical folder items under the library (series folder or the
            // library's path itself); validating the CollectionFolder doesn't pick them up.
            await ScanAsync(NearestKnownFolder(path, libraryDir) ?? library, ct).ConfigureAwait(false);
            var item = await WaitForItemAsync(path, libraryDir, ct).ConfigureAwait(false);
            logger.LogInformation("TorrCatalog: persisted {Id} as {Path} (item {ItemId})", id, path, item.Id);
            return item.Id;
        }
        finally
        {
            _gate.Release();
        }
    }

    Task ScanAsync(Folder folder, CancellationToken ct) => folder.ValidateChildren(
        new Progress<double>(),
        new MetadataRefreshOptions(new DirectoryService(fileSystem)),
        recursive: true,
        cancellationToken: ct);

    /// <summary>
    /// A fresh file doesn't always show up after the first scan (seen right after the library was created, while
    /// Jellyfin's own scan was running, and for new episodes of a known series). Poll, and rescan the nearest
    /// folder Jellyfin already knows (series/season) once if the item is still missing.
    /// </summary>
    async Task<BaseItem> WaitForItemAsync(string path, string libraryDir, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var rescanned = false;
        while (true)
        {
            if (libraryManager.FindByPath(path, false) is { } item)
            {
                return item;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Jellyfin did not add {path} to the library.");
            }

            if (!rescanned && DateTime.UtcNow > deadline - TimeSpan.FromSeconds(25))
            {
                rescanned = true;
                if (NearestKnownFolder(path, libraryDir) is { } folder)
                {
                    await ScanAsync(folder, ct).ConfigureAwait(false);
                    continue;
                }
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }
    }

    Folder? NearestKnownFolder(string path, string libraryDir)
    {
        for (var dir = Path.GetDirectoryName(path); dir is not null && dir.Length >= libraryDir.Length; dir = Path.GetDirectoryName(dir))
        {
            if (libraryManager.FindByPath(dir, true) is Folder folder)
            {
                return folder;
            }
        }

        return null;
    }

    /// <summary>Checks the secret carried by .strm URLs (constant time).</summary>
    public bool KeyMatches(string? key)
    {
        var expected = Config().StreamKey;
        return expected.Length > 0 && key is not null
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(expected));
    }

    async Task<string> RelativePathAsync(VirtualId id, CancellationToken ct)
    {
        if (id.Kind == VirtualKind.Movie)
        {
            var movie = await catalog.GetMovieAsync(id.TmdbId, ct).ConfigureAwait(false);
            return MoviePath(movie.Title ?? movie.OriginalTitle ?? $"TMDB {movie.Id}", Year(movie.ReleaseDate), movie.Id);
        }

        var show = await catalog.GetShowAsync(id.TmdbId, ct).ConfigureAwait(false);
        return EpisodePath(show.Name ?? show.OriginalName ?? $"TMDB {show.Id}", Year(show.FirstAirDate), show.Id, id.Season, id.Episode);
    }

    /// <summary>Jellyfin's naming: the [tmdbid-N] tag makes it identify the item without guessing.</summary>
    internal static string MoviePath(string title, int? year, int tmdbId)
    {
        var name = Clean(year is null ? title : $"{title} ({year})");
        return Path.Combine($"{name} [tmdbid-{tmdbId}]", name + ".strm");
    }

    internal static string EpisodePath(string show, int? year, int tvId, int season, int episode)
    {
        var series = Clean(year is null ? show : $"{show} ({year})");
        return Path.Combine($"{series} [tmdbid-{tvId}]", $"Season {season:00}", $"{Clean(show)} S{season:00}E{episode:00}.strm");
    }

    // Titles like "Дюна: Часть вторая" carry characters that are invalid or awkward in file names.
    internal static string Clean(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(":*?\"<>|/\\").ToHashSet();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? ' ' : c).ToArray());
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ');
    }

    string StrmUrl(PluginConfiguration cfg, VirtualId id)
    {
        var baseUrl = cfg.StrmBaseUrl.Trim().TrimEnd('/') is { Length: > 0 } configured ? configured : $"http://localhost:{host.HttpPort}";
        return $"{baseUrl}/Plugins/TorrCatalog/Stream/{id.Base.ToGuid():N}?key={StreamKey(cfg)}";
    }

    static string StreamKey(PluginConfiguration cfg)
    {
        if (cfg.StreamKey.Length == 0)
        {
            cfg.StreamKey = RandomNumberGenerator.GetHexString(32, lowercase: true);
            Plugin.Instance?.SaveConfiguration();
        }

        return cfg.StreamKey;
    }

    async Task<Folder> EnsureLibraryAsync(PluginConfiguration cfg, string dir, bool movies, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        if (FindLibrary(dir) is not { } info)
        {
            var options = new LibraryOptions
            {
                PathInfos = [new MediaPathInfo(dir)],
                PreferredMetadataLanguage = cfg.Language.Split('-')[0],
                MetadataCountryCode = cfg.Region,
                EnableRealtimeMonitor = false,
            };
            await libraryManager.AddVirtualFolder(
                movies ? MoviesLibrary : ShowsLibrary,
                movies ? CollectionTypeOptions.movies : CollectionTypeOptions.tvshows,
                options,
                refreshLibrary: true).ConfigureAwait(false);
            info = FindLibrary(dir) ?? throw new InvalidOperationException($"Could not create the library for {dir}.");
            logger.LogInformation("TorrCatalog: created library '{Name}' at {Dir}", info.Name, dir);
        }

        var deadline = DateTime.UtcNow + LibraryWait;
        Folder? folder;
        while ((folder = libraryManager.GetItemById(Guid.Parse(info.ItemId)) as Folder) is null)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Jellyfin did not create the '{info.Name}' library in time.");
            }

            await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
        }

        return folder;
    }

    VirtualFolderInfo? FindLibrary(string dir) =>
        libraryManager.GetVirtualFolders().FirstOrDefault(f => f.Locations.Any(l => string.Equals(
            Path.TrimEndingDirectorySeparator(l), Path.TrimEndingDirectorySeparator(dir), StringComparison.Ordinal)));

    static int? Year(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;
}
