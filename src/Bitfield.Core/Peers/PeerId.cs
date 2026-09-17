using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Bitfield.Core.Peers;

/// <summary>
/// The twenty bytes a client introduces itself with, to the tracker and in
/// every peer handshake. It is chosen once per run and is not a secret, but it
/// is how the rest of the swarm sees this client: the convention is a dash, two
/// letters naming the client, four digits of version, a dash, and twelve bytes
/// of whatever the client likes.
/// </summary>
public readonly struct PeerId : IEquatable<PeerId>
{
    public const int Size = 20;

    /// <summary>The two letters this client goes by in a peer list.</summary>
    public const string ClientCode = "BF";

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly uint _third;

    public PeerId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"a peer id is {Size} bytes, got {bytes.Length}", nameof(bytes));
        }

        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _third = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
    }

    /// <summary>
    /// A fresh peer id in the Azureus style: <c>-BF1010-</c> followed by twelve
    /// random characters. Random rather than derived from anything, so that two
    /// runs on the same machine are two different peers as far as the swarm is
    /// concerned.
    /// </summary>
    public static PeerId Generate(int major = 1, int minor = 0, int patch = 1)
    {
        Span<byte> bytes = stackalloc byte[Size];

        // One digit per version part and a trailing zero, which is the shape
        // every other client writes there.
        string prefix = $"-{ClientCode}{Digit(major)}{Digit(minor)}{Digit(patch)}0-";
        Encoding.ASCII.GetBytes(prefix, bytes);

        // Alphanumerics only: a peer id travels through tracker query strings
        // and other clients' logs, and there is nothing to gain from making it
        // awkward to read.
        const string alphabet = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        for (int i = prefix.Length; i < Size; i++)
        {
            bytes[i] = (byte)alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        }

        return new PeerId(bytes);
    }

    private static char Digit(int value) => (char)('0' + Math.Clamp(value, 0, 9));

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

    /// <summary>
    /// The name of the client behind a peer id, read from the prefix other
    /// clients write there. Worth having because a peer list is far more useful
    /// when it says who is on the other end.
    /// </summary>
    public string ClientName()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);

        if (bytes[0] != (byte)'-' || bytes[7] != (byte)'-')
        {
            return "unknown";
        }

        string code = Encoding.ASCII.GetString(bytes[1..3]);
        string version = FormatVersion(Encoding.ASCII.GetString(bytes[3..7]));

        string name = code switch
        {
            ClientCode => "Bitfield",
            "qB" => "qBittorrent",
            "TR" => "Transmission",
            "UT" => "µTorrent",
            "lt" or "LT" => "libtorrent",
            "DE" => "Deluge",
            "AZ" => "Azureus",
            "BT" => "BitTorrent",
            "WW" => "WebTorrent",
            _ => code,
        };

        return $"{name} {version}";
    }

    /// <summary>
    /// Four characters of version, which by convention are one digit per part
    /// with a trailing zero — so qBittorrent 5.0.2 writes <c>5020</c>. Clients
    /// that use letters there instead are left alone rather than mistranslated.
    /// </summary>
    private static string FormatVersion(string version)
    {
        foreach (char c in version)
        {
            if (c is < '0' or > '9')
            {
                return version;
            }
        }

        return version[3] == '0'
            ? $"{version[0]}.{version[1]}.{version[2]}"
            : $"{version[0]}.{version[1]}.{version[2]}.{version[3]}";
    }

    public bool Equals(PeerId other) =>
        _first == other._first && _second == other._second && _third == other._third;

    public override bool Equals(object? obj) => obj is PeerId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_first, _second, _third);

    /// <summary>
    /// The bytes as text, with anything unprintable shown as a dot. A peer id is
    /// arbitrary bytes, and plenty of clients put non-text in the tail.
    /// </summary>
    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);

        return string.Create(Size, bytes.ToArray(), static (text, source) =>
        {
            for (int i = 0; i < source.Length; i++)
            {
                text[i] = source[i] is >= 0x20 and < 0x7F ? (char)source[i] : '.';
            }
        });
    }

    public static bool operator ==(PeerId left, PeerId right) => left.Equals(right);

    public static bool operator !=(PeerId left, PeerId right) => !left.Equals(right);
}
