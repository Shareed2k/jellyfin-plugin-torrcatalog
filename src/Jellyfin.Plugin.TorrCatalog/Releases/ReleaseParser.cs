using System.Globalization;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.TorrCatalog.Releases;

public enum ReleaseSource
{
    Unknown,
    Cam,
    Telesync,
    Telecine,
    Dvd,
    Hdtv,
    WebRip,
    WebDl,
    BluRay,
    Remux,
}

public enum VideoCodec
{
    Unknown,
    Xvid,
    H264,
    Hevc,
    Av1,
}

public enum HdrType
{
    None,
    Hdr,
    Hdr10,
    Hdr10Plus,
    DolbyVision,
}

public enum AudioCodec
{
    Unknown,
    Aac,
    Ac3,
    Eac3,
    Dts,
    DtsHdMa,
    DtsX,
    TrueHd,
}

public sealed record ParsedRelease(
    int Resolution,
    ReleaseSource Source,
    VideoCodec Codec,
    HdrType Hdr,
    AudioCodec Audio,
    bool Atmos,
    string[] Languages,
    int? Season,
    int? Episode,
    int[] Years,
    bool IsJunk,
    bool IsUpscale);

/// <summary>Extracts quality tags from torrent titles with one small rule per tag; first matching rule wins.</summary>
public static class ReleaseParser
{
    static readonly (Regex Rule, int Value)[] Resolutions =
    [
        (Tag("2160p|4k|uhd"), 2160),
        (Tag("1080[pi]|fhd"), 1080),
        (Tag("720p"), 720),
        (Tag("576p|480p|sd|dvdrip"), 480),
    ];

    // Order matters: bad sources first (a CAM title that also says "WEB" is still a CAM),
    // REMUX before BluRay, WEB rips before bare "WEB".
    static readonly (Regex Rule, ReleaseSource Value)[] Sources =
    [
        (Tag(@"(?:hd)?cam(?:rip)?"), ReleaseSource.Cam),
        (Tag(@"(?:hd)?ts|telesync|pdvd"), ReleaseSource.Telesync),
        (Tag(@"tc|telecine"), ReleaseSource.Telecine),
        (Tag("(?:bd|uhd)?remux"), ReleaseSource.Remux),
        (Tag(@"blu-?ray|bdrip|brrip|bd(?:25|50|66|100)?"), ReleaseSource.BluRay),
        (Tag(@"web-?(?:dl-?)?rip|hdrip"), ReleaseSource.WebRip),
        (Tag(@"web-?dl|web"), ReleaseSource.WebDl),
        (Tag(@"hdtv(?:rip)?|satrip|dvb"), ReleaseSource.Hdtv),
        (Tag(@"dvd(?:rip|5|9)?"), ReleaseSource.Dvd),
    ];

    static readonly (Regex Rule, VideoCodec Value)[] Codecs =
    [
        (Tag(@"hevc|[hx]\.?265"), VideoCodec.Hevc),
        (Tag("av1"), VideoCodec.Av1),
        (Tag(@"avc|[hx]\.?264"), VideoCodec.H264),
        (Tag("xvid|divx"), VideoCodec.Xvid),
    ];

    static readonly (Regex Rule, HdrType Value)[] Hdrs =
    [
        (Tag(@"dv|dovi|dolby[ .]?vision"), HdrType.DolbyVision),
        (Tag(@"hdr10(?:\+|plus)"), HdrType.Hdr10Plus),
        (Tag("hdr10"), HdrType.Hdr10),
        (Tag("hdr"), HdrType.Hdr),
    ];

    static readonly (Regex Rule, AudioCodec Value)[] Audios =
    [
        (Tag("truehd"), AudioCodec.TrueHd),
        (Tag(@"dts[-:. ]?x"), AudioCodec.DtsX),
        (Tag(@"dts[-. ]?hd(?:[-. ]?ma)?"), AudioCodec.DtsHdMa),
        (Tag("dts"), AudioCodec.Dts),
        (Tag(@"e-?ac-?3|ddp(?:\d\.\d)?|dd\+(?:\d\.\d)?"), AudioCodec.Eac3),
        (Tag(@"ac-?3|dd(?:\d\.\d)?"), AudioCodec.Ac3),
        (Tag("aac"), AudioCodec.Aac),
    ];

    static readonly Regex Atmos = Tag("atmos");
    static readonly Regex SeasonEpisode = Tag(@"s(\d{1,2})[ .]?e(\d{1,3})|(\d{1,2})x(\d{2,3})");
    static readonly Regex SeasonOnly = Tag(@"s(\d{1,2})|season[ .]?(\d{1,2})");
    static readonly Regex Year = Tag(@"(?:19|20)\d{2}");
    static readonly Regex Junk = Tag("sample|trailer|трейлер|featurette|extras|bonus|commentary");
    static readonly Regex Upscale = Tag(@"upscaled?|ai[ .-]?upscale");

    // RuTracker voice-over codes, e.g. "| D, P, A" (dub, pro multi-voice, author): Russian audio.
    // Requires a comma list or a "|"-delimited single code, so lone letters in titles don't count.
    static readonly Regex RuTrackerVoices = new(
        @"(?<![\p{L}\p{N}])[DPAL]\d?(?:\s*,\s*[DPAL]\d?)+(?![\p{L}\p{N}])|\|\s*[DPAL]\d?\s*(?:\||$)",
        RegexOptions.CultureInvariant);

    public static ParsedRelease Parse(string title, IReadOnlyDictionary<string, Regex> languages)
    {
        int? season = null, episode = null;
        if (SeasonEpisode.Match(title) is { Success: true } se)
        {
            season = Int(se.Groups[1].Success ? se.Groups[1] : se.Groups[3]);
            episode = Int(se.Groups[2].Success ? se.Groups[2] : se.Groups[4]);
        }
        else if (SeasonOnly.Match(title) is { Success: true } s)
        {
            season = Int(s.Groups[1].Success ? s.Groups[1] : s.Groups[2]);
        }

        return new ParsedRelease(
            First(Resolutions, title),
            First(Sources, title),
            First(Codecs, title),
            First(Hdrs, title),
            First(Audios, title),
            Atmos.IsMatch(title),
            Languages(title, languages),
            season,
            episode,
            Year.Matches(title).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray(),
            Junk.IsMatch(title),
            Upscale.IsMatch(title));
    }

    static string[] Languages(string title, IReadOnlyDictionary<string, Regex> languages)
    {
        var found = languages.Where(l => l.Value.IsMatch(title)).Select(l => l.Key).ToList();
        if (!found.Contains("ru") && RuTrackerVoices.IsMatch(title))
        {
            found.Add("ru");
        }

        return found.ToArray();
    }

    /// <summary>Parses "ru=RUS,RU,DUB;en=ENG,EN" into one matcher per language.</summary>
    public static Dictionary<string, Regex> LanguageMatchers(string aliases) =>
        aliases.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(kv => kv.Length == 2 && kv[0].Length > 0 && kv[1].Length > 0)
            .ToDictionary(
                kv => kv[0].ToLowerInvariant(),
                kv => Tag(string.Join('|', kv[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Regex.Escape))));

    static T First<T>((Regex Rule, T Value)[] rules, string title)
        where T : struct
    {
        foreach (var (rule, value) in rules)
        {
            if (rule.IsMatch(title))
            {
                return value;
            }
        }

        return default;
    }

    static int Int(Group g) => int.Parse(g.Value, CultureInfo.InvariantCulture);

    // Token boundary = not adjacent to a letter or digit (Unicode-aware, so Cyrillic works too).
    static Regex Tag(string pattern) =>
        new($@"(?<![\p{{L}}\p{{N}}])(?:{pattern})(?![\p{{L}}\p{{N}}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
