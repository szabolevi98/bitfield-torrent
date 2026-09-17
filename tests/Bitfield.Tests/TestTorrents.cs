using System.Security.Cryptography;
using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Builds real torrents over real content, so that checks about storage and
/// downloading can work with correct piece hashes rather than stand-ins.
/// </summary>
internal static class TestTorrents
{
    public sealed record Built(Metainfo Torrent, byte[] Content, byte[] File);

    /// <summary>
    /// A multi-file torrent whose content is random bytes, with the piece
    /// hashes computed from that content.
    /// </summary>
    public static Built Build(
        string name,
        (string Path, long Length, bool Padding)[] files,
        int pieceLength = 16 * 1024,
        int seed = 1)
    {
        long total = files.Sum(f => f.Length);
        byte[] content = new byte[total];
        new Random(seed).NextBytes(content);

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

        BDictionary torrent = Dictionary(
            ("announce", new BString("http://tracker.example/announce")),
            ("info", Dictionary(
                ("files", new BList(entries)),
                ("name", new BString(name)),
                ("piece length", new BInteger(pieceLength)),
                ("pieces", new BString(PieceHashes(content, pieceLength))))));

        byte[] file2 = BencodeWriter.Encode(torrent);
        return new Built(Metainfo.Parse(file2), content, file2);
    }

    /// <summary>A single-file torrent, which takes the simpler layout.</summary>
    public static Built BuildSingle(string name, int length, int pieceLength = 16 * 1024, int seed = 1)
    {
        byte[] content = new byte[length];
        new Random(seed).NextBytes(content);

        BDictionary torrent = Dictionary(
            ("announce", new BString("http://tracker.example/announce")),
            ("info", Dictionary(
                ("length", new BInteger(length)),
                ("name", new BString(name)),
                ("piece length", new BInteger(pieceLength)),
                ("pieces", new BString(PieceHashes(content, pieceLength))))));

        byte[] file = BencodeWriter.Encode(torrent);
        return new Built(Metainfo.Parse(file), content, file);
    }

    private static byte[] PieceHashes(byte[] content, int pieceLength)
    {
        int count = (content.Length + pieceLength - 1) / pieceLength;
        byte[] hashes = new byte[count * 20];

        for (int piece = 0; piece < count; piece++)
        {
            int begin = piece * pieceLength;
            int length = Math.Min(pieceLength, content.Length - begin);
            SHA1.HashData(content.AsSpan(begin, length), hashes.AsSpan(piece * 20, 20));
        }

        return hashes;
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(Encoding.UTF8.GetBytes(e.Key), e.Value))]);
}
