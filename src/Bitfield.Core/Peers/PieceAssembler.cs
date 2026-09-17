using System.Security.Cryptography;

namespace Bitfield.Core.Peers;

/// <summary>
/// Collects one piece out of the blocks peers send back, and decides whether it
/// is the piece it was supposed to be.
///
/// That decision is the only thing standing between the swarm and the file on
/// disk: any peer can send any bytes, and a piece whose SHA-1 does not match
/// the one in the torrent is thrown away rather than written. Nothing leaves
/// here unverified.
/// </summary>
public sealed class PieceAssembler
{
    private readonly byte[] _data;
    private readonly byte[] _expectedHash;
    private readonly bool[] _received;

    public PieceAssembler(int index, int length, ReadOnlySpan<byte> expectedHash)
    {
        if (length <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "a piece has a length");
        }

        if (expectedHash.Length != 20)
        {
            throw new ArgumentException("a piece hash is 20 bytes", nameof(expectedHash));
        }

        Index = index;
        _data = new byte[length];
        _expectedHash = expectedHash.ToArray();
        _received = new bool[(length + BlockRequest.BlockSize - 1) / BlockRequest.BlockSize];
    }

    public int Index { get; }

    public int Length => _data.Length;

    public int BlockCount => _received.Length;

    public int ReceivedBlocks { get; private set; }

    public bool IsComplete => ReceivedBlocks == BlockCount;

    /// <summary>The assembled bytes. Only meaningful once <see cref="Verify"/> has passed.</summary>
    public ReadOnlyMemory<byte> Data => _data;

    /// <summary>
    /// Every block still missing, in order. The last one is short whenever the
    /// piece does not divide evenly, which for the last piece of a torrent is
    /// the usual case.
    /// </summary>
    public IEnumerable<BlockRequest> MissingBlocks()
    {
        for (int block = 0; block < _received.Length; block++)
        {
            if (!_received[block])
            {
                yield return BlockAt(block);
            }
        }
    }

    /// <summary>Whether one block has already arrived.</summary>
    public bool HasBlock(int block) => _received[block];

    public BlockRequest BlockAt(int block)
    {
        int begin = block * BlockRequest.BlockSize;
        return new BlockRequest(Index, begin, Math.Min(BlockRequest.BlockSize, Length - begin));
    }

    /// <summary>
    /// Takes a block a peer sent. Returns false for one that was not asked for
    /// or has already arrived — which happens honestly in endgame mode, where
    /// the same block is requested from several peers at once, and dishonestly
    /// from a peer trying to overwrite what it already sent.
    /// </summary>
    public bool Add(int begin, ReadOnlySpan<byte> block)
    {
        if (begin < 0 || begin % BlockRequest.BlockSize != 0 || begin >= Length)
        {
            return false;
        }

        int index = begin / BlockRequest.BlockSize;
        if (_received[index] || block.Length != BlockAt(index).Length)
        {
            return false;
        }

        block.CopyTo(_data.AsSpan(begin));
        _received[index] = true;
        ReceivedBlocks++;
        return true;
    }

    /// <summary>
    /// Whether the assembled piece is the one the torrent describes. False
    /// means some peer sent bad data and the piece has to be fetched again.
    /// </summary>
    public bool Verify()
    {
        if (!IsComplete)
        {
            return false;
        }

        Span<byte> hash = stackalloc byte[20];
        SHA1.HashData(_data, hash);
        return hash.SequenceEqual(_expectedHash);
    }

    /// <summary>Forgets every block received, to fetch the piece again.</summary>
    public void Reset()
    {
        Array.Clear(_received);
        ReceivedBlocks = 0;
    }

    public override string ToString() => $"piece {Index}: {ReceivedBlocks}/{BlockCount} blocks";
}
