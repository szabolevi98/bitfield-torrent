using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Storage;

/// <summary>
/// What a torrent knew about itself when it was last stopped, so that a restart
/// does not begin by hashing every byte on disk again.
///
/// It is only ever a shortcut. Anything that does not add up — a different
/// torrent, a file that changed size or was touched since — throws the whole
/// thing away and falls back to hashing, because the cost of being wrong here
/// is telling peers about pieces this client does not have.
/// </summary>
public sealed class ResumeData
{
    private const int Version = 1;

    public required InfoHash InfoHash { get; init; }

    public required PieceBitfield Pieces { get; init; }

    public long Downloaded { get; init; }

    public long Uploaded { get; init; }

    /// <summary>The length and last-write time each file had when this was saved.</summary>
    public required IReadOnlyList<(long Length, long ModifiedTicks)> Files { get; init; }

    /// <summary>Where a torrent's resume file lives, named after its infohash.</summary>
    public static string PathFor(string directory, InfoHash infoHash) =>
        Path.Combine(directory, $"{infoHash}.resume");

    public static ResumeData Capture(Metainfo torrent, TorrentStorage storage, PieceBitfield pieces, long downloaded, long uploaded)
    {
        List<(long, long)> files = [];

        foreach (StoredFile file in storage.Files)
        {
            FileInfo info = new(file.FullPath);
            files.Add(info.Exists
                ? (info.Length, info.LastWriteTimeUtc.Ticks)
                : (-1, -1));
        }

        return new ResumeData
        {
            InfoHash = torrent.InfoHash,
            Pieces = pieces,
            Downloaded = downloaded,
            Uploaded = uploaded,
            Files = files,
        };
    }

    public byte[] Encode()
    {
        List<BValue> files = [];
        foreach ((long length, long modified) in Files)
        {
            files.Add(new BList([new BInteger(length), new BInteger(modified)]));
        }

        BDictionary root = Dictionary(
            ("downloaded", new BInteger(Downloaded)),
            ("files", new BList(files)),
            ("infohash", new BString(InfoHash.ToArray())),
            ("pieces", new BString(Pieces.ToArray())),
            ("piece count", new BInteger(Pieces.PieceCount)),
            ("uploaded", new BInteger(Uploaded)),
            ("version", new BInteger(Version)));

        return BencodeWriter.Encode(root);
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        // Written beside the real file and moved into place, so that being
        // interrupted here leaves the previous resume file intact rather than
        // a truncated one that would be believed.
        string temporary = path + ".new";
        File.WriteAllBytes(temporary, Encode());
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// Reads a resume file and checks it against what is actually on disk.
    /// Returns null for anything that does not line up, which means the caller
    /// should hash the files instead.
    /// </summary>
    public static ResumeData? TryLoad(string path, Metainfo torrent, TorrentStorage storage)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (BencodeParser.Parse(File.ReadAllBytes(path)) is not BDictionary root
                || root.GetInteger("version") != Version
                || root.GetByteString("infohash") is not { Length: InfoHash.Size } hash
                || new InfoHash(hash.Span) != torrent.InfoHash
                || root.GetInteger("piece count") != torrent.PieceCount
                || root.GetByteString("pieces") is not { } pieces
                || root.GetList("files") is not { } files
                || files.Count != storage.Files.Count)
            {
                return null;
            }

            List<(long, long)> recorded = [];
            for (int i = 0; i < files.Count; i++)
            {
                if (files[i] is not BList { Count: 2 } entry
                    || entry[0] is not BInteger length
                    || entry[1] is not BInteger modified)
                {
                    return null;
                }

                FileInfo info = new(storage.Files[i].FullPath);
                long actualLength = info.Exists ? info.Length : -1;
                long actualModified = info.Exists ? info.LastWriteTimeUtc.Ticks : -1;

                if (actualLength != length.Value || actualModified != modified.Value)
                {
                    return null;
                }

                recorded.Add((length.Value, modified.Value));
            }

            return new ResumeData
            {
                InfoHash = torrent.InfoHash,
                Pieces = PieceBitfield.FromBytes(pieces.Span, torrent.PieceCount),
                Downloaded = root.GetInteger("downloaded") ?? 0,
                Uploaded = root.GetInteger("uploaded") ?? 0,
                Files = recorded,
            };
        }
        catch (Exception e) when (e is BencodeException or PeerProtocolException or IOException)
        {
            return null;
        }
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(e => e.Key, StringComparer.Ordinal)
            .Select(e => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(Encoding.ASCII.GetBytes(e.Key), e.Value))]);
}
