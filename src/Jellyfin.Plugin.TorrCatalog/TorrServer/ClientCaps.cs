using System.Security.Cryptography;
using System.Text;
using Jellyfin.Plugin.TorrCatalog.Releases;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.TorrCatalog.TorrServer;

public enum Playability
{
    /// <summary>ffprobe-verified container and codecs the client direct plays.</summary>
    Playable,

    /// <summary>Known container or codec the client can't direct play.</summary>
    NotPlayable,

    /// <summary>Container is fine but codecs are unverified (ffprobe failed): don't bet the playback on it.</summary>
    Unknown,
}

/// <summary>
/// What a client can direct play, from the DeviceProfile it sends with PlaybackInfo. Virtual items can't be
/// transcoded, so Auto mode uses this to pick a release the client can actually decode (e.g. a browser that
/// can't do HEVC/AC3 vs a TV that can), and hands the rest to Jellyfin (Phase 7 fallback).
/// </summary>
public sealed class ClientCaps(DeviceProfile profile)
{
    readonly DirectPlayProfile[] _video = (profile.DirectPlayProfiles ?? []).Where(p => p.Type == DlnaProfileType.Video).ToArray();

    /// <summary>Gets a short fingerprint of the direct-play rules, for caching per kind of client.</summary>
    public string Signature => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Describe()))).AsSpan(0, 16).ToString();

    /// <summary>The client's video direct-play rules, for logs.</summary>
    public string Describe() => string.Join("; ", _video.Select(p => $"{p.Container}|{p.VideoCodec}|{p.AudioCodec}"));

    /// <summary>Cheap pre-check from the release title, before any torrent is started. Unknown codec = maybe.</summary>
    public bool MaybePlays(ParsedRelease release) => Plays(null, VideoCodecName(release.Codec), AudioCodecNames(release));

    /// <summary>
    /// Verdict for a started stream. Only ffprobe results can make it Playable: titles rarely name the audio
    /// codec, and an AC3 track alone is enough to make a browser fail.
    /// </summary>
    public Playability Check(ResolvedStream stream, ParsedRelease release)
    {
        if (_video.Length == 0)
        {
            return Playability.Playable;
        }

        if (!_video.Any(p => p.SupportsContainer(stream.Container)))
        {
            return Playability.NotPlayable;
        }

        var streams = stream.Probe?.MediaStreams ?? [];
        var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Codec;
        if (video is null)
        {
            // No probe: the title can rule it out. Otherwise, TVs and boxes that decode AC3 handle the usual
            // torrent mix (HEVC/AC3/DTS in mkv), so play directly; browsers don't, so stay Unknown (fallback).
            if (!Plays(stream.Container, VideoCodecName(release.Codec), AudioCodecNames(release)))
            {
                return Playability.NotPlayable;
            }

            return _video.Any(p => p.SupportsContainer(stream.Container) && p.SupportsAudioCodec("ac3")) ? Playability.Playable : Playability.Unknown;
        }

        var audio = streams.Where(s => s.Type == MediaStreamType.Audio).Select(s => s.Codec).OfType<string>().ToArray();
        return Plays(stream.Container, video, audio) ? Playability.Playable : Playability.NotPlayable;
    }

    // Any audio track the client can decode is enough (it can switch to it). Null/empty = not known, not checked.
    bool Plays(string? container, string? video, string[] audio) =>
        _video.Any(p => (container is null || p.SupportsContainer(container))
            && (video is null || p.SupportsVideoCodec(video))
            && (audio.Length == 0 || audio.Any(p.SupportsAudioCodec)));

    // ffprobe codec names, which is what device profiles use.
    static string? VideoCodecName(VideoCodec codec) => codec switch
    {
        VideoCodec.Hevc => "hevc",
        VideoCodec.H264 => "h264",
        VideoCodec.Av1 => "av1",
        VideoCodec.Xvid => "mpeg4",
        _ => null,
    };

    static string[] AudioCodecNames(ParsedRelease r) => r.Audio switch
    {
        AudioCodec.Aac => ["aac"],
        AudioCodec.Ac3 => ["ac3"],
        AudioCodec.Eac3 => ["eac3"],
        AudioCodec.Dts or AudioCodec.DtsHdMa or AudioCodec.DtsX => ["dts"],
        AudioCodec.TrueHd => ["truehd"],
        _ => [],
    };
}
