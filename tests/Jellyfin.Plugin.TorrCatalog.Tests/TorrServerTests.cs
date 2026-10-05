using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.TorrCatalog.Tests;

public class TorrServerTests
{
    const string HashA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string HashB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    readonly FakeHttp _http = new();
    readonly PluginConfiguration _config = new()
    {
        TmdbApiKey = "tmdbkey",
        ProwlarrUrl = "http://prowlarr.test/",
        ProwlarrApiKey = "prowlarrkey",
        TorrServerUrl = "http://torrserver.test:8090",
        MetadataTimeoutSeconds = 5,
    };

    // Torrent hash → files TorrServer reports once metadata is "ready".
    readonly Dictionary<string, object[]> _torrents = [];

    CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void PicksLargestVideoAndSkipsJunk()
    {
        var files = new[]
        {
            new TorrentFile(1, "Movie.2024.with.Extras/Extras/Making of.mkv", 900),
            new TorrentFile(2, "Movie.2024.with.Extras/Movie.2024.mkv", 800),
            new TorrentFile(3, "Movie.2024.with.Extras/Sample/sample.mkv", 950),
            new TorrentFile(4, "Movie.2024.with.Extras/Movie.2024.rus.srt", 5000),
            new TorrentFile(5, "Movie.2024.with.Extras/Movie-trailer.mp4", 990),
        };

        Assert.Equal(2, FileSelector.Movie(files)!.Id);
        Assert.Null(FileSelector.Movie([new TorrentFile(1, "Album/track.flac", 10)]));
        Assert.Equal("mkv", FileSelector.Container("x/Movie.MKV"));
        Assert.Equal("ts", FileSelector.Container("BDMV/STREAM/00001.m2ts"));
    }

    [Fact]
    public void StreamUrlUsesFileIdAndEscapedName()
    {
        var url = TorrServerClient.StreamUrl(_config, HashA.ToUpperInvariant(), new TorrentFile(6, "Sintel/Sintel Movie.mp4", 1));

        Assert.Equal($"http://torrserver.test:8090/stream/Sintel%20Movie.mp4?link={HashA}&index=6&play", url);
    }

    [Fact]
    public async Task WaitsForMetadataThenReturnsFiles()
    {
        var polls = 0;
        _http.Respond = (_, body) => body!.Contains("\"get\"", StringComparison.Ordinal) && ++polls < 3
            ? FakeHttp.Snake(new { Hash = HashA, Stat = 1 })
            : FakeHttp.Snake(new { Hash = HashA, Stat = 3, FileStats = new[] { new { Id = 1, Path = "a.mkv", Length = 10 } } });

        var status = await Client().WaitForFilesAsync(_config, HashA, TimeSpan.FromSeconds(5), Ct);

        Assert.Equal(3, polls);
        Assert.Single(status.FileStats!);
    }

    [Fact]
    public async Task ClosedTorrentFailsFast()
    {
        _http.Respond = (_, _) => FakeHttp.Snake(new { Hash = HashA, Stat = 4 });

        await Assert.ThrowsAsync<TimeoutException>(() => Client().WaitForFilesAsync(_config, HashA, TimeSpan.FromSeconds(30), Ct));
    }

    [Fact]
    public async Task SendsBasicAuthWhenConfigured()
    {
        _config.TorrServerUsername = "user";
        _config.TorrServerPassword = "pass";
        _http.Respond = (_, _) => FakeHttp.Snake(new { Hash = HashA, Stat = 0 });

        await Client().AddAsync(_config, "magnet:?xt=urn:btih:" + HashA, "Movie", null, Ct);

        Assert.Equal("Basic dXNlcjpwYXNz", _http.Requests.Single().Headers.Authorization!.ToString());
        Assert.Contains("\"action\":\"add\"", _http.Bodies.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedHashIsNeverSent()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => Client().GetAsync(_config, "../settings", Ct));
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task ResolverFallsBackToNextReleaseAndCaches()
    {
        // Best release has no video inside; the second one does.
        _torrents[HashA] = [new { Id = 1, Path = "Best/readme.txt", Length = 10 }];
        _torrents[HashB] = [new { Id = 1, Path = "Second/sample.mkv", Length = 5 }, new { Id = 2, Path = "Second/Movie.2024.mkv", Length = 9_000 }];
        _http.Respond = Upstreams(
            Release(HashA, "Movie.2024.2160p.WEB-DL.HEVC", seeders: 100),
            Release(HashB, "Movie.2024.1080p.WEB-DL.x264", seeders: 100));
        var resolver = Resolver();

        var stream = await resolver.ResolveMovieAsync(42, Ct);
        await resolver.ResolveMovieAsync(42, Ct);

        Assert.Equal($"http://torrserver.test:8090/stream/Movie.2024.mkv?link={HashB}&index=2&play", stream.Url);
        Assert.Equal("mkv", stream.Container);
        Assert.Equal(9_000, stream.Size);
        Assert.Equal(1, Count("\"drop\"")); // failed release was dropped from TorrServer
        Assert.Equal(2, Count("\"add\"")); // second resolve came from cache
    }

    [Fact]
    public async Task ResolverRefusesCamOnlyResults()
    {
        _http.Respond = Upstreams(Release(HashA, "Movie.2024.CAM", seeders: 900));

        var ex = await Assert.ThrowsAsync<StreamNotFoundException>(() => Resolver().ResolveMovieAsync(42, Ct));

        Assert.Contains("quality", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, Count("\"add\""));
    }

    [Fact]
    public void MediaSourceUsesProbeAndPreferredAudio()
    {
        var probe = new MediaInfo
        {
            RunTimeTicks = 123,
            MediaStreams =
            [
                new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "hevc" },
                new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "eac3", Language = "eng", IsDefault = true },
                new MediaStream { Index = 2, Type = MediaStreamType.Audio, Codec = "ac3", Language = "rus" },
                new MediaStream { Index = 3, Type = MediaStreamType.Subtitle, Codec = "subrip", Language = "rus" },
            ],
        };
        var stream = new ResolvedStream("http://ts/stream/x.mkv?link=a&index=1&play", "mkv", 10, "Release", probe);

        var source = Catalog.MediaSource(Guid.NewGuid(), new TmdbMovie(1, "X", null, null, null, null, "2024-01-01", 0, 0), stream, ["ru", "en"]);

        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Equal("mkv", source.Container);
        Assert.Equal(123, source.RunTimeTicks);
        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.DoesNotContain(source.MediaStreams, s => s.Type == MediaStreamType.Subtitle);
        Assert.False(source.SupportsTranscoding);
    }

    TorrServerClient Client() => new(_http) { PollInterval = TimeSpan.Zero };

    StreamResolver Resolver()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var catalog = new Catalog(new TmdbClient(_http), cache, "server1") { Config = () => _config };
        var releases = new ReleaseService(catalog, new ProwlarrClient(_http), cache, NullLogger<ReleaseService>.Instance) { Config = () => _config };
        return new StreamResolver(catalog, releases, Client(), null!, cache, NullLogger<StreamResolver>.Instance)
        {
            Config = () => _config,
            ProbeOverride = (_, _) => Task.FromResult<MediaInfo?>(null),
        };
    }

    int Count(string action) => _http.Bodies.Count(b => b.Contains(action, StringComparison.Ordinal));

    Func<HttpRequestMessage, string?, HttpResponseMessage> Upstreams(params object[] releases) => (request, body) =>
    {
        switch (request.RequestUri!.Host)
        {
            case "api.themoviedb.org":
                return FakeHttp.Snake(new { Id = 42, Title = "Movie", ReleaseDate = "2024-05-01" });
            case "prowlarr.test":
                return FakeHttp.Camel(releases);
        }

        using var doc = JsonDocument.Parse(body!);
        var action = doc.RootElement.GetProperty("action").GetString();
        var hash = action == "add"
            ? doc.RootElement.GetProperty("link").GetString()![^40..]
            : doc.RootElement.GetProperty("hash").GetString()!;
        return action == "drop"
            ? new HttpResponseMessage(HttpStatusCode.OK)
            : FakeHttp.Snake(new { Hash = hash, Stat = 3, FileStats = action == "add" ? null : _torrents[hash] });
    };

    static object Release(string hash, string title, int seeders) => new
    {
        guid = hash,
        title,
        size = 5_000_000_000L,
        protocol = "torrent",
        seeders,
        magnetUrl = $"magnet:?xt=urn:btih:{hash}",
        infoHash = hash,
    };
}
