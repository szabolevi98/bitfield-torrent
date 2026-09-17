namespace Bitfield.Core.Torrents;

/// <summary>Thrown when a file is bencode but not a usable torrent.</summary>
public sealed class MetainfoException : Exception
{
    public MetainfoException(string message) : base(message)
    {
    }
}
