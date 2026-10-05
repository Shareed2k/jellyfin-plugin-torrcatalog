using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using MediaBrowser.Controller;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>TMDB-backed catalog: sections, movie details and search, mapped to Jellyfin DTOs and cached in memory.</summary>
public sealed partial class Catalog
{
    // ponytail: sections stop at 10 TMDB pages; raise if users actually scroll that far.
    public const int MaxSectionItems = 200;
    const int TmdbPageSize = 20;

    static readonly TimeSpan SectionTtl = TimeSpan.FromMinutes(30);
    static readonly TimeSpan DetailsTtl = TimeSpan.FromHours(24);
    static readonly TimeSpan SearchTtl = TimeSpan.FromMinutes(15);

    readonly TmdbClient _tmdb;
    readonly IMemoryCache _cache;
    readonly string _serverId;

    public Catalog(TmdbClient tmdb, IMemoryCache cache, IServerApplicationHost host)
        : this(tmdb, cache, host.SystemId)
    {
    }

    internal Catalog(TmdbClient tmdb, IMemoryCache cache, string serverId)
    {
        _tmdb = tmdb;
        _cache = cache;
        _serverId = serverId;
    }

    internal Func<PluginConfiguration> Config { get; init; } = () => Plugin.Instance?.Configuration ?? new PluginConfiguration();

    /// <summary>Gets the dev-only stand-in stream until TorrServer playback lands (Phase 4).</summary>
    // ponytail: env var instead of config UI; removed when real stream resolution exists.
    public static string? DevStreamUrl => Environment.GetEnvironmentVariable("TORRCATALOG_DEV_STREAM_URL") is { Length: > 0 } u ? u : null;

    // No key, no sections: better than showing libraries that only return errors.
    public IEnumerable<CatalogSection> Sections => string.IsNullOrWhiteSpace(Config().TmdbApiKey)
        ? []
        : Config().Sections.Where(s => s.Enabled && s.Id.Length > 0);

    public CatalogSection? FindSection(VirtualId id) => Sections.FirstOrDefault(s => VirtualId.Section(s.Id) == id);

    public async Task<(BaseItemDto[] Items, int Total)> GetSectionItemsAsync(CatalogSection section, int start, int limit, CancellationToken ct)
    {
        start = Math.Max(0, start);
        var end = Math.Min(start + Math.Max(0, limit), MaxSectionItems);
        var firstPage = (start / TmdbPageSize) + 1;
        var movies = new List<TmdbMovie>();
        var total = 0;

        for (var page = firstPage; (page - 1) * TmdbPageSize < Math.Max(end, start + 1); page++)
        {
            var p = await GetPageAsync(section, page, ct).ConfigureAwait(false);
            total = Math.Min(p.TotalResults, MaxSectionItems);
            movies.AddRange(p.Results);
            if (page >= p.TotalPages)
            {
                break;
            }
        }

        var items = movies.Skip(start - ((firstPage - 1) * TmdbPageSize)).Take(Math.Max(0, end - start)).Select(ToDto).ToArray();
        return (items, total);
    }

    public async Task<TmdbMovie> GetMovieAsync(int tmdbId, CancellationToken ct)
    {
        var cfg = Config();
        return await CachedAsync($"movie:{cfg.Language}:{tmdbId}", DetailsTtl,
            () => _tmdb.GetAsync<TmdbMovie>(cfg, $"movie/{tmdbId}", string.Empty, CancellationToken.None), ct).ConfigureAwait(false);
    }

    public async Task<BaseItemDto[]> SearchAsync(string term, int limit, CancellationToken ct)
    {
        var cfg = Config();
        term = term.Trim();
        var page = await CachedAsync($"search:{cfg.Language}:{term.ToLowerInvariant()}", SearchTtl,
            () => _tmdb.GetAsync<TmdbPage>(cfg, "search/movie", "query=" + Uri.EscapeDataString(term), CancellationToken.None), ct).ConfigureAwait(false);
        return page.Results.Take(limit).Select(ToDto).ToArray();
    }

    public Task PingAsync(CancellationToken ct) => _tmdb.GetAsync<JsonElement>(Config(), "configuration", string.Empty, ct);

    Task<TmdbPage> GetPageAsync(CatalogSection section, int page, CancellationToken ct)
    {
        var cfg = Config();
        var query = section.Query.Length > 0 ? $"{section.Query}&page={page}" : $"page={page}";
        return CachedAsync($"section:{cfg.Language}:{cfg.Region}:{cfg.IncludeAdult}:{section.Path}?{query}", SectionTtl,
            () => _tmdb.GetAsync<TmdbPage>(cfg, section.Path, query, CancellationToken.None), ct);
    }

    // Caches the in-flight task, so concurrent callers share one TMDB request; failures are evicted.
    // The fetch itself ignores the caller's token (shared by others); callers stop waiting via WaitAsync.
    // ponytail: GetOrCreate isn't atomic, a rare race can issue a duplicate request. Keyed lock if that matters.
    async Task<T> CachedAsync<T>(string key, TimeSpan ttl, Func<Task<T>> fetch, CancellationToken ct)
    {
        var lazy = _cache.GetOrCreate(key, e =>
        {
            e.AbsoluteExpirationRelativeToNow = ttl;
            return new Lazy<Task<T>>(fetch);
        })!;

        try
        {
            return await lazy.Value.WaitAsync(ct).ConfigureAwait(false);
        }
        catch when (lazy.Value.IsFaulted || lazy.Value.IsCanceled)
        {
            _cache.Remove(key);
            throw;
        }
    }

    public BaseItemDto ToDto(TmdbMovie m)
    {
        var id = VirtualId.Movie(m.Id).ToGuid();
        DateTime? released = DateTime.TryParseExact(m.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;

        var providerIds = new Dictionary<string, string> { ["Tmdb"] = m.Id.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(m.ImdbId))
        {
            providerIds["Imdb"] = m.ImdbId;
        }

        var imageTags = new Dictionary<ImageType, string>();
        if (ImageTag(m.PosterPath) is { } poster)
        {
            imageTags[ImageType.Primary] = poster;
        }

        return new BaseItemDto
        {
            Id = id,
            ServerId = _serverId,
            Name = m.Title ?? m.OriginalTitle ?? $"TMDB {m.Id}",
            OriginalTitle = m.OriginalTitle,
            Type = BaseItemKind.Movie,
            MediaType = MediaType.Video,
            IsFolder = false,
            LocationType = LocationType.FileSystem,
            VideoType = VideoType.VideoFile,
            Overview = m.Overview,
            Taglines = string.IsNullOrEmpty(m.Tagline) ? [] : [m.Tagline],
            ProductionYear = released?.Year,
            PremiereDate = released,
            CommunityRating = m.VoteCount > 0 ? (float)Math.Round(m.VoteAverage, 1) : null,
            RunTimeTicks = m.Runtime > 0 ? TimeSpan.FromMinutes(m.Runtime.Value).Ticks : null,
            Genres = m.Genres?.Select(g => g.Name).ToArray() ?? [],
            ProviderIds = providerIds,
            ImageTags = imageTags,
            BackdropImageTags = ImageTag(m.BackdropPath) is { } backdrop ? [backdrop] : [],
            PrimaryImageAspectRatio = 2.0 / 3.0,
            CanDownload = false,
            UserData = UserData(id),
        };
    }

    public BaseItemDto ToDto(CatalogSection s)
    {
        var id = VirtualId.Section(s.Id).ToGuid();
        return new BaseItemDto
        {
            Id = id,
            ServerId = _serverId,
            Name = s.Name,
            Type = BaseItemKind.CollectionFolder,
            CollectionType = CollectionType.movies,
            IsFolder = true,
            LocationType = LocationType.FileSystem,
            DisplayPreferencesId = id.ToString("N"),
            ImageTags = new Dictionary<ImageType, string>(),
            BackdropImageTags = [],
            UserData = UserData(id),
        };
    }

    public static UserItemDataDto UserData(Guid id) => new() { Key = id.ToString("N"), ItemId = id };

    /// <summary>Builds the playable source. Protocol=File on purpose: every client then requests
    /// /Videos/{id}/stream, which the filter redirects. Protocol=Http breaks jellyfin-android (forces HLS).</summary>
    public static MediaSourceInfo MediaSource(Guid id, TmdbMovie m, string url) => new()
    {
        Id = id.ToString("N"),
        Name = m.Title,
        Path = url,
        Protocol = MediaProtocol.File,
        Type = MediaSourceType.Default,
        Container = "mp4",
        IsRemote = false,
        SupportsDirectPlay = true,
        SupportsDirectStream = false,
        SupportsTranscoding = false,
        SupportsProbing = false,
        RunTimeTicks = m.Runtime > 0 ? TimeSpan.FromMinutes(m.Runtime.Value).Ticks : null,
        // ponytail: dev stream codecs are known (h264/aac test mp4); real values come from TorrServer in Phase 4.
        MediaStreams =
        [
            new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264", Width = 1920, Height = 1080, IsDefault = true },
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
        ],
    };

    /// <summary>TMDB image path ("/abc.jpg") doubles as the Jellyfin image tag, so image requests need no TMDB lookup.</summary>
    public static string? ImageTag(string? tmdbPath) => tmdbPath?.TrimStart('/') is { Length: > 0 } t && SafeImageTag().IsMatch(t) ? t : null;

    public static string? ImageUrl(ImageType type, string? tag)
    {
        if (tag is null || !SafeImageTag().IsMatch(tag))
        {
            return null;
        }

        return type switch
        {
            ImageType.Primary => "https://image.tmdb.org/t/p/w500/" + tag,
            ImageType.Backdrop => "https://image.tmdb.org/t/p/w1280/" + tag,
            _ => null,
        };
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.(jpg|jpeg|png|webp|svg)$")]
    private static partial Regex SafeImageTag();
}
