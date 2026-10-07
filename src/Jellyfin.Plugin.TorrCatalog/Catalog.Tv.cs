using System.Globalization;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TorrCatalog;

/// <summary>TV: series → seasons → episodes, each with its own virtual id.</summary>
public sealed partial class Catalog
{
    // Seasons change while a show airs; keep them fresher than movie details.
    static readonly TimeSpan SeasonTtl = TimeSpan.FromHours(6);

    public async Task<TmdbShow> GetShowAsync(int tvId, CancellationToken ct)
    {
        var cfg = Config();
        return await _cache.GetOrFetchAsync($"tv:{cfg.Language}:{tvId}", DetailsTtl,
            () => _tmdb.GetAsync<TmdbShow>(cfg, $"tv/{tvId}", "append_to_response=external_ids", CancellationToken.None), ct).ConfigureAwait(false);
    }

    public async Task<TmdbSeason> GetSeasonAsync(int tvId, int season, CancellationToken ct)
    {
        var cfg = Config();
        return await _cache.GetOrFetchAsync($"tv:{cfg.Language}:{tvId}:season:{season}", SeasonTtl,
            () => _tmdb.GetAsync<TmdbSeason>(cfg, $"tv/{tvId}/season/{season}", string.Empty, CancellationToken.None), ct).ConfigureAwait(false);
    }

    public async Task<(TmdbShow Show, TmdbEpisode Episode)> GetEpisodeAsync(int tvId, int season, int episode, CancellationToken ct)
    {
        var show = await GetShowAsync(tvId, ct).ConfigureAwait(false);
        var s = await GetSeasonAsync(tvId, season, ct).ConfigureAwait(false);
        return (show, s.Episodes.FirstOrDefault(e => e.EpisodeNumber == episode)
            ?? throw new InvalidOperationException($"TMDB has no S{season:00}E{episode:00} for show {tvId}"));
    }

    /// <summary>Seasons worth listing: with episodes, specials (season 0) last.</summary>
    public static IEnumerable<TmdbSeasonInfo> Seasons(TmdbShow show) =>
        (show.Seasons ?? []).Where(s => s.EpisodeCount > 0).OrderBy(s => s.SeasonNumber == 0 ? int.MaxValue : s.SeasonNumber);

    public BaseItemDto ToDto(TmdbShow show)
    {
        var id = VirtualId.Series(show.Id).ToGuid();
        var firstAired = Date(show.FirstAirDate);
        var providerIds = new Dictionary<string, string> { ["Tmdb"] = show.Id.ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(show.ExternalIds?.ImdbId))
        {
            providerIds["Imdb"] = show.ExternalIds.ImdbId;
        }

        if (show.ExternalIds?.TvdbId is { } tvdb)
        {
            providerIds["Tvdb"] = tvdb.ToString(CultureInfo.InvariantCulture);
        }

        return new BaseItemDto
        {
            Id = id,
            ServerId = _serverId,
            Name = show.Name ?? show.OriginalName ?? $"TMDB {show.Id}",
            OriginalTitle = show.OriginalName,
            Type = BaseItemKind.Series,
            IsFolder = true,
            LocationType = LocationType.FileSystem,
            Overview = show.Overview,
            ProductionYear = firstAired?.Year,
            PremiereDate = firstAired,
            CommunityRating = show.VoteCount > 0 ? (float)Math.Round(show.VoteAverage, 1) : null,
            RunTimeTicks = show.EpisodeRunTime is [var minutes, ..] && minutes > 0 ? TimeSpan.FromMinutes(minutes).Ticks : null,
            Genres = show.Genres?.Select(g => g.Name).ToArray() ?? [],
            ProviderIds = providerIds,
            ChildCount = show.Seasons is null ? null : Seasons(show).Count(),
            ImageTags = Tags(show.PosterPath),
            BackdropImageTags = ImageTag(show.BackdropPath) is { } backdrop ? [backdrop] : [],
            PrimaryImageAspectRatio = 2.0 / 3.0,
            DisplayPreferencesId = id.ToString("N"),
            UserData = UserData(id),
        };
    }

    public BaseItemDto ToDto(TmdbShow show, TmdbSeasonInfo season)
    {
        var seriesId = VirtualId.Series(show.Id).ToGuid();
        var id = VirtualId.SeasonOf(show.Id, season.SeasonNumber).ToGuid();
        var aired = Date(season.AirDate);
        return new BaseItemDto
        {
            Id = id,
            ServerId = _serverId,
            Name = season.Name ?? $"Season {season.SeasonNumber}",
            Type = BaseItemKind.Season,
            IsFolder = true,
            LocationType = LocationType.FileSystem,
            IndexNumber = season.SeasonNumber,
            ParentId = seriesId,
            SeriesId = seriesId,
            SeriesName = show.Name,
            SeriesPrimaryImageTag = ImageTag(show.PosterPath),
            ParentBackdropItemId = seriesId,
            ParentBackdropImageTags = ImageTag(show.BackdropPath) is { } backdrop ? [backdrop] : [],
            ChildCount = season.EpisodeCount,
            ProductionYear = aired?.Year,
            PremiereDate = aired,
            ImageTags = Tags(season.PosterPath ?? show.PosterPath),
            BackdropImageTags = [],
            PrimaryImageAspectRatio = 2.0 / 3.0,
            DisplayPreferencesId = id.ToString("N"),
            UserData = UserData(id),
        };
    }

    public BaseItemDto ToDto(TmdbShow show, TmdbEpisode episode)
    {
        var seriesId = VirtualId.Series(show.Id).ToGuid();
        var seasonId = VirtualId.SeasonOf(show.Id, episode.SeasonNumber).ToGuid();
        var id = VirtualId.EpisodeOf(show.Id, episode.SeasonNumber, episode.EpisodeNumber).ToGuid();
        var aired = Date(episode.AirDate);
        var runtime = episode.Runtime ?? (show.EpisodeRunTime is [var m, ..] ? m : null);
        return new BaseItemDto
        {
            Id = id,
            ServerId = _serverId,
            Name = episode.Name ?? $"Episode {episode.EpisodeNumber}",
            Type = BaseItemKind.Episode,
            MediaType = MediaType.Video,
            IsFolder = false,
            // Unaired episodes show as missing (no Play button) instead of failing on Play.
            LocationType = aired is { } a && a <= DateTime.UtcNow ? LocationType.FileSystem : LocationType.Virtual,
            VideoType = VideoType.VideoFile,
            Overview = episode.Overview,
            IndexNumber = episode.EpisodeNumber,
            ParentIndexNumber = episode.SeasonNumber,
            ParentId = seasonId,
            SeriesId = seriesId,
            SeriesName = show.Name,
            SeasonId = seasonId,
            SeasonName = Seasons(show).FirstOrDefault(s => s.SeasonNumber == episode.SeasonNumber)?.Name,
            SeriesPrimaryImageTag = ImageTag(show.PosterPath),
            ParentBackdropItemId = seriesId,
            ParentBackdropImageTags = ImageTag(show.BackdropPath) is { } backdrop ? [backdrop] : [],
            PremiereDate = aired,
            ProductionYear = aired?.Year,
            RunTimeTicks = runtime > 0 ? TimeSpan.FromMinutes(runtime.Value).Ticks : null,
            CommunityRating = episode.VoteAverage > 0 ? (float)Math.Round(episode.VoteAverage, 1) : null,
            ImageTags = Tags(episode.StillPath),
            BackdropImageTags = [],
            PrimaryImageAspectRatio = 16.0 / 9.0,
            CanDownload = false,
            UserData = UserData(id),
        };
    }

    static Dictionary<ImageType, string> Tags(string? primaryPath) =>
        ImageTag(primaryPath) is { } tag ? new() { [ImageType.Primary] = tag } : new();
}
