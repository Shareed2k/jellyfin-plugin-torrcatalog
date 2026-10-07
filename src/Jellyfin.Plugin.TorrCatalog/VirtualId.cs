using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TorrCatalog;

public enum VirtualKind : byte
{
    Movie = 1,
    Series = 2,
    Season = 3,
    Episode = 4,
    Section = 10,
}

/// <summary>
/// Reversible virtual item ID packed into a Guid, so an ID decodes straight back to TMDB coordinates
/// without a mapping table and survives restarts. Layout (16 bytes):
/// "TRCTL" magic (5) | kind (1) | TMDB id int32 (4) | season (1) | episode uint16 (2) | release uint24 (3).
/// Sections use the 10 payload bytes for a hash of their config id instead.
/// Release != 0 marks a specific "version" (torrent release) of a playable item; 0 means "best release".
/// </summary>
public readonly record struct VirtualId(VirtualKind Kind, int TmdbId, int Season = 0, int Episode = 0, int Release = 0, long SectionHash = 0)
{
    static ReadOnlySpan<byte> Magic => "TRCTL"u8;

    public static VirtualId Movie(int tmdbId) => new(VirtualKind.Movie, tmdbId);

    public static VirtualId Series(int tvId) => new(VirtualKind.Series, tvId);

    public static VirtualId SeasonOf(int tvId, int season) => new(VirtualKind.Season, tvId, season);

    public static VirtualId EpisodeOf(int tvId, int season, int episode) => new(VirtualKind.Episode, tvId, season, episode);

    public static VirtualId Section(string sectionId) =>
        new(VirtualKind.Section, 0, SectionHash: BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(sectionId))));

    public bool IsPlayable => Kind is VirtualKind.Movie or VirtualKind.Episode;

    /// <summary>Gets the item without a release pin: what progress and metadata are keyed on.</summary>
    public VirtualId Base => this with { Release = 0 };

    public VirtualId WithRelease(string releaseId) => this with { Release = ReleasePrefix(releaseId) };

    public bool IsRelease(string releaseId) => Release != 0 && Release == ReleasePrefix(releaseId);

    /// <summary>First 24 bits of a hex release id; never 0 (that means "auto").</summary>
    public static int ReleasePrefix(string releaseId) =>
        int.Parse(releaseId.AsSpan(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture) is var p and not 0 ? p : 1;

    /// <summary>Compact form for logs, e.g. "Episode:1399:S03E09@a1b2c3".</summary>
    /// <remarks>Must stay overridden: the generated record ToString prints every property, and Base is itself a
    /// VirtualId, so the default recursed until the stack overflowed and took the whole Jellyfin process down.</remarks>
    public override string ToString() => Kind switch
    {
        VirtualKind.Section => $"Section:{SectionHash:x16}",
        VirtualKind.Season => $"Season:{TmdbId}:S{Season:00}",
        VirtualKind.Episode => $"Episode:{TmdbId}:S{Season:00}E{Episode:00}" + (Release != 0 ? $"@{Release:x6}" : string.Empty),
        _ => $"{Kind}:{TmdbId}" + (Release != 0 ? $"@{Release:x6}" : string.Empty),
    };

    public Guid ToGuid()
    {
        Span<byte> b = stackalloc byte[16];
        Magic.CopyTo(b);
        b[5] = (byte)Kind;
        if (Kind == VirtualKind.Section)
        {
            BinaryPrimitives.WriteInt64LittleEndian(b[6..], SectionHash);
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(b[6..], TmdbId);
            b[10] = checked((byte)Season);
            BinaryPrimitives.WriteUInt16LittleEndian(b[11..], checked((ushort)Episode));
            b[13] = (byte)Release;
            b[14] = (byte)(Release >> 8);
            b[15] = (byte)(Release >> 16);
        }

        return new Guid(b);
    }

    public static bool TryParse(Guid id, out VirtualId result)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        result = default;
        if (!b[..5].SequenceEqual(Magic) || !Enum.IsDefined((VirtualKind)b[5]))
        {
            return false;
        }

        var kind = (VirtualKind)b[5];
        result = kind == VirtualKind.Section
            ? new VirtualId(kind, 0, SectionHash: BinaryPrimitives.ReadInt64LittleEndian(b[6..]))
            : new VirtualId(kind, BinaryPrimitives.ReadInt32LittleEndian(b[6..]), b[10], BinaryPrimitives.ReadUInt16LittleEndian(b[11..]), b[13] | (b[14] << 8) | (b[15] << 16));
        return true;
    }

    /// <summary>Parses a Jellyfin media source id ("N"-format Guid string) into a virtual id, if it is one.</summary>
    public static bool TryParse(string? id, out VirtualId result)
    {
        result = default;
        return Guid.TryParse(id, out var guid) && TryParse(guid, out result);
    }
}
