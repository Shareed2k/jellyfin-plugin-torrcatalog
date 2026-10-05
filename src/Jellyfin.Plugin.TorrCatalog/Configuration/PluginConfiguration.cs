using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.TorrCatalog.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the TMDB v3 API key or v4 read access token (starts with "eyJ").</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    public string Language { get; set; } = "ru-RU";

    public string Region { get; set; } = "IL";

    public bool IncludeAdult { get; set; }

    // Array, not List: XmlSerializer appends to an initialized List and would duplicate the defaults.
    public CatalogSection[] Sections { get; set; } = DefaultSections();

    /// <summary>Gets or sets the Prowlarr base URL, e.g. http://prowlarr:9696/.</summary>
    public string ProwlarrUrl { get; set; } = string.Empty;

    public string ProwlarrApiKey { get; set; } = string.Empty;

    public int SearchTimeoutSeconds { get; set; } = 30;

    /// <summary>Gets or sets the minimum seeders; releases below it are dropped.</summary>
    public int MinSeeders { get; set; } = 1;

    /// <summary>Gets or sets the maximum release size in GB; 0 means unlimited.</summary>
    public int MaxSizeGb { get; set; }

    /// <summary>Gets or sets the highest wanted resolution (2160, 1080, 720); higher releases are ranked down.</summary>
    public int MaxResolution { get; set; } = 2160;

    /// <summary>Gets or sets language codes in order of preference, e.g. "ru,en".</summary>
    public string PreferredLanguages { get; set; } = "ru,en";

    /// <summary>Gets or sets title tags per language: "ru=RUS,RU,DUB;en=ENG,EN".</summary>
    public string LanguageAliases { get; set; } = "ru=RUS,RU,Russian,DUB,MVO,DVO,AVO,Дубляж,Дублированный;en=ENG,EN,English";

    public static CatalogSection[] DefaultSections() =>
    [
        new() { Id = "trending", Name = "Trending", Path = "trending/movie/week" },
        new() { Id = "new", Name = "New Releases", Path = "movie/now_playing" },
        new() { Id = "popular", Name = "Popular", Path = "movie/popular" },
        new() { Id = "top", Name = "Top Rated", Path = "movie/top_rated", Enabled = false },
        new() { Id = "kids", Name = "Kids", Path = "discover/movie", Query = "sort_by=popularity.desc&with_genres=10751", Enabled = false },
    ];
}

/// <summary>A catalog row backed by one TMDB list endpoint. Exposed to clients as a virtual movie library.</summary>
public class CatalogSection
{
    /// <summary>Gets or sets the stable key; the virtual library ID is derived from it, so don't change it once used.</summary>
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the TMDB path relative to /3/, e.g. "trending/movie/week" or "discover/movie".</summary>
    public string Path { get; set; } = string.Empty;

    /// <summary>Gets or sets extra TMDB query parameters, e.g. "sort_by=popularity.desc&amp;with_genres=16".</summary>
    public string Query { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
