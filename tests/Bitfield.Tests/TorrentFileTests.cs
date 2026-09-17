using System.Reflection;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Real torrent files, published by the projects that distribute the content:
/// two single-file Linux images and one multi-file item from the Internet
/// Archive. They are embedded in this assembly so the checks do not depend on
/// a download that will eventually be retired.
///
/// The expected values were derived with a separate implementation written for
/// the purpose rather than read out of this one, which is the only way this
/// check can fail when the parser is wrong. The infohash in particular is the
/// interesting one: it is the SHA-1 of the info dictionary's own bytes, so a
/// parser that reconstructs the dictionary instead of remembering where it was
/// produces a plausible looking hash that no peer will recognise.
/// </summary>
internal static class TorrentFileTests
{
    private sealed record Expected(
        string Resource,
        string InfoHash,
        string Name,
        int PieceLength,
        long TotalLength,
        int PieceCount,
        int FileCount,
        bool IsPrivate,
        string FirstPath,
        string Announce);

    private static readonly Expected[] Torrents =
    [
        new(
            Resource: "debian-13.7.0-amd64-netinst.iso.torrent",
            InfoHash: "7acf8fb590b2060dd9c3146ef770169d593433b0",
            Name: "debian-13.7.0-amd64-netinst.iso",
            PieceLength: 262_144,
            TotalLength: 792_723_456,
            PieceCount: 3_024,
            FileCount: 1,
            IsPrivate: false,
            FirstPath: "debian-13.7.0-amd64-netinst.iso",
            Announce: "http://bttracker.debian.org:6969/announce"),
        new(
            Resource: "ubuntu-24.04.4-desktop-amd64.iso.torrent",
            InfoHash: "01c137287d6f0ed05a56742dae794f632c79ff3d",
            Name: "ubuntu-24.04.4-desktop-amd64.iso",
            PieceLength: 262_144,
            TotalLength: 6_655_619_072,
            PieceCount: 25_390,
            FileCount: 1,
            IsPrivate: false,
            FirstPath: "ubuntu-24.04.4-desktop-amd64.iso",
            Announce: "https://torrent.ubuntu.com/announce"),
        new(
            Resource: "popeye-meets-sindbad_archive.torrent",
            InfoHash: "1d88f1afaa46b5b736133bfe59472de453a0c086",
            Name: "PopeyeTheSailorMeetsSindbadTheSailor",
            PieceLength: 524_288,
            TotalLength: 70_622_084,
            PieceCount: 135,
            FileCount: 4,
            IsPrivate: false,
            FirstPath: "PopeyeTheSailorMeetsSindbadTheSailor/PopeyeTheSailorMeetsSindbadTheSailor_meta.xml",
            Announce: "http://bt1.archive.org:6969/announce"),
    ];

    public static void Run(Action<string, bool, string> check)
    {
        foreach (Expected expected in Torrents)
        {
            byte[]? file = Read(expected.Resource);
            if (file == null)
            {
                check($"{expected.Resource}: embedded in the test assembly", false, "resource not found");
                continue;
            }

            Metainfo torrent = Metainfo.Parse(file);

            check($"{expected.Resource}: infohash",
                torrent.InfoHash.ToString() == expected.InfoHash, torrent.InfoHash.ToString());
            check($"{expected.Resource}: name", torrent.Name == expected.Name, torrent.Name);
            check($"{expected.Resource}: piece length",
                torrent.PieceLength == expected.PieceLength, $"{torrent.PieceLength}");
            check($"{expected.Resource}: total length",
                torrent.TotalLength == expected.TotalLength, $"{torrent.TotalLength}");
            check($"{expected.Resource}: piece count",
                torrent.PieceCount == expected.PieceCount, $"{torrent.PieceCount}");
            check($"{expected.Resource}: file count",
                torrent.Files.Count == expected.FileCount, $"{torrent.Files.Count}");
            check($"{expected.Resource}: private flag",
                torrent.IsPrivate == expected.IsPrivate, $"{torrent.IsPrivate}");
            check($"{expected.Resource}: first path",
                torrent.Files[0].Path == expected.FirstPath, torrent.Files[0].Path);
            check($"{expected.Resource}: announce",
                torrent.AnnounceTiers.Count > 0 && torrent.AnnounceTiers[0][0] == expected.Announce,
                torrent.AnnounceTiers.Count > 0 ? torrent.AnnounceTiers[0][0] : "none");

            // The file's own bytes are the reference: re-encoding the parsed
            // tree has to reproduce them exactly, or the infohash above was
            // luck rather than correctness.
            byte[] reencoded = BencodeWriter.Encode(BencodeParser.Parse(file));
            check($"{expected.Resource}: re-encodes to the same {file.Length} bytes",
                reencoded.AsSpan().SequenceEqual(file),
                $"got {reencoded.Length} bytes");

            // And the recorded info range has to be the dictionary itself.
            check($"{expected.Resource}: the raw info range hashes to the infohash",
                InfoHash.ComputeSha1(torrent.RawInfo.Span).ToString() == expected.InfoHash, "");
            check($"{expected.Resource}: the raw info range starts a dictionary and ends with its terminator",
                torrent.RawInfo.Span[0] == (byte)'d' && torrent.RawInfo.Span[^1] == (byte)'e', "");

            // Offsets have to describe one continuous stream, with the last
            // file ending exactly at the total length.
            long offset = 0;
            bool contiguous = true;
            foreach (TorrentFile entry in torrent.Files)
            {
                contiguous &= entry.Offset == offset;
                offset += entry.Length;
            }

            check($"{expected.Resource}: file offsets are contiguous",
                contiguous && offset == torrent.TotalLength, $"{offset} vs {torrent.TotalLength}");

            check($"{expected.Resource}: every piece hash is readable",
                torrent.PieceHash(0).Length == 20 && torrent.PieceHash(torrent.PieceCount - 1).Length == 20, "");

            check($"{expected.Resource}: the piece lengths add up to the content",
                SumOfPieceLengths(torrent) == torrent.TotalLength, $"{SumOfPieceLengths(torrent)}");
        }
    }

    private static long SumOfPieceLengths(Metainfo torrent)
    {
        long total = 0;
        for (int i = 0; i < torrent.PieceCount; i++)
        {
            total += torrent.PieceLengthAt(i);
        }

        return total;
    }

    private static byte[]? Read(string resource)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null)
        {
            return null;
        }

        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
