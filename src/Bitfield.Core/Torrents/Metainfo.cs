using System.Text;
using Bitfield.Core.Bencode;

namespace Bitfield.Core.Torrents;

/// <summary>
/// A parsed <c>.torrent</c> file: what is being downloaded, how it is cut into
/// pieces, and who to ask for it.
///
/// A torrent describes its content as one continuous byte stream. Files are
/// laid end to end in that stream, and pieces are cut from it at fixed
/// intervals without regard for where one file ends and the next begins, so a
/// piece routinely spans two or more files. The per-file offsets recorded here
/// are what later turns a verified piece into writes on disk.
/// </summary>
public sealed class Metainfo
{
    private readonly ReadOnlyMemory<byte> _pieceHashes;

    private Metainfo(
        InfoHash infoHash,
        ReadOnlyMemory<byte> rawInfo,
        string name,
        int pieceLength,
        ReadOnlyMemory<byte> pieceHashes,
        IReadOnlyList<TorrentFile> files,
        long totalLength,
        bool isPrivate,
        bool isSingleFile,
        IReadOnlyList<IReadOnlyList<string>> announceTiers,
        IReadOnlyList<string> webSeeds,
        IReadOnlyList<(string Host, int Port)> dhtNodes,
        string? comment,
        string? createdBy,
        DateTimeOffset? createdOn)
    {
        InfoHash = infoHash;
        RawInfo = rawInfo;
        Name = name;
        PieceLength = pieceLength;
        _pieceHashes = pieceHashes;
        Files = files;
        TotalLength = totalLength;
        IsPrivate = isPrivate;
        IsSingleFile = isSingleFile;
        AnnounceTiers = announceTiers;
        WebSeeds = webSeeds;
        DhtNodes = dhtNodes;
        Comment = comment;
        CreatedBy = createdBy;
        CreatedOn = createdOn;
    }

    /// <summary>The torrent's identity: SHA-1 over <see cref="RawInfo"/>.</summary>
    public InfoHash InfoHash { get; }

    /// <summary>
    /// The info dictionary's bytes as they appeared in the file. Kept because
    /// the infohash is defined over them, and because a peer that asks for the
    /// metadata over the extension protocol expects these bytes back.
    /// </summary>
    public ReadOnlyMemory<byte> RawInfo { get; }

    /// <summary>
    /// The suggested name: the file's name for a single-file torrent, the
    /// containing directory's name otherwise. Advisory — the user may rename
    /// the download — but it is already included in every path in
    /// <see cref="Files"/>.
    /// </summary>
    public string Name { get; }

    public int PieceLength { get; }

    public long TotalLength { get; }

    public int PieceCount => _pieceHashes.Length / InfoHash.Size;

    /// <summary>
    /// The last piece is short unless the content divides evenly, which it
    /// almost never does.
    /// </summary>
    public int LastPieceLength => (int)(TotalLength - (long)(PieceCount - 1) * PieceLength);

    /// <summary>
    /// Every file in stream order, including any BEP 47 padding files. Paths are
    /// relative to the download directory and already include <see cref="Name"/>
    /// where the torrent has a directory, so joining the download directory with
    /// a path is all the storage layer has to do. Components are checked for the
    /// forms that would escape that directory; characters that are merely
    /// illegal on Windows are left alone, because rejecting them here would make
    /// ordinary torrents unusable — mapping them to a legal filename belongs to
    /// the storage layer.
    /// </summary>
    public IReadOnlyList<TorrentFile> Files { get; }

    public bool IsSingleFile { get; }

    /// <summary>
    /// Set by <c>private: 1</c>. The torrent is to be found through its tracker
    /// and nowhere else: no DHT, no peer exchange, no local discovery. This is
    /// not advisory, and a client that ignores it gets its user banned.
    /// </summary>
    public bool IsPrivate { get; }

    /// <summary>
    /// Trackers grouped into tiers: each tier is tried in order, and within a
    /// tier the entries are shuffled and the first that answers is promoted.
    /// Empty for a torrent that carries no tracker at all.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> AnnounceTiers { get; }

    /// <summary>Plain HTTP sources for the same content (BEP 19).</summary>
    public IReadOnlyList<string> WebSeeds { get; }

    /// <summary>DHT nodes to start from in a trackerless torrent (BEP 5).</summary>
    public IReadOnlyList<(string Host, int Port)> DhtNodes { get; }

    public string? Comment { get; }

    public string? CreatedBy { get; }

    public DateTimeOffset? CreatedOn { get; }

    /// <summary>The expected SHA-1 of one piece.</summary>
    public ReadOnlySpan<byte> PieceHash(int index)
    {
        if ((uint)index >= (uint)PieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the torrent has {PieceCount} pieces");
        }

        return _pieceHashes.Span.Slice(index * InfoHash.Size, InfoHash.Size);
    }

    /// <summary>The length of one piece; only the last one differs.</summary>
    public int PieceLengthAt(int index)
    {
        if ((uint)index >= (uint)PieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(index), index, $"the torrent has {PieceCount} pieces");
        }

        return index == PieceCount - 1 ? LastPieceLength : PieceLength;
    }

    public static Metainfo Load(string path) => Parse(File.ReadAllBytes(path));

    /// <summary>
    /// Builds a torrent from an info dictionary fetched from peers, which is
    /// all a magnet link ever gets. The bytes are wrapped in the file a torrent
    /// would have been, verbatim, so the infohash computed from them is the one
    /// that was asked for — copying them into a rebuilt dictionary would be the
    /// same mistake as hashing a re-encoding.
    /// </summary>
    public static Metainfo FromInfoDictionary(ReadOnlyMemory<byte> rawInfo, IEnumerable<string>? trackers = null)
    {
        List<string> urls = [.. (trackers ?? []).Where(url => url.Length > 0)];

        using MemoryStream file = new();
        file.WriteByte((byte)'d');

        // Keys in a torrent file are sorted, and "announce-list" comes before
        // "info".
        if (urls.Count > 0)
        {
            BList tiers = new([.. urls.Select(url => (BValue)new BList([new BString(url)]))]);
            byte[] encoded = BencodeWriter.Encode(tiers);

            file.Write("13:announce-list"u8);
            file.Write(encoded);
        }

        file.Write("4:info"u8);
        file.Write(rawInfo.Span);
        file.WriteByte((byte)'e');

        return Parse(file.ToArray());
    }

    public static Metainfo Parse(ReadOnlyMemory<byte> torrentFile)
    {
        BValue root;
        try
        {
            root = BencodeParser.Parse(torrentFile);
        }
        catch (BencodeException e)
        {
            throw new MetainfoException($"not a torrent file: {e.Message}");
        }

        if (root is not BDictionary metainfo)
        {
            throw new MetainfoException("the file's top-level value is not a dictionary");
        }

        if (metainfo.Get("info") is not BDictionary info)
        {
            throw new MetainfoException("the file has no info dictionary");
        }

        if (!info.HasSource)
        {
            throw new MetainfoException("the info dictionary has no recorded source range");
        }

        ReadOnlyMemory<byte> rawInfo = torrentFile.Slice(info.SourceStart, info.SourceLength);
        InfoHash infoHash = InfoHash.ComputeSha1(rawInfo.Span);

        long metaVersion = info.GetInteger("meta version") ?? 1;
        if (metaVersion > 1 && info.GetByteString("pieces") == null)
        {
            throw new MetainfoException(
                $"this is a version {metaVersion} torrent (BEP 52) with no version 1 data, which is not supported yet");
        }

        string name = ReadName(info);
        int pieceLength = ReadPieceLength(info);
        ReadOnlyMemory<byte> pieceHashes = ReadPieceHashes(info);

        (IReadOnlyList<TorrentFile> files, bool isSingleFile) = ReadFiles(info, name);
        long totalLength = files.Count == 0 ? 0 : files[^1].End;

        if (totalLength <= 0)
        {
            throw new MetainfoException("the torrent has no content");
        }

        int pieceCount = pieceHashes.Length / InfoHash.Size;
        long expectedPieces = (totalLength + pieceLength - 1) / pieceLength;
        if (pieceCount != expectedPieces)
        {
            throw new MetainfoException(
                $"{totalLength} bytes in {pieceLength} byte pieces needs {expectedPieces} hashes, the file has {pieceCount}");
        }

        return new Metainfo(
            infoHash,
            rawInfo,
            name,
            pieceLength,
            pieceHashes,
            files,
            totalLength,
            info.GetInteger("private") == 1,
            isSingleFile,
            ReadAnnounceTiers(metainfo),
            ReadWebSeeds(metainfo),
            ReadDhtNodes(metainfo),
            metainfo.GetString("comment"),
            metainfo.GetString("created by"),
            metainfo.GetInteger("creation date") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null);
    }

    private static string ReadName(BDictionary info)
    {
        // "name.utf-8" is what clients wrote before UTF-8 was the norm; where
        // both exist it is the more trustworthy of the two.
        BString? raw = info.GetByteString("name.utf-8") ?? info.GetByteString("name");
        if (raw == null)
        {
            throw new MetainfoException("the info dictionary has no name");
        }

        string name = raw.Text;
        if (!IsSafeComponent(name))
        {
            throw new MetainfoException($"the torrent's name \"{name}\" is not a usable directory name");
        }

        return name;
    }

    private static int ReadPieceLength(BDictionary info)
    {
        long pieceLength = info.GetInteger("piece length")
            ?? throw new MetainfoException("the info dictionary has no piece length");

        // 16 KiB is the block size peers trade in, so a piece cannot sensibly be
        // smaller; the upper bound is simply far past anything in use.
        if (pieceLength < 16 * 1024 || pieceLength > 256L * 1024 * 1024)
        {
            throw new MetainfoException($"a piece length of {pieceLength} bytes is out of range");
        }

        return (int)pieceLength;
    }

    private static ReadOnlyMemory<byte> ReadPieceHashes(BDictionary info)
    {
        BString pieces = info.GetByteString("pieces")
            ?? throw new MetainfoException("the info dictionary has no piece hashes");

        if (pieces.Length == 0 || pieces.Length % InfoHash.Size != 0)
        {
            throw new MetainfoException(
                $"the piece hashes are {pieces.Length} bytes, which is not a whole number of {InfoHash.Size} byte hashes");
        }

        return pieces.Bytes;
    }

    private static (IReadOnlyList<TorrentFile> Files, bool IsSingleFile) ReadFiles(BDictionary info, string name)
    {
        BList? fileList = info.GetList("files");
        long? singleLength = info.GetInteger("length");

        if (fileList != null && singleLength != null)
        {
            throw new MetainfoException("the info dictionary has both a length and a file list");
        }

        if (fileList == null)
        {
            if (singleLength == null)
            {
                throw new MetainfoException("the info dictionary has neither a length nor a file list");
            }

            if (singleLength < 0)
            {
                throw new MetainfoException($"the file's length is {singleLength}");
            }

            return ([new TorrentFile(name, singleLength.Value, 0)], true);
        }

        if (fileList.Count == 0)
        {
            throw new MetainfoException("the file list is empty");
        }

        List<TorrentFile> files = new(fileList.Count);
        long offset = 0;

        foreach (BValue entry in fileList.Items)
        {
            if (entry is not BDictionary file)
            {
                throw new MetainfoException("an entry in the file list is not a dictionary");
            }

            long length = file.GetInteger("length")
                ?? throw new MetainfoException("an entry in the file list has no length");

            if (length < 0)
            {
                throw new MetainfoException($"an entry in the file list has a length of {length}");
            }

            BList components = (file.GetList("path.utf-8") ?? file.GetList("path"))
                ?? throw new MetainfoException("an entry in the file list has no path");

            files.Add(new TorrentFile(BuildPath(name, components), length, offset, IsPaddingFile(file)));
            offset += length;
        }

        return (files, false);
    }

    /// <summary>
    /// Joins the torrent's name with a file's path components, rejecting the
    /// forms that would let a torrent write outside the download directory.
    /// </summary>
    private static string BuildPath(string name, BList components)
    {
        if (components.Count == 0)
        {
            throw new MetainfoException("a file in the torrent has an empty path");
        }

        StringBuilder path = new(name);

        foreach (BValue component in components.Items)
        {
            if (component is not BString part)
            {
                throw new MetainfoException("a path component is not a string");
            }

            string text = part.Text;
            if (!IsSafeComponent(text))
            {
                throw new MetainfoException($"the path component \"{text}\" would escape the download directory");
            }

            path.Append('/').Append(text);
        }

        return path.ToString();
    }

    /// <summary>
    /// A component is dangerous if it is empty, is a relative directory, or
    /// carries a separator or a null — those are the forms that let a path climb
    /// out of the directory it was meant to stay in. Characters that are merely
    /// illegal on Windows are someone else's problem: plenty of real torrents
    /// contain them, and refusing to parse those torrents would be worse than
    /// substituting the characters when the file is finally created.
    /// </summary>
    private static bool IsSafeComponent(string component)
    {
        if (component.Length == 0 || component == "." || component == "..")
        {
            return false;
        }

        foreach (char c in component)
        {
            if (c is '/' or '\\' or '\0')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// BEP 47 padding: a file that exists only to push the next real file onto a
    /// piece boundary, so that pieces do not straddle two files and can be
    /// shared between torrents.
    /// </summary>
    private static bool IsPaddingFile(BDictionary file)
    {
        BString? attributes = file.GetByteString("attr");
        if (attributes == null)
        {
            return false;
        }

        foreach (byte attribute in attributes.Span)
        {
            if (attribute == (byte)'p')
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<IReadOnlyList<string>> ReadAnnounceTiers(BDictionary metainfo)
    {
        List<IReadOnlyList<string>> tiers = [];

        if (metainfo.GetList("announce-list") is { } announceList)
        {
            foreach (BValue tierValue in announceList.Items)
            {
                if (tierValue is not BList tier)
                {
                    continue;
                }

                List<string> urls = [];
                foreach (BValue url in tier.Items)
                {
                    if (url is BString text && text.Length > 0)
                    {
                        urls.Add(text.Text);
                    }
                }

                if (urls.Count > 0)
                {
                    tiers.Add(urls);
                }
            }
        }

        // The single announce URL is the older form, and a fallback for a file
        // whose announce-list turned out to be empty or malformed.
        if (tiers.Count == 0 && metainfo.GetString("announce") is { Length: > 0 } announce)
        {
            tiers.Add([announce]);
        }

        return tiers;
    }

    private static IReadOnlyList<string> ReadWebSeeds(BDictionary metainfo)
    {
        BValue? urlList = metainfo.Get("url-list");

        return urlList switch
        {
            BString single when single.Length > 0 => [single.Text],
            BList list => [.. list.Items.OfType<BString>().Where(u => u.Length > 0).Select(u => u.Text)],
            _ => [],
        };
    }

    private static IReadOnlyList<(string Host, int Port)> ReadDhtNodes(BDictionary metainfo)
    {
        if (metainfo.GetList("nodes") is not { } nodes)
        {
            return [];
        }

        List<(string, int)> result = [];
        foreach (BValue value in nodes.Items)
        {
            if (value is BList { Count: 2 } node
                && node[0] is BString host
                && node[1] is BInteger port
                && host.Length > 0
                && port.Value is > 0 and <= ushort.MaxValue)
            {
                result.Add((host.Text, (int)port.Value));
            }
        }

        return result;
    }

    public override string ToString() =>
        $"{Name} ({TotalLength} bytes, {PieceCount} pieces of {PieceLength}, {InfoHash})";
}
