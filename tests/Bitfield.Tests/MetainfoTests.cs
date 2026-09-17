using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Torrent files built here on purpose, so that the awkward shapes — a file
/// list whose offsets have to line up, a path that tries to climb out of the
/// download directory, a piece count that does not match the content — can be
/// checked without waiting to meet one in the wild.
/// </summary>
internal static class MetainfoTests
{
    private const int PieceLength = 16 * 1024;

    public static void Run(Action<string, bool, string> check)
    {
        SingleFile(check);
        MultiFile(check);
        Pieces(check);
        RawInfoIsPreserved(check);
        Private(check);
        Trackers(check);
        DangerousPaths(check);
        Malformed(check);
    }

    private static void SingleFile(Action<string, bool, string> check)
    {
        Metainfo torrent = Parse(SingleFileTorrent("holiday.mp4", 40_000));

        check("single file: name", torrent.Name == "holiday.mp4", torrent.Name);
        check("single file: reported as one file", torrent.IsSingleFile && torrent.Files.Count == 1, "");
        check("single file: length", torrent.TotalLength == 40_000, $"{torrent.TotalLength}");
        check("single file: the path is the name",
            torrent.Files[0] is { Path: "holiday.mp4", Offset: 0, Length: 40_000 }, torrent.Files[0].Path);
        check("single file: infohash is the SHA-1 of the info dictionary",
            torrent.InfoHash == InfoHash.ComputeSha1(torrent.RawInfo.Span), "");
    }

    private static void MultiFile(Action<string, bool, string> check)
    {
        Metainfo torrent = Parse(MultiFileTorrent(
            "album",
            [("cover.jpg", 1_000, false), ("disc one/track.flac", 30_000, false), ("notes.txt", 500, false)]));

        check("multi file: not reported as a single file", !torrent.IsSingleFile, "");
        check("multi file: file count", torrent.Files.Count == 3, $"{torrent.Files.Count}");
        check("multi file: total length", torrent.TotalLength == 31_500, $"{torrent.TotalLength}");

        // Files are laid end to end in one stream, and this is the arithmetic
        // the storage layer will later use to turn a piece into writes.
        check("multi file: offsets follow one another",
            torrent.Files[0].Offset == 0 && torrent.Files[1].Offset == 1_000 && torrent.Files[2].Offset == 31_000,
            string.Join(", ", torrent.Files.Select(f => f.Offset)));

        check("multi file: the torrent's name leads every path",
            torrent.Files[1].Path == "album/disc one/track.flac", torrent.Files[1].Path);

        check("multi file: the end of the last file is the total length",
            torrent.Files[^1].End == torrent.TotalLength, "");

        // BEP 47 padding exists only to push the next real file onto a piece
        // boundary; it takes up its share of the stream but nobody wants it.
        Metainfo padded = Parse(MultiFileTorrent(
            "padded", [("real.bin", 20_000, false), (".pad/12768", 12_768, true)]));

        check("multi file: padding files are marked",
            padded.Files is [{ IsPadding: false }, { IsPadding: true }], "");
        check("multi file: padding still occupies the stream",
            padded.TotalLength == 32_768, $"{padded.TotalLength}");
    }

    private static void Pieces(Action<string, bool, string> check)
    {
        // 40,000 bytes in 16 KiB pieces is two full pieces and a short one.
        Metainfo torrent = Parse(SingleFileTorrent("holiday.mp4", 40_000));

        check("pieces: count", torrent.PieceCount == 3, $"{torrent.PieceCount}");
        check("pieces: full pieces have the piece length",
            torrent.PieceLengthAt(0) == PieceLength && torrent.PieceLengthAt(1) == PieceLength, "");
        check("pieces: the last piece is short",
            torrent.LastPieceLength == 40_000 - (2 * PieceLength) && torrent.PieceLengthAt(2) == 7_232,
            $"{torrent.LastPieceLength}");
        check("pieces: hashes are twenty bytes each",
            torrent.PieceHash(1).Length == 20, "");
        check("pieces: hashes come back in order",
            torrent.PieceHash(1)[0] == 1 && torrent.PieceHash(2)[0] == 2, "");

        bool threw = false;
        try
        {
            torrent.PieceHash(3);
        }
        catch (ArgumentOutOfRangeException)
        {
            threw = true;
        }

        check("pieces: asking past the last one throws", threw, "");

        // Content that divides evenly has no short piece at all.
        Metainfo exact = Parse(SingleFileTorrent("exact.bin", PieceLength * 2));
        check("pieces: an exact multiple has no short last piece",
            exact is { PieceCount: 2, LastPieceLength: PieceLength }, $"{exact.LastPieceLength}");
    }

    /// <summary>
    /// The reason the parser keeps byte ranges at all. This torrent's info
    /// dictionary has its keys out of order and carries a key this client knows
    /// nothing about — both of which are things real files do. A client that
    /// rebuilt the dictionary from what it understood, or that tidied the key
    /// order on the way out, would compute an infohash that no peer and no
    /// tracker recognises, and the download would simply never start.
    /// </summary>
    private static void RawInfoIsPreserved(Action<string, bool, string> check)
    {
        const string announce = "http://tracker.example/announce";
        string pieces = new('A', 20);

        // Keys deliberately out of order: "pieces" sorts after "piece length",
        // and "extra" before both.
        string info =
            "d6:lengthi16384e4:name5:a.bin6:pieces20:" + pieces + "12:piece lengthi16384e5:extra3:abce";
        string file = $"d8:announce{announce.Length}:{announce}4:info{info}e";

        byte[] bytes = Encoding.ASCII.GetBytes(file);
        int infoStart = file.IndexOf(info, StringComparison.Ordinal);

        Metainfo torrent = Metainfo.Parse(bytes);

        check("raw info: an info dictionary with unsorted and unknown keys still parses",
            torrent is { Name: "a.bin", TotalLength: 16_384, PieceCount: 1 }, "");

        check("raw info: the recorded range is exactly the bytes in the file",
            torrent.RawInfo.Span.SequenceEqual(bytes.AsSpan(infoStart, info.Length)),
            $"{torrent.RawInfo.Length} bytes at some other place");

        check("raw info: the infohash is the SHA-1 of those bytes",
            torrent.InfoHash == InfoHash.ComputeSha1(bytes.AsSpan(infoStart, info.Length)), "");

        check("raw info: the unsorted keys are reported as such",
            BencodeParser.Parse(bytes) is BDictionary root
            && root.GetDictionary("info") is { KeysAreSorted: false }, "");

        check("raw info: the whole file re-encodes unchanged",
            BencodeWriter.Encode(BencodeParser.Parse(bytes)).AsSpan().SequenceEqual(bytes), "");

        // Sorting those keys is a different torrent as far as the network is
        // concerned, which is the whole point.
        string sortedInfo =
            "d5:extra3:abc6:lengthi16384e4:name5:a.bin12:piece lengthi16384e6:pieces20:" + pieces + "e";
        Metainfo sorted = Metainfo.Parse(
            Encoding.ASCII.GetBytes($"d8:announce{announce.Length}:{announce}4:info{sortedInfo}e"));

        check("raw info: tidying the key order would change the torrent's identity",
            sorted.InfoHash != torrent.InfoHash,
            $"both came out as {torrent.InfoHash}");
    }

    private static void Private(Action<string, bool, string> check)
    {
        check("private: absent means public", !Parse(SingleFileTorrent("a.bin", 1_000)).IsPrivate, "");

        check("private: private: 1 is honoured",
            Parse(SingleFileTorrent("a.bin", 1_000, isPrivate: true)).IsPrivate, "");
    }

    private static void Trackers(Action<string, bool, string> check)
    {
        BDictionary torrent = Dictionary(
            ("announce", new BString("http://one.example/announce")),
            ("announce-list", new BList([
                new BList([new BString("http://one.example/announce")]),
                new BList([new BString("http://two.example/announce"), new BString("http://three.example/announce")]),
            ])),
            ("url-list", new BString("https://mirror.example/files/")),
            ("info", InfoDictionary("a.bin", 1_000)));

        Metainfo parsed = Parse(torrent);

        check("trackers: tiers are kept apart",
            parsed.AnnounceTiers.Count == 2 && parsed.AnnounceTiers[1].Count == 2,
            string.Join(" | ", parsed.AnnounceTiers.Select(t => string.Join(", ", t))));

        check("trackers: web seeds are read", parsed.WebSeeds is ["https://mirror.example/files/"], "");

        // The single announce key is the older form and the fallback.
        Metainfo single = Parse(Dictionary(
            ("announce", new BString("http://only.example/announce")),
            ("info", InfoDictionary("a.bin", 1_000))));

        check("trackers: a lone announce becomes one tier",
            single.AnnounceTiers is [["http://only.example/announce"]], "");

        // A torrent may carry no tracker at all and be found through the DHT.
        Metainfo trackerless = Parse(Dictionary(("info", InfoDictionary("a.bin", 1_000))));
        check("trackers: a trackerless torrent parses", trackerless.AnnounceTiers.Count == 0, "");
    }

    private static void DangerousPaths(Action<string, bool, string> check)
    {
        // A torrent decides where bytes land on disk, which makes its paths the
        // most obviously hostile input this client handles.
        Rejects(check, "a path component of ..", MultiFileTorrent("x", [("../escape.txt", 10, false)]));
        Rejects(check, "a path component of .", MultiFileTorrentRaw("x", [[".", "escape.txt"]]));
        Rejects(check, "an empty path component", MultiFileTorrentRaw("x", [[""], ["a.txt"]]));
        Rejects(check, "a backslash inside a component", MultiFileTorrentRaw("x", [["..\\escape.txt"]]));
        Rejects(check, "an empty path list", MultiFileTorrentRaw("x", [[]]));
        Rejects(check, "a name of ..", SingleFileTorrent("..", 1_000));

        // Characters that are only illegal on Windows are kept: real torrents
        // carry them, and substituting them belongs to the storage layer.
        Metainfo colon = Parse(MultiFileTorrentRaw("x", [["episode 1: pilot.mkv"]]));
        check("paths: a colon in a name is kept",
            colon.Files[0].Path == "x/episode 1: pilot.mkv", colon.Files[0].Path);
    }

    private static void Malformed(Action<string, bool, string> check)
    {
        Rejects(check, "a top-level value that is not a dictionary", new BList([]));
        Rejects(check, "a missing info dictionary", Dictionary(("announce", new BString("http://x/"))));
        Rejects(check, "a missing name", Dictionary(("info", Dictionary(
            ("length", new BInteger(1_000)),
            ("piece length", new BInteger(PieceLength)),
            ("pieces", PieceHashes(1))))));
        Rejects(check, "a missing piece length", Dictionary(("info", Dictionary(
            ("length", new BInteger(1_000)),
            ("name", new BString("a.bin")),
            ("pieces", PieceHashes(1))))));
        Rejects(check, "piece hashes that are not a multiple of twenty", Dictionary(("info", Dictionary(
            ("length", new BInteger(1_000)),
            ("name", new BString("a.bin")),
            ("piece length", new BInteger(PieceLength)),
            ("pieces", new BString(new byte[25]))))));
        Rejects(check, "a piece count that does not match the content", Dictionary(("info", Dictionary(
            ("length", new BInteger(100_000)),
            ("name", new BString("a.bin")),
            ("piece length", new BInteger(PieceLength)),
            ("pieces", PieceHashes(2))))));
        Rejects(check, "both a length and a file list", Dictionary(("info", Dictionary(
            ("length", new BInteger(1_000)),
            ("files", new BList([Dictionary(
                ("length", new BInteger(1_000)),
                ("path", new BList([new BString("a.bin")])))])),
            ("name", new BString("a.bin")),
            ("piece length", new BInteger(PieceLength)),
            ("pieces", PieceHashes(1))))));
        Rejects(check, "a piece length below the block size", Dictionary(("info", Dictionary(
            ("length", new BInteger(1_000)),
            ("name", new BString("a.bin")),
            ("piece length", new BInteger(512)),
            ("pieces", PieceHashes(2))))));

        // A version 2 torrent describes its content with a different structure
        // entirely. Saying so is better than reading it as an empty version 1.
        Rejects(check, "a version 2 torrent with no version 1 data", Dictionary(("info", Dictionary(
            ("meta version", new BInteger(2)),
            ("name", new BString("a.bin")),
            ("piece length", new BInteger(PieceLength))))));

        bool threw = false;
        try
        {
            Metainfo.Parse(Encoding.ASCII.GetBytes("this is not bencode"));
        }
        catch (MetainfoException)
        {
            threw = true;
        }

        check("rejects a file that is not bencode at all", threw, "");
    }

    // ------------------------------------------------------------- builders

    private static Metainfo Parse(BDictionary torrent) => Metainfo.Parse(BencodeWriter.Encode(torrent));

    private static void Rejects(Action<string, bool, string> check, string name, BValue torrent)
    {
        string? message = null;
        try
        {
            Metainfo.Parse(BencodeWriter.Encode(torrent));
        }
        catch (MetainfoException e)
        {
            message = e.Message;
        }

        check($"rejects {name}", message != null, message == null ? "it was accepted" : "");
    }

    private static BDictionary SingleFileTorrent(string name, long length, bool isPrivate = false)
    {
        return Dictionary(
            ("announce", new BString("http://tracker.example/announce")),
            ("info", InfoDictionary(name, length, isPrivate)));
    }

    private static BDictionary InfoDictionary(string name, long length, bool isPrivate = false)
    {
        List<(string, BValue)> entries =
        [
            ("length", new BInteger(length)),
            ("name", new BString(name)),
            ("piece length", new BInteger(PieceLength)),
            ("pieces", PieceHashes(PieceCountFor(length))),
        ];

        if (isPrivate)
        {
            entries.Add(("private", new BInteger(1)));
        }

        return Dictionary([.. entries]);
    }

    private static BDictionary MultiFileTorrent(string name, (string Path, long Length, bool Padding)[] files)
    {
        List<BValue> entries = [];
        foreach ((string path, long length, bool padding) in files)
        {
            List<(string, BValue)> file =
            [
                ("length", new BInteger(length)),
                ("path", new BList([.. path.Split('/').Select(part => new BString(part))])),
            ];

            if (padding)
            {
                file.Add(("attr", new BString("p")));
            }

            entries.Add(Dictionary([.. file]));
        }

        long total = files.Sum(f => f.Length);

        return Dictionary(
            ("announce", new BString("http://tracker.example/announce")),
            ("info", Dictionary(
                ("files", new BList(entries)),
                ("name", new BString(name)),
                ("piece length", new BInteger(PieceLength)),
                ("pieces", PieceHashes(PieceCountFor(total))))));
    }

    /// <summary>
    /// Builds a file list from path components given literally, so that a
    /// component may be anything at all — including the forms the parser is
    /// supposed to refuse.
    /// </summary>
    private static BDictionary MultiFileTorrentRaw(string name, string[][] paths)
    {
        List<BValue> entries = [.. paths.Select(components => (BValue)Dictionary(
            ("length", new BInteger(1_000)),
            ("path", new BList([.. components.Select(c => new BString(c))]))))];

        return Dictionary(
            ("announce", new BString("http://tracker.example/announce")),
            ("info", Dictionary(
                ("files", new BList(entries)),
                ("name", new BString(name)),
                ("piece length", new BInteger(PieceLength)),
                ("pieces", PieceHashes(PieceCountFor(Math.Max(1_000L * paths.Length, 1)))))));
    }

    private static int PieceCountFor(long length) => (int)((length + PieceLength - 1) / PieceLength);

    /// <summary>
    /// Stand-in hashes whose first byte is the piece index, so a check can tell
    /// which piece it was handed.
    /// </summary>
    private static BString PieceHashes(int count)
    {
        byte[] hashes = new byte[count * 20];
        for (int i = 0; i < count; i++)
        {
            hashes[i * 20] = (byte)i;
        }

        return new BString(hashes);
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(Encoding.UTF8.GetBytes(e.Key), e.Value))]);
}
