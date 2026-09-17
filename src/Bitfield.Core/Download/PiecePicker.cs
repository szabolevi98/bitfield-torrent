using Bitfield.Core.Peers;

namespace Bitfield.Core.Download;

/// <summary>
/// Decides which piece a peer should be asked for next, and keeps two peers
/// from spending the connection on the same one.
///
/// This is the simple version: the lowest-numbered piece still wanted that the
/// peer actually holds. It is enough to finish a torrent, and it is deliberately
/// not what a real client does — asking for pieces in order means the rarest
/// ones are left until last, when the peers holding them may be gone. Rarest
/// first comes with the piece picker proper.
///
/// The one exception is the end. With a handful of pieces left, a download can
/// sit waiting on one slow peer while everything else idles, so near the finish
/// the same piece is handed to several peers at once and whichever answers
/// first wins.
/// </summary>
public sealed class PiecePicker
{
    /// <summary>How many pieces may be left before duplicate requests are allowed.</summary>
    private const int EndgameThreshold = 4;

    private readonly Lock _gate = new();
    private readonly HashSet<int> _inProgress = [];

    public PiecePicker(PieceBitfield have)
    {
        Have = have;
        PieceCount = have.PieceCount;
    }

    public PieceBitfield Have { get; }

    public int PieceCount { get; }

    public bool IsComplete
    {
        get
        {
            lock (_gate)
            {
                return Have.IsComplete;
            }
        }
    }

    public int Remaining
    {
        get
        {
            lock (_gate)
            {
                return PieceCount - Have.SetCount;
            }
        }
    }

    /// <summary>
    /// Reserves a piece for a peer, or returns null when it holds nothing this
    /// client still wants.
    /// </summary>
    public int? Take(PieceBitfield available)
    {
        lock (_gate)
        {
            bool endgame = PieceCount - Have.SetCount <= EndgameThreshold;

            for (int piece = 0; piece < PieceCount; piece++)
            {
                if (Have[piece] || !available[piece])
                {
                    continue;
                }

                if (_inProgress.Contains(piece) && !endgame)
                {
                    continue;
                }

                _inProgress.Add(piece);
                return piece;
            }

            return null;
        }
    }

    /// <summary>Gives a piece back, after a peer choked, failed or disconnected.</summary>
    public void Release(int piece)
    {
        lock (_gate)
        {
            _inProgress.Remove(piece);
        }
    }

    /// <summary>Records a piece as held, once it has been verified and written.</summary>
    public void Completed(int piece)
    {
        lock (_gate)
        {
            _inProgress.Remove(piece);
            Have.Set(piece);
        }
    }

    public bool Wants(int piece)
    {
        lock (_gate)
        {
            return !Have[piece];
        }
    }
}
