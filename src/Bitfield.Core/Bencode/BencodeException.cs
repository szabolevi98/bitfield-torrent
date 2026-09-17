namespace Bitfield.Core.Bencode;

/// <summary>Thrown when a buffer is not valid bencode.</summary>
public sealed class BencodeException : Exception
{
    public BencodeException(string message) : base(message)
    {
    }
}
