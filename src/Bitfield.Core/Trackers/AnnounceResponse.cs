using System.Buffers.Binary;
using System.Net;
using Bitfield.Core.Bencode;

namespace Bitfield.Core.Trackers;

/// <summary>Thrown when a tracker refuses an announce or answers with nonsense.</summary>
public sealed class TrackerException : Exception
{
    public TrackerException(string message) : base(message)
    {
    }

    /// <summary>
    /// True when the tracker itself said no — as opposed to the network failing
    /// or the reply being unreadable. Its text comes from the tracker and is
    /// worth showing to the user verbatim, because it is where "unregistered
    /// torrent" and "client not allowed" arrive.
    /// </summary>
    public bool FromTracker { get; init; }
}

/// <summary>
/// What a tracker sends back: some peers to connect to, and how long to wait
/// before asking again.
/// </summary>
public sealed record AnnounceResponse
{
    /// <summary>Seconds to wait before the next routine announce.</summary>
    public required TimeSpan Interval { get; init; }

    /// <summary>
    /// The shortest interval the tracker will tolerate, when it says so.
    /// Announcing more often than this is how a client gets itself banned.
    /// </summary>
    public TimeSpan? MinInterval { get; init; }

    /// <summary>
    /// Peers with the whole torrent, or null where the tracker did not say.
    /// Plenty of them do not — a bare opentracker answers with an interval and
    /// a peer list and nothing else — and reporting that as a swarm of zero
    /// would be inventing a fact.
    /// </summary>
    public int? Seeders { get; init; }

    /// <summary>Peers still downloading, or null where the tracker did not say.</summary>
    public int? Leechers { get; init; }

    public required IReadOnlyList<IPEndPoint> Peers { get; init; }

    /// <summary>An identifier to send back on the next announce, if given.</summary>
    public string? TrackerId { get; init; }

    /// <summary>A complaint that did not stop the announce from succeeding.</summary>
    public string? Warning { get; init; }

    public static AnnounceResponse Parse(ReadOnlyMemory<byte> body)
    {
        BValue root;
        try
        {
            root = BencodeParser.Parse(body);
        }
        catch (BencodeException e)
        {
            throw new TrackerException($"the tracker's reply is not bencode: {e.Message}");
        }

        if (root is not BDictionary reply)
        {
            throw new TrackerException("the tracker's reply is not a dictionary");
        }

        if (reply.GetString("failure reason") is { } failure)
        {
            throw new TrackerException(failure) { FromTracker = true };
        }

        long interval = reply.GetInteger("interval") ?? 1800;
        if (interval is <= 0 or > 86_400)
        {
            interval = 1800;
        }

        List<IPEndPoint> peers = [];
        ReadPeers(reply.Get("peers"), peers, compactEntrySize: 6);
        ReadPeers(reply.Get("peers6"), peers, compactEntrySize: 18);

        return new AnnounceResponse
        {
            Interval = TimeSpan.FromSeconds(interval),
            MinInterval = reply.GetInteger("min interval") is long min and > 0
                ? TimeSpan.FromSeconds(min)
                : null,
            Seeders = Count(reply.GetInteger("complete")),
            Leechers = Count(reply.GetInteger("incomplete")),
            Peers = peers,
            TrackerId = reply.GetString("tracker id"),
            Warning = reply.GetString("warning message"),
        };
    }

    private static int? Count(long? value) =>
        value is >= 0 ? (int)Math.Min(value.Value, int.MaxValue) : null;

    /// <summary>
    /// Peers arrive in one of two shapes. The compact form (BEP 23) is a single
    /// string of fixed-width records — four address bytes and a port for IPv4,
    /// sixteen and a port for IPv6. The older form is a list of dictionaries,
    /// where the address is text and may even be a hostname.
    ///
    /// Which compact width applies is decided by the key the string arrived
    /// under, never by its length: eighteen bytes divide evenly by six, so an
    /// IPv6 peer guessed at by length reads back as three nonsense IPv4 ones.
    /// </summary>
    private static void ReadPeers(BValue? value, List<IPEndPoint> peers, int compactEntrySize)
    {
        switch (value)
        {
            case BString compact:
                ReadCompactPeers(compact.Span, peers, compactEntrySize);
                break;

            case BList list:
                foreach (BValue item in list.Items)
                {
                    if (item is not BDictionary peer)
                    {
                        continue;
                    }

                    string? address = peer.GetString("ip");
                    long? port = peer.GetInteger("port");

                    if (address != null && port is > 0 and <= ushort.MaxValue
                        && IPAddress.TryParse(address, out IPAddress? parsed))
                    {
                        Add(peers, parsed, (int)port.Value);
                    }
                }

                break;
        }
    }

    private static void ReadCompactPeers(ReadOnlySpan<byte> compact, List<IPEndPoint> peers, int size)
    {
        if (compact.Length % size != 0)
        {
            throw new TrackerException(
                $"a compact peer list of {compact.Length} bytes is not a whole number of {size} byte peers");
        }

        int addressLength = size - 2;
        for (int offset = 0; offset + size <= compact.Length; offset += size)
        {
            IPAddress address = new(compact.Slice(offset, addressLength));
            int port = BinaryPrimitives.ReadUInt16BigEndian(compact.Slice(offset + addressLength, 2));
            Add(peers, address, port);
        }
    }

    /// <summary>
    /// Trackers repeat themselves, pad a short list with zero ports, and hand
    /// back unroutable addresses. None of that is worth a connection attempt.
    /// </summary>
    private static void Add(List<IPEndPoint> peers, IPAddress address, int port)
    {
        if (port is <= 0 or > ushort.MaxValue
            || IPAddress.Any.Equals(address)
            || IPAddress.IPv6Any.Equals(address))
        {
            return;
        }

        IPEndPoint endPoint = new(address, port);
        if (!peers.Contains(endPoint))
        {
            peers.Add(endPoint);
        }
    }
}
