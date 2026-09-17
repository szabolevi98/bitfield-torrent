using Bitfield.Core.Torrents;

namespace Bitfield.Core.Download;

/// <summary>How much a file is wanted, if at all.</summary>
public enum FilePriority
{
    /// <summary>Not wanted. Its pieces are never asked for.</summary>
    Skip = 0,

    Low = 1,

    Normal = 2,

    High = 3,
}

/// <summary>
/// Turns per-file priorities into per-piece ones, which is what the picker can
/// actually act on.
///
/// The awkwardness is that pieces and files do not line up. A piece routinely
/// spans two or three files, so it may belong to a file that is not wanted and
/// one that is — and then it is wanted, because the part that matters cannot be
/// had without it. By the same argument a piece takes the highest priority of
/// the files it touches: the file that wants it most decides when it arrives.
///
/// This is also where "complete" stops meaning "every piece". A torrent with a
/// file set aside is finished when every piece it still wants is held, and a
/// client that waited for the rest would never finish at all.
/// </summary>
public sealed class PiecePriorities
{
    private readonly FilePriority[] _pieces;

    public PiecePriorities(Metainfo torrent, IReadOnlyList<FilePriority>? files = null)
    {
        Torrent = torrent;
        Files = files ?? [.. Enumerable.Repeat(FilePriority.Normal, torrent.Files.Count)];

        if (Files.Count != torrent.Files.Count)
        {
            throw new ArgumentException(
                $"the torrent has {torrent.Files.Count} files, got {Files.Count} priorities", nameof(files));
        }

        _pieces = new FilePriority[torrent.PieceCount];

        for (int i = 0; i < torrent.Files.Count; i++)
        {
            TorrentFile file = torrent.Files[i];
            FilePriority priority = file.IsPadding ? FilePriority.Skip : Files[i];

            if (priority == FilePriority.Skip || file.Length == 0)
            {
                continue;
            }

            // Every piece this file touches, from the one holding its first
            // byte to the one holding its last.
            int first = (int)(file.Offset / torrent.PieceLength);
            int last = (int)((file.End - 1) / torrent.PieceLength);

            for (int piece = first; piece <= last && piece < _pieces.Length; piece++)
            {
                if (priority > _pieces[piece])
                {
                    _pieces[piece] = priority;
                }
            }
        }

        WantedCount = _pieces.Count(priority => priority != FilePriority.Skip);
        WantedBytes = ComputeWantedBytes(torrent, Files);
    }

    public Metainfo Torrent { get; }

    public IReadOnlyList<FilePriority> Files { get; }

    /// <summary>How many pieces are worth having at all.</summary>
    public int WantedCount { get; }

    /// <summary>How many bytes the wanted files add up to, which is what progress is out of.</summary>
    public long WantedBytes { get; }

    public bool WantsEverything => WantedCount == Torrent.PieceCount;

    public bool Wanted(int piece) => _pieces[piece] != FilePriority.Skip;

    public FilePriority PriorityOf(int piece) => _pieces[piece];

    /// <summary>
    /// Whether a file is only partly wanted: it shares a piece with a file that
    /// is set aside, so part of it arrives either way. Worth knowing because it
    /// is why a skipped file is sometimes not empty.
    /// </summary>
    public bool SharesPieces(int file)
    {
        TorrentFile entry = Torrent.Files[file];
        if (entry.Length == 0)
        {
            return false;
        }

        int first = (int)(entry.Offset / Torrent.PieceLength);
        int last = (int)((entry.End - 1) / Torrent.PieceLength);

        // A file shares its first or last piece whenever that piece starts
        // before it does or ends after it does.
        return (long)first * Torrent.PieceLength < entry.Offset
            || ((long)last * Torrent.PieceLength) + Torrent.PieceLengthAt(last) > entry.End;
    }

    private static long ComputeWantedBytes(Metainfo torrent, IReadOnlyList<FilePriority> files)
    {
        long total = 0;

        for (int i = 0; i < torrent.Files.Count; i++)
        {
            if (files[i] != FilePriority.Skip && !torrent.Files[i].IsPadding)
            {
                total += torrent.Files[i].Length;
            }
        }

        return total;
    }
}
