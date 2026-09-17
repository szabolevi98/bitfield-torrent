using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Bitfield.Core.Torrents;

/// <summary>
/// The twenty bytes that identify a torrent: the SHA-1 of its info dictionary,
/// taken over the dictionary's own bytes exactly as they appeared in the file.
/// Everything else keys off this — the tracker announce, the peer handshake and
/// the DHT lookup all carry it, so getting it wrong means no peer will talk.
/// </summary>
public readonly struct InfoHash : IEquatable<InfoHash>
{
    public const int Size = 20;

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly uint _third;

    public InfoHash(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"an infohash is {Size} bytes, got {bytes.Length}", nameof(bytes));
        }

        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _third = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
    }

    /// <summary>
    /// Hashes the raw bytes of an info dictionary. The caller passes the slice
    /// of the original file, never a re-encoding of the parsed tree: a torrent
    /// may carry keys this client does not know, or keys in an order it would
    /// not have chosen, and re-encoding would silently change the identity of
    /// the torrent.
    /// </summary>
    public static InfoHash ComputeSha1(ReadOnlySpan<byte> rawInfoDictionary)
    {
        Span<byte> hash = stackalloc byte[Size];
        SHA1.HashData(rawInfoDictionary, hash);
        return new InfoHash(hash);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"need {Size} bytes", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64BigEndian(destination, _first);
        BinaryPrimitives.WriteUInt64BigEndian(destination[8..], _second);
        BinaryPrimitives.WriteUInt32BigEndian(destination[16..], _third);
    }

    public byte[] ToArray()
    {
        byte[] bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    public static InfoHash Parse(ReadOnlySpan<char> hex)
    {
        if (!TryParse(hex, out InfoHash hash))
        {
            throw new FormatException($"\"{hex}\" is not a {Size * 2} character hexadecimal infohash");
        }

        return hash;
    }

    public static bool TryParse(ReadOnlySpan<char> hex, out InfoHash hash)
    {
        hash = default;

        if (hex.Length != Size * 2)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[Size];
        for (int i = 0; i < Size; i++)
        {
            int high = HexDigit(hex[i * 2]);
            int low = HexDigit(hex[(i * 2) + 1]);
            if (high < 0 || low < 0)
            {
                return false;
            }

            bytes[i] = (byte)((high << 4) | low);
        }

        hash = new InfoHash(bytes);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    public bool Equals(InfoHash other) =>
        _first == other._first && _second == other._second && _third == other._third;

    public override bool Equals(object? obj) => obj is InfoHash other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_first, _second, _third);

    /// <summary>The usual lowercase hexadecimal form, as magnet links spell it.</summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static bool operator ==(InfoHash left, InfoHash right) => left.Equals(right);

    public static bool operator !=(InfoHash left, InfoHash right) => !left.Equals(right);
}
