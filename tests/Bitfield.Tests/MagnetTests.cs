using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Tests;

/// <summary>
/// Magnet links, and the UDP tracker most of them point at.
///
/// The UDP exchange is checked against a tracker written here, which answers on
/// loopback and inspects what it is sent. Two datagrams with fixed layouts is
/// exactly the kind of thing to get subtly wrong and never notice, because a
/// tracker that does not understand a request simply says nothing.
/// </summary>
internal static class MagnetTests
{
    private const string DebianInfoHash = "7acf8fb590b2060dd9c3146ef770169d593433b0";

    public static void Run(Action<string, bool, string> check)
    {
        Links(check);
        UdpTracker(check);
        MetadataMessages(check);
    }

    private static void Links(Action<string, bool, string> check)
    {
        MagnetLink link = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{DebianInfoHash}"
            + "&dn=debian-13.7.0-amd64-netinst.iso"
            + "&tr=udp%3A%2F%2Ftracker.example%3A6969%2Fannounce"
            + "&tr=http%3A%2F%2Fother.example%2Fannounce"
            + "&x.pe=192.168.1.5%3A51413");

        check("magnet: the infohash", link.InfoHash.ToString() == DebianInfoHash, link.InfoHash.ToString());
        check("magnet: the display name",
            link.DisplayName == "debian-13.7.0-amd64-netinst.iso", link.DisplayName ?? "none");
        check("magnet: trackers are unescaped",
            link.Trackers is ["udp://tracker.example:6969/announce", "http://other.example/announce"],
            string.Join(", ", link.Trackers));
        check("magnet: peers named in the link itself",
            link.Peers is [{ Port: 51413 }], string.Join(", ", link.Peers));

        // The base32 spelling is what older clients wrote, and links using it
        // are still in circulation.
        string base32 = ToBase32(Convert.FromHexString(DebianInfoHash));
        check("magnet: the base32 spelling of the same hash",
            MagnetLink.Parse($"magnet:?xt=urn:btih:{base32}").InfoHash.ToString() == DebianInfoHash,
            base32);

        // A link may name the torrent more than one way; the version 1 hash is
        // the one this client can act on.
        check("magnet: a version 2 hash alongside is stepped over",
            MagnetLink.Parse(
                $"magnet:?xt=urn:btmh:1220abcd&xt=urn:btih:{DebianInfoHash}").InfoHash.ToString() == DebianInfoHash, "");

        check("magnet: a link with nothing else still works",
            MagnetLink.TryParse($"magnet:?xt=urn:btih:{DebianInfoHash}", out _), "");

        check("magnet: not a magnet link", !MagnetLink.TryParse("http://example/x.torrent", out _), "");
        check("magnet: no infohash", !MagnetLink.TryParse("magnet:?dn=something", out _), "");
        check("magnet: an infohash of the wrong length",
            !MagnetLink.TryParse("magnet:?xt=urn:btih:abcd", out _), "");
        check("magnet: an infohash that is not hexadecimal",
            !MagnetLink.TryParse($"magnet:?xt=urn:btih:{new string('z', 40)}", out _), "");
    }

    private static void UdpTracker(Action<string, bool, string> check)
    {
        using Socket tracker = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        tracker.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)tracker.LocalEndPoint!).Port;

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(20));

        byte[]? announceSeen = null;
        Task serving = Task.Run(() => ServeAsync(tracker, seen => announceSeen = seen, stop.Token), CancellationToken.None);

        using UdpTrackerClient client = new() { FirstTimeout = TimeSpan.FromSeconds(3) };

        AnnounceRequest request = new()
        {
            InfoHash = InfoHash.Parse(DebianInfoHash),
            PeerId = new PeerId(Encoding.ASCII.GetBytes("-BF1010-abcdefghijkl")),
            Port = 6881,
            Downloaded = 1_000,
            Left = 2_000,
            Uploaded = 3_000,
            Event = TrackerEvent.Started,
            NumWant = 50,
        };

        AnnounceResponse? response = null;
        string failure = "";

        try
        {
            response = client
                .AnnounceAsync(new Uri($"udp://127.0.0.1:{port}/announce"), request, stop.Token)
                .GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            failure = $"{e.GetType().Name}: {e.Message}";
        }

        stop.Cancel();

        check("udp tracker: the announce came back", response != null, failure);
        check("udp tracker: the peers it sent",
            response?.Peers is [{ Port: 6881 }, { Port: 51413 }],
            string.Join(", ", response?.Peers ?? []));
        check("udp tracker: the swarm sizes it reported",
            response is { Seeders: 7, Leechers: 3 }, $"{response?.Seeders}/{response?.Leechers}");
        check("udp tracker: and the interval",
            response?.Interval == TimeSpan.FromSeconds(1800), $"{response?.Interval}");

        // The request's own layout, as the tracker on the other side read it.
        check("udp tracker: the request is 98 bytes", announceSeen?.Length == 98, $"{announceSeen?.Length}");
        check("udp tracker: with the infohash at offset 16",
            announceSeen != null && Convert.ToHexStringLower(announceSeen.AsSpan(16, 20)) == DebianInfoHash,
            announceSeen == null ? "" : Convert.ToHexStringLower(announceSeen.AsSpan(16, 20)));
        check("udp tracker: the peer id at 36",
            announceSeen != null && Encoding.ASCII.GetString(announceSeen, 36, 20) == "-BF1010-abcdefghijkl", "");
        check("udp tracker: the counters at 56, 64 and 72",
            announceSeen != null
            && BinaryPrimitives.ReadInt64BigEndian(announceSeen.AsSpan(56)) == 1_000
            && BinaryPrimitives.ReadInt64BigEndian(announceSeen.AsSpan(64)) == 2_000
            && BinaryPrimitives.ReadInt64BigEndian(announceSeen.AsSpan(72)) == 3_000, "");
        check("udp tracker: the event at 80",
            announceSeen != null && BinaryPrimitives.ReadInt32BigEndian(announceSeen.AsSpan(80)) == 1, "");
        check("udp tracker: and the port at 96",
            announceSeen != null && BinaryPrimitives.ReadUInt16BigEndian(announceSeen.AsSpan(96)) == 6881, "");

        try
        {
            serving.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // The server ends by cancellation.
        }
    }

    /// <summary>
    /// A UDP tracker, as far as BEP 15 describes one: hand out a connection id,
    /// then answer the announce that uses it.
    /// </summary>
    private static async Task ServeAsync(Socket tracker, Action<byte[]> onAnnounce, CancellationToken cancellationToken)
    {
        const long ConnectionId = 0x0BAD_C0DE_1234_5678;
        byte[] buffer = new byte[2048];

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await tracker
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception)
            {
                return;
            }

            if (received.ReceivedBytes < 16)
            {
                continue;
            }

            int action = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(8));
            int transaction = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(12));

            if (action == 0 && BinaryPrimitives.ReadInt64BigEndian(buffer) == 0x0000_0417_2710_1980)
            {
                byte[] reply = new byte[16];
                BinaryPrimitives.WriteInt32BigEndian(reply, 0);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), transaction);
                BinaryPrimitives.WriteInt64BigEndian(reply.AsSpan(8), ConnectionId);

                await tracker.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
                continue;
            }

            if (action == 1 && BinaryPrimitives.ReadInt64BigEndian(buffer) == ConnectionId)
            {
                onAnnounce(buffer[..received.ReceivedBytes]);

                byte[] reply = new byte[20 + 12];
                BinaryPrimitives.WriteInt32BigEndian(reply, 1);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(4), transaction);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(8), 1800);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(12), 3);
                BinaryPrimitives.WriteInt32BigEndian(reply.AsSpan(16), 7);

                // 10.0.0.1:6881 and 10.0.0.2:51413
                byte[] peers = [10, 0, 0, 1, 0x1A, 0xE1, 10, 0, 0, 2, 0xC8, 0xD5];
                peers.CopyTo(reply, 20);

                await tracker.SendToAsync(reply, SocketFlags.None, received.RemoteEndPoint, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static void MetadataMessages(Action<string, bool, string> check)
    {
        // The description is handed over in 16 KiB pieces, with the bytes
        // appended after a bencoded header — so where the header stops is what
        // says where the piece begins.
        byte[] description = new byte[40_000];
        new Random(3).NextBytes(description);

        byte[] first = MetadataExchange.DataMessage(0, description);
        byte[] last = MetadataExchange.DataMessage(2, description);

        check("metadata: a full piece carries 16 KiB after its header",
            first.Length > 16 * 1024 && first.Length < 16 * 1024 + 100, $"{first.Length}");
        check("metadata: the last piece is short",
            last.Length > 40_000 - (2 * 16 * 1024) && last.Length < 40_000 - (2 * 16 * 1024) + 100, $"{last.Length}");

        check("metadata: a piece that does not exist is rejected rather than invented",
            Encoding.ASCII.GetString(MetadataExchange.DataMessage(9, description)).Contains("msg_typei2e"), "");

        check("metadata: a request reads back",
            MetadataExchange.TryReadRequest("d8:msg_typei0e5:piecei4ee"u8.ToArray(), out int piece) && piece == 4,
            $"{piece}");
        check("metadata: a data message is not a request",
            !MetadataExchange.TryReadRequest(first, out _), "");

        check("metadata: this client's handshake offers ut_metadata and says how big the description is",
            MetadataExchange.ReadHandshake(MetadataExchange.Handshake(6881, 40_000).ReadExtended().Payload)
                is { MetadataExtension: 1, MetadataSize: 40_000 }, "");
        check("metadata: a peer that offers nothing is read as offering nothing",
            MetadataExchange.ReadHandshake("de"u8.ToArray()) is { HasMetadata: false }, "");
    }

    private static string ToBase32(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        StringBuilder text = new();
        int buffer = 0;
        int bits = 0;

        foreach (byte b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;

            while (bits >= 5)
            {
                bits -= 5;
                text.Append(alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            text.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return text.ToString();
    }
}
