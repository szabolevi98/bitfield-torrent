namespace Bitfield.Core.Torrents;

/// <summary>
/// One file inside a torrent, with the offset it starts at in the torrent's
/// single continuous byte stream. Pieces are cut from that stream without
/// regard for where files end, so this offset is what the storage layer needs
/// to turn a piece into writes.
/// </summary>
/// <param name="Path">
/// The path relative to the torrent's own directory, always with forward
/// slashes. Every component has been checked: nothing empty, no <c>.</c> or
/// <c>..</c>, no separators, no reserved device names. A torrent is untrusted
/// input, and this path decides where bytes land on disk.
/// </param>
/// <param name="Length">The file's length in bytes. May be zero.</param>
/// <param name="Offset">Where the file starts in the torrent's byte stream.</param>
/// <param name="IsPadding">
/// A padding file (BEP 47), inserted only so that the next real file starts on
/// a piece boundary. It occupies its stretch of the stream but is not something
/// the user asked for, and does not have to be written out.
/// </param>
public sealed record TorrentFile(string Path, long Length, long Offset, bool IsPadding = false)
{
    /// <summary>One past the last byte of this file in the torrent's byte stream.</summary>
    public long End => Offset + Length;

    public override string ToString() => $"{Path} ({Length} bytes at {Offset})";
}
