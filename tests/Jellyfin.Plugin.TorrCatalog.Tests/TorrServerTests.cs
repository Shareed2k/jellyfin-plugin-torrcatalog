using System.Net;
using System.Text.Json;
using Jellyfin.Plugin.TorrCatalog.Configuration;
using Jellyfin.Plugin.TorrCatalog.Releases;
using Jellyfin.Plugin.TorrCatalog.Tmdb;
using Jellyfin.Plugin.TorrCatalog.TorrServer;
using MediaBrowser.Model.Dlna;
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

    static readonly Dictionary<string, System.Text.RegularExpressions.Regex> NoLanguages = new();

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

        var stream = await resolver.ResolveAsync(VirtualId.Movie(42), null, Ct);
        await resolver.ResolveAsync(VirtualId.Movie(42), null, Ct);

        Assert.Equal($"http://torrserver.test:8090/stream/Movie.2024.mkv?link={HashB}&index=2&play", stream.Url);
        Assert.Equal("mkv", stream.Container);
        Assert.Equal(9_000, stream.Size);
        Assert.Equal(1, Count("\"drop\"")); // failed release was dropped from TorrServer
        Assert.Equal(2, Count("\"add\"")); // second resolve came from cache
    }

    [Fact]
    public async Task LiveCamIsTheLastResort()
    {
        _torrents[HashA] = [new { Id = 1, Path = "Cam/Movie.2024.CAM.mkv", Length = 9 }];
        _http.Respond = Upstreams(Release(HashA, "Movie.2024.CAM", seeders: 900));
        var messages = new List<string>();

        var stream = await Resolver().ResolveAsync(VirtualId.Movie(42), null, Ct, messages.Add);

        Assert.Contains(HashA, stream.Url, StringComparison.Ordinal);
        Assert.Contains(messages, m => m.StartsWith('⚠'));
    }

    [Fact]
    public async Task DeadCamIsRefused()
    {
        _config.MinSeeders = 0; // let the dead torrent through ranking, so the resolver's own rule is tested
        _http.Respond = Upstreams(Release(HashA, "Movie.2024.CAM", seeders: 0));

        await Assert.ThrowsAsync<StreamNotFoundException>(() => Resolver().ResolveAsync(VirtualId.Movie(42), null, Ct));
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

        var source = Catalog.MediaSource(Guid.NewGuid(), 999, stream, ["ru", "en"]);

        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.Equal("mkv", source.Container);
        Assert.Equal(123, source.RunTimeTicks);
        Assert.Equal(2, source.DefaultAudioStreamIndex);
        Assert.DoesNotContain(source.MediaStreams, s => s.Type == MediaStreamType.Subtitle);
        Assert.False(source.SupportsTranscoding);
    }

    [Fact]
    public async Task PickedVersionResolvesExactlyThatRelease()
    {
        _torrents[HashA] = [new { Id = 1, Path = "A/Movie.2024.mkv", Length = 9 }];
        _torrents[HashB] = [new { Id = 1, Path = "B/Movie.2024.mkv", Length = 9 }];
        _http.Respond = Upstreams(
            Release(HashA, "Movie.2024.2160p.WEB-DL.HEVC", seeders: 100),
            Release(HashB, "Movie.2024.720p.WEBRip", seeders: 100));
        var resolver = Resolver();
        var second = (await ReleasesFor(resolver)).Single(r => r.Source.InfoHash == HashB);

        var stream = await resolver.ResolveAsync(VirtualId.Movie(42).WithRelease(second.Id), null, Ct);

        Assert.Contains(HashB, stream.Url, StringComparison.Ordinal);
        Assert.Equal(1, Count("\"add\""));
    }

    [Fact]
    public async Task AutoPicksAReleaseTheClientCanPlay()
    {
        // A browser-like profile: mp4 + h264 + aac only.
        var caps = new ClientCaps(new DeviceProfile
        {
            DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4", VideoCodec = "h264", AudioCodec = "aac" }],
        });
        const string HashC = "cccccccccccccccccccccccccccccccccccccccc";
        _torrents[HashA] = [new { Id = 1, Path = "A/Movie.2024.mkv", Length = 9 }];
        _torrents[HashB] = [new { Id = 1, Path = "B/Movie.2024.mkv", Length = 9 }];
        _torrents[HashC] = [new { Id = 1, Path = "C/Movie.2024.mp4", Length = 9 }];
        _http.Respond = Upstreams(
            Release(HashA, "Movie.2024.2160p.WEB-DL.HEVC", seeders: 100), // HEVC by title: never started
            Release(HashB, "Movie.2024.1080p.WEB-DL.x264", seeders: 100), // mkv once started: skipped
            Release(HashC, "Movie.2024.720p.WEBRip.x264", seeders: 100));
        var resolver = Resolver();

        var stream = await resolver.ResolveAsync(VirtualId.Movie(42), caps, Ct);
        await resolver.ResolveAsync(VirtualId.Movie(42), caps, Ct);

        Assert.Contains(HashC, stream.Url, StringComparison.Ordinal);
        Assert.Equal(2, Count("\"add\"")); // B and C; A skipped, second call cached
        Assert.DoesNotContain(_http.Bodies, b => b.Contains(HashA, StringComparison.Ordinal) && b.Contains("\"add\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ReleasePickedForOneEpisodeIsReusedForTheSeason()
    {
        _torrents[HashA] = [new { Id = 1, Path = "A/Show.S01E01.mkv", Length = 9 }, new { Id = 2, Path = "A/Show.S01E02.mkv", Length = 9 }];
        _torrents[HashB] = [new { Id = 1, Path = "B/Show.S01E01.mkv", Length = 9 }, new { Id = 2, Path = "B/Show.S01E02.mkv", Length = 9 }];
        var prowlarr = new object[]
        {
            Release(HashA, "Show.S01.2160p.WEB-DL.HEVC", seeders: 100), // Auto's favourite
            Release(HashB, "Show.S01.1080p.WEB-DL.x264", seeders: 100), // the user's pick
        };
        _http.Respond = (request, body) => request.RequestUri!.AbsolutePath switch
        {
            var p when p.EndsWith("/tv/42", StringComparison.Ordinal) => FakeHttp.Snake(new
            {
                Id = 42, Name = "Show", FirstAirDate = "2024-01-01",
                Seasons = new[] { new { SeasonNumber = 1, Name = "S1", EpisodeCount = 2 } },
            }),
            var p when p.Contains("/tv/42/season/1", StringComparison.Ordinal) => FakeHttp.Snake(new
            {
                SeasonNumber = 1,
                Episodes = new[] { new { EpisodeNumber = 1, SeasonNumber = 1, AirDate = "2024-01-01" }, new { EpisodeNumber = 2, SeasonNumber = 1, AirDate = "2024-01-08" } },
            }),
            _ => Upstreams(prowlarr)(request, body),
        };
        var resolver = Resolver();
        var pick = (await new ReleaseService(NewCatalog(), new ProwlarrClient(_http), new MemoryCache(new MemoryCacheOptions()), NullLogger<ReleaseService>.Instance)
            { Config = () => _config }.SearchEpisodeAsync(42, 1, 1, Ct)).Single(r => r.Source.InfoHash == HashB);

        await resolver.ResolveAsync(VirtualId.EpisodeOf(42, 1, 1).WithRelease(pick.Id), null, Ct);
        var next = await resolver.ResolveAsync(VirtualId.EpisodeOf(42, 1, 2), null, Ct);

        Assert.Equal($"http://torrserver.test:8090/stream/Show.S01E02.mkv?link={HashB}&index=2&play", next.Url);
    }

    [Fact]
    public void PlayabilityNeedsProbeToSayYes()
    {
        var browser = new ClientCaps(new DeviceProfile
        {
            DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mp4,mkv", VideoCodec = "h264", AudioCodec = "aac,opus" }],
        });
        var title = ReleaseParser.Parse("Show.S01E01.1080p.WEB-DL.x264", NoLanguages);
        static ResolvedStream Probed(string container, params (MediaStreamType Type, string Codec)[] streams) =>
            new("http://ts/x", container, 1, "r", new MediaInfo { MediaStreams = streams.Select((s, i) => new MediaStream { Index = i, Type = s.Type, Codec = s.Codec }).ToArray() });

        Assert.Equal(Playability.Playable, browser.Check(Probed("mkv", (MediaStreamType.Video, "h264"), (MediaStreamType.Audio, "aac")), title));
        Assert.Equal(Playability.NotPlayable, browser.Check(Probed("mkv", (MediaStreamType.Video, "h264"), (MediaStreamType.Audio, "ac3")), title));
        Assert.Equal(Playability.NotPlayable, browser.Check(Probed("avi", (MediaStreamType.Video, "mpeg4")), title));
        Assert.Equal(Playability.Unknown, browser.Check(new ResolvedStream("http://ts/x", "mkv", 1, "r", null), title));
        Assert.Equal(Playability.NotPlayable, browser.Check(new ResolvedStream("http://ts/x", "mkv", 1, "r", null), ReleaseParser.Parse("Show.S01E01.HEVC", NoLanguages)));

        // A TV (decodes AC3) gets an unprobed HEVC mkv directly instead of a needless transcode.
        var tv = new ClientCaps(new DeviceProfile
        {
            DirectPlayProfiles = [new DirectPlayProfile { Type = DlnaProfileType.Video, Container = "mkv,mp4", VideoCodec = "h264,hevc", AudioCodec = "aac,ac3,eac3,dts" }],
        });
        Assert.Equal(Playability.Playable, tv.Check(new ResolvedStream("http://ts/x", "mkv", 1, "r", null), ReleaseParser.Parse("Show.S01E01.2160p.HEVC", NoLanguages)));
    }

    [Theory]
    [InlineData("Show.S03.1080p/Show.S03E09.1080p.mkv", 3, 9)]
    [InlineData("Show.S03.1080p/09. The Rains of Castamere.mkv", 3, 9)]
    [InlineData("Show.Complete/Season 3/[09] Episode.mkv", 3, 9)]
    [InlineData("Show / Сезон 3/Серия 9.mkv", 3, 9)]
    public void PicksEpisodeFileInPacks(string wanted, int season, int episode)
    {
        var files = new[]
        {
            new TorrentFile(1, wanted.Replace("9", "8", StringComparison.Ordinal), 500),
            new TorrentFile(2, wanted, 400),
            new TorrentFile(3, wanted.Replace("9", "10", StringComparison.Ordinal), 600),
            new TorrentFile(4, "Show/Sample/sample.mkv", 900),
        };

        Assert.Equal(wanted, FileSelector.Episode(files, season, episode)!.Path);
    }

    [Fact]
    public void OtherSeasonsFileIsNotPicked()
    {
        var files = new[] { new TorrentFile(1, "Show/Show.S02E09.mkv", 1), new TorrentFile(2, "Show/Show.S03E01.mkv", 1) };

        Assert.Null(FileSelector.Episode(files, 3, 9));
    }

    Task<RankedRelease[]> ReleasesFor(StreamResolver _) =>
        new ReleaseService(NewCatalog(), new ProwlarrClient(_http), new MemoryCache(new MemoryCacheOptions()), NullLogger<ReleaseService>.Instance)
        { Config = () => _config }.SearchMovieAsync(42, Ct);

    Catalog NewCatalog() => new(new TmdbClient(_http), new MemoryCache(new MemoryCacheOptions()), "server1") { Config = () => _config };

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
