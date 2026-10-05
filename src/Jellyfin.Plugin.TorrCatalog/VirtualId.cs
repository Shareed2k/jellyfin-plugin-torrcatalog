using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.TorrCatalog;

public enum VirtualKind : byte
{
    Movie = 1,
    Section = 10,
}

/// <summary>
/// Reversible virtual item ID: 6-byte magic + kind + 64-bit payload packed into a Guid.
/// A Guid decodes straight back to its TMDB id, so no mapping table is needed and IDs survive restarts.
/// </summary>
public readonly record struct VirtualId(VirtualKind Kind, long Value)
{
    static ReadOnlySpan<byte> Magic => "TRRCTL"u8;

    public static VirtualId Movie(int tmdbId) => new(VirtualKind.Movie, tmdbId);

    public static VirtualId Section(string sectionId) =>
        new(VirtualKind.Section, BinaryPrimitives.ReadInt64LittleEndian(SHA256.HashData(Encoding.UTF8.GetBytes(sectionId))));

    public Guid ToGuid()
    {
        Span<byte> b = stackalloc byte[16];
        Magic.CopyTo(b);
        b[6] = (byte)Kind;
        BinaryPrimitives.WriteInt64LittleEndian(b[8..], Value);
        return new Guid(b);
    }

    public static bool TryParse(Guid id, out VirtualId result)
    {
        Span<byte> b = stackalloc byte[16];
        id.TryWriteBytes(b);
        if (!b[..6].SequenceEqual(Magic) || !Enum.IsDefined((VirtualKind)b[6]))
        {
            result = default;
            return false;
        }

        result = new((VirtualKind)b[6], BinaryPrimitives.ReadInt64LittleEndian(b[8..]));
        return true;
    }
}
