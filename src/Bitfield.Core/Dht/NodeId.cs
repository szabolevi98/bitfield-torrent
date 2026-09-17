using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Dht;

/// <summary>
/// A node's place in the DHT: 160 bits, the same width as an infohash and in
/// the same space as one, which is the whole trick behind Kademlia.
///
/// The distance between two ids is their exclusive or, read as a number. It has
/// nothing to do with geography or latency — it is arithmetic, and that is what
/// makes it useful: every node can work out which of the nodes it knows is
/// closer to a target, so a lookup that asks the closest node it has, then the
/// closest one that answers, converges without anybody holding a map of the
/// whole network. A torrent's peers are kept by the nodes whose ids are closest
/// to its infohash.
/// </summary>
public readonly struct NodeId : IEquatable<NodeId>
{
    public const int Size = 20;

    private readonly ulong _first;
    private readonly ulong _second;
    private readonly uint _third;

    public NodeId(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != Size)
        {
            throw new ArgumentException($"a node id is {Size} bytes, got {bytes.Length}", nameof(bytes));
        }

        _first = BinaryPrimitives.ReadUInt64BigEndian(bytes);
        _second = BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]);
        _third = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..]);
    }

    private NodeId(ulong first, ulong second, uint third)
    {
        _first = first;
        _second = second;
        _third = third;
    }

    public static NodeId Random()
    {
        Span<byte> bytes = stackalloc byte[Size];
        RandomNumberGenerator.Fill(bytes);
        return new NodeId(bytes);
    }

    /// <summary>An infohash read as a position in the same space.</summary>
    public static NodeId From(InfoHash infoHash)
    {
        Span<byte> bytes = stackalloc byte[Size];
        infoHash.WriteTo(bytes);
        return new NodeId(bytes);
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

    /// <summary>
    /// How many leading bits two ids share, which is what decides the bucket a
    /// node belongs in: the more bits an id shares with this client's own, the
    /// nearer it is and the more of its neighbourhood is worth remembering.
    /// </summary>
    public int CommonPrefixLength(NodeId other)
    {
        ulong first = _first ^ other._first;
        if (first != 0)
        {
            return BitOperations.LeadingZeroCount(first);
        }

        ulong second = _second ^ other._second;
        if (second != 0)
        {
            return 64 + BitOperations.LeadingZeroCount(second);
        }

        uint third = _third ^ other._third;
        return third != 0 ? 128 + BitOperations.LeadingZeroCount((ulong)third) - 32 : 160;
    }

    /// <summary>
    /// Which of two ids is closer to a target: negative when
    /// <paramref name="left"/> is, as a comparer wants it.
    /// </summary>
    public static int CompareDistance(NodeId target, NodeId left, NodeId right)
    {
        ulong a = left._first ^ target._first;
        ulong b = right._first ^ target._first;
        if (a != b)
        {
            return a < b ? -1 : 1;
        }

        a = left._second ^ target._second;
        b = right._second ^ target._second;
        if (a != b)
        {
            return a < b ? -1 : 1;
        }

        uint c = left._third ^ target._third;
        uint d = right._third ^ target._third;
        return c == d ? 0 : c < d ? -1 : 1;
    }

    /// <summary>A comparer that orders nodes by how close they are to a target.</summary>
    public static IComparer<NodeId> ClosestTo(NodeId target) => new DistanceComparer(target);

    private sealed class DistanceComparer(NodeId target) : IComparer<NodeId>
    {
        public int Compare(NodeId left, NodeId right) => CompareDistance(target, left, right);
    }

    public bool Equals(NodeId other) =>
        _first == other._first && _second == other._second && _third == other._third;

    public override bool Equals(object? obj) => obj is NodeId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_first, _second, _third);

    public override string ToString()
    {
        Span<byte> bytes = stackalloc byte[Size];
        WriteTo(bytes);
        return Convert.ToHexStringLower(bytes);
    }

    public static bool operator ==(NodeId left, NodeId right) => left.Equals(right);

    public static bool operator !=(NodeId left, NodeId right) => !left.Equals(right);
}
