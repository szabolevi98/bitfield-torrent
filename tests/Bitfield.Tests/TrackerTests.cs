using System.Net;
using System.Text;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Tests;

/// <summary>
/// The tracker conversation, as far as it can be had without a network: the
/// exact bytes of the request, and every shape the reply comes back in.
/// </summary>
internal static class TrackerTests
{
    private const string DebianInfoHash = "7acf8fb590b2060dd9c3146ef770169d593433b0";

    public static void Run(Action<string, bool, string> check)
    {
        PeerIds(check);
        RequestUrls(check);
        CompactPeers(check);
        DictionaryPeers(check);
        ReplyFields(check);
        BadReplies(check);
        Tiers(check);
    }

    private static void PeerIds(Action<string, bool, string> check)
    {
        PeerId generated = PeerId.Generate();
        string text = generated.ToString();

        check("peer id: twenty bytes", generated.ToArray().Length == 20, "");
        check("peer id: names this client", text.StartsWith("-BF0100-", StringComparison.Ordinal), text);
        check("peer id: the tail is random",
            PeerId.Generate() != PeerId.Generate(), "two generated ids came out the same");
        check("peer id: reads back its own client name",
            generated.ClientName() == "Bitfield 0.1.0", generated.ClientName());

        // Other clients' ids are what a peer list will mostly be made of.
        check("peer id: recognises qBittorrent",
            Id("-qB5020-abcdefghijkl").ClientName() == "qBittorrent 5.0.2", "");
        check("peer id: recognises Transmission",
            Id("-TR4060-abcdefghijkl").ClientName() == "Transmission 4.0.6", "");
        check("peer id: an unknown prefix falls back to its code",
            Id("-ZZ1234-abcdefghijkl").ClientName() == "ZZ 1.2.3.4", "");
        check("peer id: a version written in letters is not mistranslated",
            Id("-lt0D60-abcdefghijkl").ClientName() == "libtorrent 0D60", "");
        check("peer id: a peer id in no known style is not guessed at",
            Id("M7-7-2--abcdefghijkl").ClientName() == "unknown", "");
        check("peer id: unprintable bytes show as dots",
            new PeerId([.. Enumerable.Repeat((byte)0, 20)]).ToString() == new string('.', 20), "");
    }

    private static void RequestUrls(Action<string, bool, string> check)
    {
        AnnounceRequest request = new()
        {
            InfoHash = InfoHash.Parse(DebianInfoHash),
            PeerId = Id("-BF0100-abcdefghijkl"),
            Port = 6881,
            Left = 792_723_456,
            Event = TrackerEvent.Started,
        };

        Uri uri = request.ToUri(new Uri("http://bttracker.debian.org:6969/announce"));

        // Twenty raw bytes of SHA-1, escaped byte by byte. The expected string
        // was produced with a different encoder; the whole point of this check
        // is that a general-purpose one, which would treat these bytes as text,
        // gets it wrong.
        const string expected =
            "http://bttracker.debian.org:6969/announce"
            + "?info_hash=z%CF%8F%B5%90%B2%06%0D%D9%C3%14n%F7p%16%9DY43%B0"
            + "&peer_id=-BF0100-abcdefghijkl"
            + "&port=6881&uploaded=0&downloaded=0&left=792723456&compact=1&event=started";

        check("announce url: built byte for byte", uri.AbsoluteUri == expected, uri.AbsoluteUri);

        // A tracker whose URL already carries something — a passkey, most
        // often — must keep it.
        Uri withQuery = request.ToUri(new Uri("https://tracker.example/announce?passkey=abc123"));
        check("announce url: an existing query is kept and added to",
            withQuery.AbsoluteUri.StartsWith("https://tracker.example/announce?passkey=abc123&info_hash=", StringComparison.Ordinal),
            withQuery.AbsoluteUri);

        check("announce url: a routine announce carries no event",
            !(request with { Event = TrackerEvent.None })
                .ToUri(new Uri("http://t.example/a")).AbsoluteUri.Contains("event="), "");

        check("announce url: stopping says so",
            (request with { Event = TrackerEvent.Stopped })
                .ToUri(new Uri("http://t.example/a")).AbsoluteUri.EndsWith("&event=stopped", StringComparison.Ordinal), "");

        check("announce url: optional fields are left out when unset",
            !uri.AbsoluteUri.Contains("numwant=") && !uri.AbsoluteUri.Contains("key="), "");

        check("announce url: optional fields appear when set",
            (request with { NumWant = 80, Key = 0xDEADBEEF })
                .ToUri(new Uri("http://t.example/a")).AbsoluteUri.Contains("&numwant=80&key=3735928559"), "");

        // A seed has nothing left, and the tracker treats it differently.
        check("announce url: a seed reports nothing left",
            (request with { Left = 0 }).ToUri(new Uri("http://t.example/a")).AbsoluteUri.Contains("&left=0&"), "");
    }

    private static void CompactPeers(Action<string, bool, string> check)
    {
        // Four address bytes and a big-endian port, repeated (BEP 23).
        byte[] compact =
        [
            192, 168, 1, 10, 0x1A, 0xE1, // 192.168.1.10:6881
            10, 0, 0, 1, 0x1A, 0xE9,     // 10.0.0.1:6889
        ];

        AnnounceResponse response = Parse(Dictionary($"8:completei5e10:incompletei7e8:intervali1800e5:peers12:{Latin1(compact)}"));

        check("compact peers: both were read", response.Peers.Count == 2, $"{response.Peers.Count}");
        check("compact peers: address and port",
            response.Peers[0].ToString() == "192.168.1.10:6881" && response.Peers[1].ToString() == "10.0.0.1:6889",
            string.Join(", ", response.Peers));

        // Sixteen address bytes and a port for IPv6, in a separate key.
        byte[] compact6 = [.. Enumerable.Repeat((byte)0, 15), 1, 0x1A, 0xE1];
        AnnounceResponse v6 = Parse(Dictionary($"8:intervali900e6:peers6{compact6.Length}:{Latin1(compact6)}"));

        check("compact peers: IPv6 is read from peers6",
            v6.Peers is [{ } only] && only.ToString() == "[::1]:6881",
            string.Join(", ", v6.Peers));

        // Trackers pad, repeat themselves and send placeholders.
        byte[] noisy =
        [
            192, 168, 1, 10, 0x1A, 0xE1, // once
            192, 168, 1, 10, 0x1A, 0xE1, // and again
            0, 0, 0, 0, 0, 0,            // a placeholder
            8, 8, 8, 8, 0, 0,            // port zero
        ];

        AnnounceResponse cleaned = Parse(Dictionary($"8:intervali900e5:peers24:{Latin1(noisy)}"));
        check("compact peers: duplicates, zero addresses and zero ports are dropped",
            cleaned.Peers is [{ } single] && single.ToString() == "192.168.1.10:6881",
            string.Join(", ", cleaned.Peers));
    }

    private static void DictionaryPeers(Action<string, bool, string> check)
    {
        // The older, larger form: a list of dictionaries.
        AnnounceResponse response = Parse(Dictionary(
            "8:intervali1800e5:peersld2:ip9:127.0.0.14:porti6881eed2:ip7:8.8.8.84:porti51413eee"));

        check("dictionary peers: both were read", response.Peers.Count == 2, $"{response.Peers.Count}");
        check("dictionary peers: address and port",
            response.Peers[0].ToString() == "127.0.0.1:6881" && response.Peers[1].ToString() == "8.8.8.8:51413",
            string.Join(", ", response.Peers));

        // Some trackers put a hostname there, which is not something to connect
        // to without resolving it first, so it is skipped rather than guessed.
        AnnounceResponse hostname = Parse(Dictionary(
            "8:intervali1800e5:peersld2:ip12:peer.example4:porti6881eee"));
        check("dictionary peers: a hostname is skipped rather than guessed at",
            hostname.Peers.Count == 0, $"{hostname.Peers.Count}");
    }

    private static void ReplyFields(Action<string, bool, string> check)
    {
        AnnounceResponse response = Parse(Dictionary(
            "8:completei42e10:incompletei7e8:intervali1800e12:min intervali900e5:peers0:"
            + "10:tracker id4:abcd15:warning message5:hello"));

        check("reply: interval", response.Interval == TimeSpan.FromMinutes(30), $"{response.Interval}");
        check("reply: minimum interval", response.MinInterval == TimeSpan.FromMinutes(15), $"{response.MinInterval}");
        check("reply: seeders and leechers",
            response is { Seeders: 42, Leechers: 7 }, $"{response.Seeders}/{response.Leechers}");
        check("reply: tracker id", response.TrackerId == "abcd", response.TrackerId ?? "none");
        check("reply: warning", response.Warning == "hello", response.Warning ?? "none");
        check("reply: an empty peer list is not an error", response.Peers.Count == 0, "");

        AnnounceResponse sparse = Parse(Dictionary("5:peers0:"));
        check("reply: a missing interval falls back to half an hour",
            sparse.Interval == TimeSpan.FromMinutes(30), $"{sparse.Interval}");
        check("reply: a missing minimum interval stays unset", sparse.MinInterval == null, "");

        // A tracker that does not report swarm sizes is not a swarm of zero,
        // and a bare opentracker — Debian's, for one — reports neither.
        check("reply: unreported swarm sizes stay unknown rather than becoming zero",
            sparse is { Seeders: null, Leechers: null }, $"{sparse.Seeders}/{sparse.Leechers}");
        check("reply: a genuine zero is kept as zero",
            Parse(Dictionary("8:completei0e5:peers0:")) is { Seeders: 0 }, "");

        AnnounceResponse absurd = Parse(Dictionary("8:intervali-5e5:peers0:"));
        check("reply: an absurd interval is replaced rather than obeyed",
            absurd.Interval == TimeSpan.FromMinutes(30), $"{absurd.Interval}");
    }

    private static void BadReplies(Action<string, bool, string> check)
    {
        // The tracker saying no is different from the reply being broken, and
        // its wording is what tells a user their client is not welcome.
        TrackerException? refusal = Catch(() => Parse(Dictionary("14:failure reason20:Unregistered torrent")));
        check("bad reply: a failure reason is raised",
            refusal?.Message == "Unregistered torrent", refusal?.Message ?? "nothing was thrown");
        check("bad reply: a failure reason is marked as coming from the tracker",
            refusal?.FromTracker == true, "");

        check("bad reply: not bencode", Catch(() => Parse("this is not bencode")) != null, "");
        check("bad reply: not a dictionary", Catch(() => Parse("li1ee")) != null, "");
        check("bad reply: a compact list that does not divide evenly",
            Catch(() => Parse(Dictionary("8:intervali900e5:peers7:1234567"))) != null, "");
    }

    private static void Tiers(Action<string, bool, string> check)
    {
        AnnounceRequest request = new()
        {
            InfoHash = InfoHash.Parse(DebianInfoHash),
            PeerId = PeerId.Generate(),
            Port = 6881,
        };

        // Shuffling is off here so the order under test is the order written.
        TrackerTiers tiers = new(
            [["http://dead.example/a", "http://alive.example/a"], ["http://backup.example/a"]],
            shuffle: false);

        check("tiers: trackers are counted across tiers", tiers.Count == 3, $"{tiers.Count}");

        List<Uri> attempted = [];
        Task<AnnounceResponse> Announce(Uri tracker, AnnounceRequest _, CancellationToken __)
        {
            attempted.Add(tracker);
            return tracker.Host == "alive.example"
                ? Task.FromResult(Empty())
                : throw new TrackerException($"{tracker.Host} is down");
        }

        (Uri tracker, _) = tiers.AnnounceAsync(request, Announce).GetAwaiter().GetResult();

        check("tiers: falls through to the tracker that answers",
            tracker.Host == "alive.example", tracker.Host);
        check("tiers: the dead one was tried first",
            attempted is [{ Host: "dead.example" }, { Host: "alive.example" }],
            string.Join(", ", attempted.Select(u => u.Host)));

        // And it is remembered, so the next announce does not retry the dead
        // one first.
        attempted.Clear();
        tiers.AnnounceAsync(request, Announce).GetAwaiter().GetResult();
        check("tiers: the working tracker is promoted to the front of its tier",
            attempted is [{ Host: "alive.example" }], string.Join(", ", attempted.Select(u => u.Host)));

        // When everything is down the user deserves all of the reasons, not the
        // last one.
        TrackerTiers allDown = new([["http://one.example/a"], ["http://two.example/a"]], shuffle: false);
        TrackerException? failure = Catch(() => allDown
            .AnnounceAsync(request, (t, _, _) => throw new TrackerException($"{t.Host} is down"))
            .GetAwaiter().GetResult());

        check("tiers: every failure is reported",
            failure != null
            && failure.Message.Contains("one.example is down")
            && failure.Message.Contains("two.example is down"),
            failure?.Message ?? "nothing was thrown");

        check("tiers: a torrent with no trackers says so",
            Catch(() => new TrackerTiers([]).AnnounceAsync(request, (_, _, _) => Task.FromResult(Empty()))
                .GetAwaiter().GetResult()) != null, "");

        check("tiers: unparseable tracker urls are dropped",
            new TrackerTiers([["not a url", "http://good.example/a"]], shuffle: false).Count == 1, "");
    }

    // ------------------------------------------------------------- helpers

    private static PeerId Id(string text) => new(Encoding.ASCII.GetBytes(text));

    private static AnnounceResponse Empty() => new() { Interval = TimeSpan.FromMinutes(30), Peers = [] };

    private static AnnounceResponse Parse(string reply) =>
        AnnounceResponse.Parse(Encoding.Latin1.GetBytes(reply));

    /// <summary>Wraps bencoded entries in the dictionary a reply always is.</summary>
    private static string Dictionary(string entries) => $"d{entries}e";

    /// <summary>
    /// Raw bytes as a string, one char per byte, so that binary fields can be
    /// written inline in these bencoded replies.
    /// </summary>
    private static string Latin1(byte[] bytes) => Encoding.Latin1.GetString(bytes);

    private static TrackerException? Catch(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (TrackerException e)
        {
            return e;
        }
    }
}
