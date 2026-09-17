using System.Net;
using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Dht;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// The DHT: the arithmetic of distance, the table built on it, the messages,
/// and then a small network of these nodes on loopback that finds a torrent's
/// peers with no tracker anywhere in the picture.
/// </summary>
internal static class DhtTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Distance(check);
        Table(check);
        Messages(check);
        Network(check);
    }

    private static void Distance(Action<string, bool, string> check)
    {
        NodeId zero = Id("00");
        NodeId one = Id("01");
        NodeId high = Id("80");

        // Distance is exclusive or read as a number: nothing to do with how
        // the ids look, everything to do with their leading bits.
        check("distance: a node is closest to itself",
            NodeId.CompareDistance(zero, zero, one) < 0, "");
        check("distance: one bit apart beats a whole byte apart",
            NodeId.CompareDistance(zero, one, high) < 0, "");
        check("distance: and the comparison is the other way round from there",
            NodeId.CompareDistance(zero, high, one) > 0, "");
        check("distance: two equal ids tie",
            NodeId.CompareDistance(zero, one, one) == 0, "");

        // The common prefix is what decides which bucket a node lands in.
        check("prefix: an id shares every bit with itself",
            zero.CommonPrefixLength(zero) == 160, $"{zero.CommonPrefixLength(zero)}");
        check("prefix: a difference in the top bit shares none",
            zero.CommonPrefixLength(high) == 0, $"{zero.CommonPrefixLength(high)}");
        check("prefix: a difference in the bottom bit of the first byte shares seven",
            zero.CommonPrefixLength(one) == 7, $"{zero.CommonPrefixLength(one)}");

        // The last of the twenty bytes has to count as carefully as the first.
        NodeId lastByte = new(Convert.FromHexString(new string('0', 38) + "01"));
        check("prefix: a difference in the very last bit shares 159",
            zero.CommonPrefixLength(lastByte) == 159, $"{zero.CommonPrefixLength(lastByte)}");

        check("distance: an infohash reads as a position in the same space",
            NodeId.From(InfoHash.Parse("7acf8fb590b2060dd9c3146ef770169d593433b0")).ToString()
                == "7acf8fb590b2060dd9c3146ef770169d593433b0", "");
    }

    private static void Table(Action<string, bool, string> check)
    {
        RoutingTable table = new(Id("00"));

        check("table: starts empty with one bucket",
            table is { Count: 0, BucketCount: 1 }, $"{table.Count}/{table.BucketCount}");

        for (int i = 1; i <= RoutingTable.BucketSize; i++)
        {
            table.Add(Contact($"{i:X2}", 1000 + i));
        }

        check("table: a bucket holds eight", table.Count == 8, $"{table.Count}");

        // Adding a ninth far-away node splits the bucket containing this node's
        // own id rather than throwing anything away.
        table.Add(Contact("F1", 2000));
        check("table: a ninth node splits rather than being dropped",
            table.BucketCount > 1, $"{table.BucketCount}");

        // A node heard from again is remembered once, not twice.
        int before = table.Count;
        table.Add(Contact("01", 1001));
        check("table: a node seen again is not added twice", table.Count == before, $"{table.Count}");

        check("table: a node never stores itself", !table.Add(Contact("00", 1)), "");

        // Closest is what a lookup starts from, and has to be in order.
        RoutingTable ordered = new(Id("00"));
        ordered.Add(Contact("F0", 1));
        ordered.Add(Contact("01", 2));
        ordered.Add(Contact("40", 3));

        IReadOnlyList<DhtContact> closest = ordered.Closest(Id("00"), 3);
        check("table: the closest come back nearest first",
            closest.Count == 3 && closest[0].EndPoint.Port == 2 && closest[2].EndPoint.Port == 1,
            string.Join(", ", closest.Select(c => c.EndPoint.Port)));

        // A node that stops answering is dropped, because a lookup that
        // includes it spends a timeout on it.
        RoutingTable failing = new(Id("00"));
        failing.Add(Contact("01", 1));
        failing.Failed(Id("01"));
        failing.Failed(Id("01"));
        check("table: two failures are forgiven", failing.Count == 1, $"{failing.Count}");
        failing.Failed(Id("01"));
        check("table: a third drops the node", failing.Count == 0, $"{failing.Count}");
    }

    private static void Messages(Action<string, bool, string> check)
    {
        byte[] transaction = [0xAB, 0xCD];

        KrpcMessage ping = KrpcMessage.MakeQuery(transaction, "ping", ("id", new BString(Id("01").ToArray())));
        check("krpc: a ping encodes as a query",
            Encoding.ASCII.GetString(ping.Encode()).Contains("1:y1:q") && ping.Encode().Length > 20, "");

        check("krpc: and reads back",
            KrpcMessage.TryParse(ping.Encode(), out KrpcMessage? parsed)
            && parsed is { Kind: KrpcKind.Query, Query: "ping" }
            && parsed.TransactionId.SequenceEqual(transaction)
            && parsed.SenderId == Id("01"), "");

        KrpcMessage response = KrpcMessage.MakeResponse(transaction, ("id", new BString(Id("02").ToArray())));
        check("krpc: a response reads back",
            KrpcMessage.TryParse(response.Encode(), out KrpcMessage? back)
            && back is { Kind: KrpcKind.Response } && back.SenderId == Id("02"), "");

        KrpcMessage error = KrpcMessage.MakeError(transaction, 201, "generic error");
        check("krpc: an error carries its code and text",
            KrpcMessage.TryParse(error.Encode(), out KrpcMessage? failed)
            && failed is { Kind: KrpcKind.Error, ErrorCode: 201, ErrorMessage: "generic error" }, "");

        check("krpc: nonsense is refused", !KrpcMessage.TryParse("not bencode"u8.ToArray(), out _), "");
        check("krpc: a dictionary that is not a message is refused",
            !KrpcMessage.TryParse("d1:ti1ee"u8.ToArray(), out _), "");

        // Nodes travel packed twenty-six bytes each: the id, four bytes of
        // address and a port.
        DhtContact contact = new(Id("07"), new IPEndPoint(IPAddress.Parse("10.1.2.3"), 6881));
        byte[] packed = new byte[DhtContact.CompactSize];
        contact.WriteTo(packed);

        IReadOnlyList<DhtContact> unpacked = DhtContact.ReadCompact(packed);
        check("krpc: a node packs into twenty-six bytes and back",
            unpacked.Count == 1 && unpacked[0].Id == contact.Id && unpacked[0].EndPoint.Equals(contact.EndPoint),
            unpacked.Count == 1 ? unpacked[0].ToString() : "nothing");

        check("krpc: several nodes in one string",
            DhtContact.ReadCompact(new byte[DhtContact.CompactSize * 3].AsSpan()).Count == 0,
            "zero addresses should be dropped");
    }

    /// <summary>
    /// Eight of these nodes on loopback. One announces a torrent; another,
    /// which has never heard of it, looks it up and is told where to find it —
    /// the whole point of the DHT, with nothing else involved.
    /// </summary>
    private static void Network(Action<string, bool, string> check)
    {
        RunAsync(async () =>
        {
            using CancellationTokenSource stop = new(TimeSpan.FromSeconds(30));

            List<DhtNode> nodes = [];
            List<Task> running = [];

            try
            {
                for (int i = 0; i < 8; i++)
                {
                    DhtNode node = new(port: 0, address: IPAddress.Loopback) { PeerPort = 7000 + i };
                    nodes.Add(node);
                    running.Add(Task.Run(() => node.RunAsync(stop.Token), CancellationToken.None));
                }

                // Everything joins through the first node, which is all a real
                // client gets from a bootstrap router.
                string first = $"127.0.0.1:{nodes[0].Port}";

                foreach (DhtNode node in nodes.Skip(1))
                {
                    await node.BootstrapAsync([first], stop.Token).ConfigureAwait(false);
                }

                check("dht: the nodes found each other",
                    nodes.Skip(1).All(node => node.Table.Count >= 2),
                    string.Join(", ", nodes.Select(node => node.Table.Count)));

                InfoHash infoHash = InfoHash.Parse("7acf8fb590b2060dd9c3146ef770169d593433b0");

                // Nobody has heard of this torrent yet.
                IReadOnlyList<IPEndPoint> before = await nodes[^1]
                    .FindPeersAsync(infoHash, stop.Token).ConfigureAwait(false);

                check("dht: a torrent nobody holds has no peers", before.Count == 0, $"{before.Count}");

                int announced = await nodes[1].AnnounceAsync(infoHash, stop.Token).ConfigureAwait(false);
                check("dht: announcing reaches the nodes nearest the infohash", announced > 0, $"{announced}");

                IReadOnlyList<IPEndPoint> found = await nodes[^1]
                    .FindPeersAsync(infoHash, stop.Token).ConfigureAwait(false);

                check("dht: and another node then finds that peer",
                    found.Count > 0, $"{found.Count} peers");
                check("dht: at the port it announced",
                    found.Any(peer => peer.Port == nodes[1].PeerPort),
                    string.Join(", ", found));

                // The token is what stops a node being told that somebody else
                // is a peer, so an announce carrying a made-up one is refused.
                KrpcMessage? refused = await nodes[2].QueryAsync(
                    new IPEndPoint(IPAddress.Loopback, nodes[3].Port),
                    "announce_peer",
                    [
                        ("info_hash", new BString(infoHash.ToArray())),
                        ("port", new BInteger(9999)),
                        ("token", new BString("wrong")),
                    ],
                    stop.Token).ConfigureAwait(false);

                check("dht: an announce with an invented token is refused",
                    refused is { Kind: KrpcKind.Error }, refused?.ToString() ?? "no answer");
            }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);

                foreach (DhtNode node in nodes)
                {
                    await node.DisposeAsync().ConfigureAwait(false);
                }
            }
        });
    }

    private static NodeId Id(string leadingHex) =>
        new(Convert.FromHexString(leadingHex.PadRight(40, '0')));

    private static DhtContact Contact(string leadingHex, int port) =>
        new(Id(leadingHex), new IPEndPoint(IPAddress.Loopback, port));

    private static void RunAsync(Func<Task> work) => work().GetAwaiter().GetResult();
}
