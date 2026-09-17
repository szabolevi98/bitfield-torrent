namespace Bitfield.Core.Peers;

/// <summary>
/// Which pieces are held — by this client, or by a peer that sent its bitfield.
///
/// The wire format is one bit per piece, most significant bit first, padded out
/// to whole bytes. The padding has to be zero: a peer that sets those spare
/// bits is claiming pieces the torrent does not have, and the specification
/// says to drop it rather than guess what it meant.
///
/// This is also what the piece map draws.
/// </summary>
public sealed class PieceBitfield
{
    private readonly byte[] _bits;

    public PieceBitfield(int pieceCount)
    {
        if (pieceCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pieceCount), pieceCount, "a torrent has at least one piece");
        }

        PieceCount = pieceCount;
        _bits = new byte[ByteCount(pieceCount)];
    }

    private PieceBitfield(byte[] bits, int pieceCount, int setCount)
    {
        _bits = bits;
        PieceCount = pieceCount;
        SetCount = setCount;
    }

    public int PieceCount { get; }

    /// <summary>How many pieces are held.</summary>
    public int SetCount { get; private set; }

    public bool IsComplete => SetCount == PieceCount;

    public bool IsEmpty => SetCount == 0;

    /// <summary>How many bytes a bitfield for this many pieces occupies.</summary>
    public static int ByteCount(int pieceCount) => (pieceCount + 7) / 8;

    public bool this[int piece]
    {
        get
        {
            if ((uint)piece >= (uint)PieceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(piece), piece, $"the torrent has {PieceCount} pieces");
            }

            return (_bits[piece >> 3] & (0x80 >> (piece & 7))) != 0;
        }
    }

    public void Set(int piece)
    {
        if ((uint)piece >= (uint)PieceCount)
        {
            throw new ArgumentOutOfRangeException(nameof(piece), piece, $"the torrent has {PieceCount} pieces");
        }

        int mask = 0x80 >> (piece & 7);
        if ((_bits[piece >> 3] & mask) != 0)
        {
            return;
        }

        _bits[piece >> 3] |= (byte)mask;
        SetCount++;
    }

    /// <summary>
    /// Reads a peer's bitfield message. The length has to match the torrent
    /// exactly — a peer sending a bitfield for a different torrent, or one
    /// rounded to a different number of bytes, is not one to trade with.
    /// </summary>
    public static PieceBitfield FromBytes(ReadOnlySpan<byte> bytes, int pieceCount)
    {
        int expected = ByteCount(pieceCount);
        if (bytes.Length != expected)
        {
            throw new PeerProtocolException(
                $"a bitfield for {pieceCount} pieces is {expected} bytes, got {bytes.Length}");
        }

        int spare = (expected * 8) - pieceCount;
        if (spare > 0 && (bytes[^1] & ((1 << spare) - 1)) != 0)
        {
            throw new PeerProtocolException(
                $"the bitfield's {spare} spare bits are set, which claims pieces the torrent does not have");
        }

        int setCount = 0;
        foreach (byte b in bytes)
        {
            setCount += System.Numerics.BitOperations.PopCount(b);
        }

        return new PieceBitfield(bytes.ToArray(), pieceCount, setCount);
    }

    /// <summary>A bitfield with every piece held, as a seed would send.</summary>
    public static PieceBitfield Complete(int pieceCount)
    {
        PieceBitfield bitfield = new(pieceCount);
        for (int i = 0; i < pieceCount; i++)
        {
            bitfield.Set(i);
        }

        return bitfield;
    }

    public byte[] ToArray() => _bits.ToArray();

    public ReadOnlySpan<byte> Span => _bits;

    public override string ToString() => $"{SetCount}/{PieceCount} pieces";
}
