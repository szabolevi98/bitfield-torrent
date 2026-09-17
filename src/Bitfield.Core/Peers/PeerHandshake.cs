using System.Text;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Peers;

/// <summary>Thrown when a peer does not speak the protocol as specified.</summary>
public sealed class PeerProtocolException : Exception
{
    public PeerProtocolException(string message) : base(message)
    {
    }
}

/// <summary>
/// The eight bytes each side sets aside in the handshake to say what it can do
/// beyond the base protocol. Nearly all of them are still unused; the three
/// that matter are read here and the rest are carried through untouched.
/// </summary>
public readonly struct ReservedBits
{
    private readonly ulong _bits;

    public ReservedBits(ulong bits) => _bits = bits;

    public ReservedBits(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 8)
        {
            throw new ArgumentException("the reserved field is 8 bytes", nameof(bytes));
        }

        _bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(bytes);
    }

    /// <summary>What this client advertises.</summary>
    public static ReservedBits Ours => new(ExtensionProtocol | Dht);

    /// <summary>BEP 10: further messages can be negotiated by name.</summary>
    public const ulong ExtensionProtocol = 0x0000_0000_0010_0000;

    /// <summary>BEP 5: this peer runs a DHT node and will accept a port message.</summary>
    public const ulong Dht = 0x0000_0000_0000_0001;

    /// <summary>BEP 6: the fast extension's have-all, have-none and reject messages.</summary>
    public const ulong FastExtension = 0x0000_0000_0000_0004;

    public bool SupportsExtensionProtocol => (_bits & ExtensionProtocol) != 0;

    public bool SupportsDht => (_bits & Dht) != 0;

    public bool SupportsFastExtension => (_bits & FastExtension) != 0;

    public void WriteTo(Span<byte> destination) =>
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64BigEndian(destination, _bits);

    public override string ToString()
    {
        List<string> names = [];
        if (SupportsExtensionProtocol)
        {
            names.Add("extension protocol");
        }

        if (SupportsFastExtension)
        {
            names.Add("fast extension");
        }

        if (SupportsDht)
        {
            names.Add("DHT");
        }

        return names.Count == 0 ? "none" : string.Join(", ", names);
    }
}

/// <summary>
/// The sixty-eight bytes that open every peer connection: a length-prefixed
/// protocol name, eight reserved bytes, the infohash and the sender's peer id.
///
/// Both sides send it immediately without waiting for the other, so the
/// exchange costs no extra round trip. If the infohash coming back is not the
/// one that was sent, the peer is serving a different torrent and the
/// connection is of no use.
/// </summary>
public readonly struct PeerHandshake
{
    public const int Size = 68;

    private const string ProtocolName = "BitTorrent protocol";

    public required InfoHash InfoHash { get; init; }

    public required PeerId PeerId { get; init; }

    public ReservedBits Reserved { get; init; }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"a handshake is {Size} bytes", nameof(destination));
        }

        destination[0] = (byte)ProtocolName.Length;
        Encoding.ASCII.GetBytes(ProtocolName, destination[1..]);
        Reserved.WriteTo(destination[20..28]);
        InfoHash.WriteTo(destination[28..48]);
        PeerId.WriteTo(destination[48..68]);
    }

    public byte[] ToArray()
    {
        byte[] bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }

    public static PeerHandshake Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new PeerProtocolException($"a handshake is {Size} bytes, got {bytes.Length}");
        }

        if (bytes[0] != ProtocolName.Length)
        {
            throw new PeerProtocolException(
                $"the handshake names a protocol of {bytes[0]} characters, not {ProtocolName.Length}");
        }

        if (!bytes[1..20].SequenceEqual(Encoding.ASCII.GetBytes(ProtocolName)))
        {
            throw new PeerProtocolException(
                $"the handshake names \"{Encoding.ASCII.GetString(bytes[1..20])}\", not \"{ProtocolName}\"");
        }

        return new PeerHandshake
        {
            Reserved = new ReservedBits(bytes[20..28]),
            InfoHash = new InfoHash(bytes[28..48]),
            PeerId = new PeerId(bytes[48..68]),
        };
    }
}
