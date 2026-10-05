using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Web;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class ReleaseServiceTests
{
    readonly FakeHttp _http = new();
    readonly PluginConfiguration _config = new()
    {
        TmdbApiKey = "tmdbkey",
        ProwlarrUrl = "http://prowlarr.test:9696",
        ProwlarrApiKey = "prowlarrkey",
    };

    readonly ReleaseService _service;

    public ReleaseServiceTests()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new Catalog(new TmdbClient(_http), cache, "server1") { Config = () => _config };
        _service = new ReleaseService(catalog, new ProwlarrClient(_http), cache, NullLogger<ReleaseService>.Instance) { Config = () => _config };
    }

    CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SearchesByIdAndTitlesMergesAndRanks()
    {
        _http.Respond = (r, _) => r.RequestUri!.Host switch
        {
            "api.themoviedb.org" => Json(new { Id = 693134, Title = "Дюна: Часть вторая", OriginalTitle = "Dune: Part Two", ReleaseDate = "2024-02-27", ImdbId = "tt15239678" }),
            _ => HttpUtility.ParseQueryString(r.RequestUri.Query)["type"] == "movie"
                ? Prowlarr(new[]
                {
                    Release("a", "Dune.Part.Two.2024.1080p.WEB-DL.x264", seeders: 300, hash: "AAA"),
                    Release("b", "Dune.Part.Two.2024.2160p.WEB-DL.DV.HEVC", seeders: 40, hash: "BBB"),
                })
                : Prowlarr(new[]
                {
                    Release("a2", "Dune.Part.Two.2024.1080p.WEB-DL.x264", seeders: 300, hash: "aaa"), // same torrent, other indexer
                    Release("c", "Dune.Part.Two.2024.CAM", seeders: 900, hash: "CCC"),
                    Release("d", "Dune.Part.Two.2024.1080p.WEB-DL", seeders: 0, hash: "DDD"), // below MinSeeders
                    Release("e", "Dune.Part.Two.2024.1080p.WEB-DL", seeders: 99, hash: "EEE", protocol: "usenet"),
                }),
        };

        var ranked = await _service.SearchMovieAsync(693134, Ct);

        Assert.Equal(["Dune.Part.Two.2024.2160p.WEB-DL.DV.HEVC", "Dune.Part.Two.2024.1080p.WEB-DL.x264", "Dune.Part.Two.2024.CAM"], ranked.Select(r => r.Source.Title));

        var prowlarrCalls = _http.Requests.Where(r => r.RequestUri!.Host == "prowlarr.test").ToArray();
        Assert.Equal(3, prowlarrCalls.Length); // ID search + localized title + original title
        var queries = prowlarrCalls.Select(r => HttpUtility.ParseQueryString(r.RequestUri!.Query)["query"]).ToArray();
        Assert.Contains("{ImdbId:tt15239678}{TmdbId:693134}", queries);
        Assert.Contains("Дюна: Часть вторая 2024", queries);
        Assert.Contains("Dune: Part Two 2024", queries);
        Assert.All(prowlarrCalls, r =>
        {
            Assert.Equal("prowlarrkey", r.Headers.GetValues("X-Api-Key").Single());
            Assert.DoesNotContain("prowlarrkey", r.RequestUri!.ToString(), StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ClientDtoCarriesNoUrls()
    {
        _http.Respond = (r, _) => r.RequestUri!.Host == "api.themoviedb.org"
            ? Json(new { Id = 1, Title = "X", ReleaseDate = "2026-01-01" })
            : Prowlarr(new[] { Release("a", "X.2026.1080p.WEB-DL", seeders: 10, hash: "AAA") });

        var dto = (await _service.SearchMovieAsync(1, Ct)).Single().ToDto();
        var json = JsonSerializer.Serialize(dto);

        Assert.DoesNotContain("apikey", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("magnet:", json, StringComparison.Ordinal);
        Assert.DoesNotContain("http", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OneFailingStrategyDoesNotHideOthers()
    {
        _http.Respond = (r, _) => r.RequestUri!.Host == "api.themoviedb.org"
            ? Json(new { Id = 1, Title = "X", OriginalTitle = "Y", ReleaseDate = "2026-01-01" })
            : r.RequestUri.Query.Contains("query=X", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : Prowlarr(new[] { Release("a", "Y.2026.1080p.WEB-DL", seeders: 10, hash: "AAA") });

        Assert.Single(await _service.SearchMovieAsync(1, Ct));
    }

    [Theory]
    [InlineData("")]
    [InlineData("ftp://prowlarr:9696")]
    [InlineData("http://user:pass@prowlarr:9696")]
    public async Task InvalidProwlarrUrlIsRejected(string url)
    {
        _config.ProwlarrUrl = url;
        _http.Respond = (_, _) => Json(new { Id = 1, Title = "X", ReleaseDate = "2026-01-01" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => _service.SearchMovieAsync(1, Ct));
        Assert.DoesNotContain(_http.Requests, r => r.RequestUri!.Host != "api.themoviedb.org");
    }

    static object Release(string guid, string title, int seeders, string hash, string protocol = "torrent") => new
    {
        guid,
        title,
        size = 5_000_000_000L,
        indexer = "test",
        protocol,
        seeders,
        leechers = 1,
        magnetUrl = $"magnet:?xt=urn:btih:{hash}",
        downloadUrl = $"http://prowlarr.test:9696/1/download?apikey=prowlarrkey&link={guid}",
        infoHash = hash,
        publishDate = "2026-01-01T00:00:00Z",
    };

    // TMDB speaks snake_case; Prowlarr camelCase (the anonymous fixtures above are already camelCase).
    static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = JsonContent.Create(body, options: new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower }) };

    static HttpResponseMessage Prowlarr(object[] releases) => new(HttpStatusCode.OK) { Content = JsonContent.Create(releases) };
}
