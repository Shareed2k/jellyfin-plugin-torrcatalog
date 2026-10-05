using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class CatalogTests
{
    static readonly JsonSerializerOptions Snake = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

    readonly FakeTmdb _tmdb = new();
    readonly PluginConfiguration _config = new() { TmdbApiKey = "v3key" };
    readonly CatalogSection _trending = new() { Id = "trending", Name = "Trending", Path = "trending/movie/week" };
    readonly Catalog _catalog;

    public CatalogTests()
    {
        _catalog = new Catalog(new TmdbClient(_tmdb), new MemoryCache(new MemoryCacheOptions()), "server1") { Config = () => _config };
    }

    CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SectionPagingFetchesOnlyTheNeededTmdbPages()
    {
        _tmdb.Respond = r => PageOf(r, totalResults: 95);

        var (items, total) = await _catalog.GetSectionItemsAsync(_trending, start: 15, limit: 10, Ct);

        Assert.Equal(95, total);
        Assert.Equal(Enumerable.Range(16, 10).Select(i => VirtualId.Movie(i).ToGuid()), items.Select(i => i.Id));
        Assert.Equal(["1", "2"], _tmdb.Requests.Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["page"]));
    }

    [Fact]
    public async Task SectionTotalIsCapped()
    {
        _tmdb.Respond = r => PageOf(r, totalResults: 5000);

        var (_, total) = await _catalog.GetSectionItemsAsync(_trending, 0, 20, Ct);

        Assert.Equal(Catalog.MaxSectionItems, total);
    }

    [Fact]
    public async Task SectionPagesAreCached()
    {
        _tmdb.Respond = r => PageOf(r, totalResults: 40);

        await _catalog.GetSectionItemsAsync(_trending, 0, 20, Ct);
        await _catalog.GetSectionItemsAsync(_trending, 0, 20, Ct);

        Assert.Single(_tmdb.Requests);
    }

    [Fact]
    public async Task FailedFetchIsNotCached()
    {
        var calls = 0;
        _tmdb.Respond = r => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : PageOf(r, totalResults: 20);

        await Assert.ThrowsAsync<HttpRequestException>(() => _catalog.GetSectionItemsAsync(_trending, 0, 20, Ct));
        var (items, _) = await _catalog.GetSectionItemsAsync(_trending, 0, 20, Ct);

        Assert.Equal(20, items.Length);
    }

    [Fact]
    public async Task MovieDetailsMapToBaseItemDto()
    {
        _tmdb.Respond = _ => Json(new
        {
            Id = 693134,
            Title = "Дюна: Часть вторая",
            OriginalTitle = "Dune: Part Two",
            Overview = "Paul Atreides unites with the Fremen.",
            ReleaseDate = "2024-02-27",
            VoteAverage = 8.153,
            VoteCount = 6000,
            Runtime = 167,
            PosterPath = "/poster.jpg",
            BackdropPath = "/backdrop.jpg",
            ImdbId = "tt15239678",
            Genres = new[] { new { Id = 878, Name = "фантастика" } },
        });

        var dto = _catalog.ToDto(await _catalog.GetMovieAsync(693134, Ct));

        Assert.Equal(VirtualId.Movie(693134).ToGuid(), dto.Id);
        Assert.Equal("server1", dto.ServerId);
        Assert.Equal("Дюна: Часть вторая", dto.Name);
        Assert.Equal(2024, dto.ProductionYear);
        Assert.Equal(8.2f, dto.CommunityRating);
        Assert.Equal(TimeSpan.FromMinutes(167).Ticks, dto.RunTimeTicks);
        Assert.Equal("poster.jpg", dto.ImageTags[ImageType.Primary]);
        Assert.Equal(["backdrop.jpg"], dto.BackdropImageTags);
        Assert.Equal("693134", dto.ProviderIds["Tmdb"]);
        Assert.Equal("tt15239678", dto.ProviderIds["Imdb"]);
        Assert.Equal(["фантастика"], dto.Genres);
    }

    [Fact]
    public void EmptyReleaseDateLeavesYearUnset()
    {
        var dto = _catalog.ToDto(new TmdbMovie(1, "X", null, null, null, null, ReleaseDate: "", VoteAverage: 0, VoteCount: 0));

        Assert.Null(dto.ProductionYear);
        Assert.Null(dto.CommunityRating);
        Assert.Empty(dto.ImageTags);
    }

    [Fact]
    public async Task V3KeyGoesInQueryV4TokenInHeader()
    {
        _tmdb.Respond = r => PageOf(r, totalResults: 1);

        await _catalog.GetSectionItemsAsync(_trending, 0, 1, Ct);
        _config.TmdbApiKey = "eyJv4token";
        _config.Language = "en-US"; // new cache key
        await _catalog.GetSectionItemsAsync(_trending, 0, 1, Ct);

        Assert.Contains("api_key=v3key", _tmdb.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Null(_tmdb.Requests[0].Headers.Authorization);
        Assert.DoesNotContain("api_key", _tmdb.Requests[1].RequestUri!.Query, StringComparison.Ordinal);
        Assert.Equal("Bearer eyJv4token", _tmdb.Requests[1].Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task ErrorsDoNotLeakTheKey()
    {
        _tmdb.Respond = _ => new HttpResponseMessage(HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => _catalog.GetMovieAsync(1, Ct));

        Assert.DoesNotContain("v3key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingKeyFailsWithoutCallingTmdb()
    {
        _config.TmdbApiKey = " ";

        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.GetMovieAsync(1, Ct));
        Assert.Empty(_tmdb.Requests);
    }

    [Theory]
    [InlineData("../movie/popular")]
    [InlineData("https://evil.example/x")]
    [InlineData("movie/popular?api_key=x")]
    public async Task UnsafeSectionPathIsRejected(string path)
    {
        var section = new CatalogSection { Id = "bad", Name = "Bad", Path = path };

        await Assert.ThrowsAsync<InvalidOperationException>(() => _catalog.GetSectionItemsAsync(section, 0, 20, Ct));
        Assert.Empty(_tmdb.Requests);
    }

    [Fact]
    public void ImageUrlsOnlyPointAtTmdb()
    {
        Assert.Equal("https://image.tmdb.org/t/p/w500/abc.jpg", Catalog.ImageUrl(ImageType.Primary, "abc.jpg"));
        Assert.Equal("https://image.tmdb.org/t/p/w1280/abc.jpg", Catalog.ImageUrl(ImageType.Backdrop, "abc.jpg"));
        Assert.Null(Catalog.ImageUrl(ImageType.Primary, "../abc.jpg"));
        Assert.Null(Catalog.ImageUrl(ImageType.Primary, "evil.example/abc.jpg"));
        Assert.Null(Catalog.ImageUrl(ImageType.Logo, "abc.jpg"));
    }

    static HttpResponseMessage PageOf(HttpRequestMessage request, int totalResults)
    {
        var page = int.Parse(HttpUtility.ParseQueryString(request.RequestUri!.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
        var first = ((page - 1) * 20) + 1;
        var ids = Enumerable.Range(first, Math.Max(0, Math.Min(20, totalResults - first + 1)));
        return Json(new
        {
            Page = page,
            TotalPages = (totalResults + 19) / 20,
            TotalResults = totalResults,
            Results = ids.Select(id => new { Id = id, Title = $"Movie {id}", ReleaseDate = "2026-01-01", VoteAverage = 7.0, VoteCount = 10 }),
        });
    }

    static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: Snake) };

    sealed class FakeTmdb : HttpMessageHandler, IHttpClientFactory
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = TmdbClient.BaseAddress };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Respond(request));
        }
    }
}
