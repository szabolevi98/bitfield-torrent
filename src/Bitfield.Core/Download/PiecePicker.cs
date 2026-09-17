using Bitfield.Core.Peers;

namespace Bitfield.Core.Download;

/// <summary>
/// Decides which piece a peer should be asked for next.
///
/// Three rules, in order:
///
/// <b>Rarest first.</b> Of the pieces this client still wants and the peer
/// holds, the one the fewest connected peers have. Asking in order instead
/// leaves the rare pieces until last, by which time the few peers holding them
/// may be gone and the download stalls a hair from the end. Spreading the rare
/// pieces early also gives the swarm more places to get them, which is the
/// difference between a torrent that stays alive and one that does not.
///
/// <b>Random while there is nothing to trade.</b> Rarest first needs to know
/// what is rare, and at the start this client has spoken to almost nobody. Worse,
/// every new client would agree on the same rare piece and pile onto whoever has
/// it. So the first few pieces are picked at random, which gets something worth
/// trading in hand quickly.
///
/// <b>Duplicate at the end.</b> With a handful of pieces left, a download can
/// sit waiting on one slow peer while every other connection idles. Near the
/// finish the same piece goes to several peers and whichever answers first wins.
/// </summary>
public sealed class PiecePicker
{
    /// <summary>How many pieces may be left before duplicate requests are allowed.</summary>
    private const int EndgameThreshold = 4;

    /// <summary>How many pieces to pick at random before rarest first takes over.</summary>
    private const int RandomFirstPieces = 4;

    private readonly Lock _gate = new();
    private readonly HashSet<int> _inProgress = [];
    private readonly int[] _availability;
    private readonly Random _random;

    public PiecePicker(PieceBitfield have, Random? random = null)
    {
        Have = have;
        PieceCount = have.PieceCount;
        _availability = new int[PieceCount];
        _random = random ?? Random.Shared;
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

    /// <summary>How many connected peers hold a piece.</summary>
    public int AvailabilityOf(int piece)
    {
        lock (_gate)
        {
            return _availability[piece];
        }
    }

    /// <summary>Counts everything a peer just said it holds.</summary>
    public void AddAvailability(PieceBitfield available)
    {
        lock (_gate)
        {
            for (int piece = 0; piece < PieceCount; piece++)
            {
                if (available[piece])
                {
                    _availability[piece]++;
                }
            }
        }
    }

    public void AddAvailability(int piece)
    {
        lock (_gate)
        {
            _availability[piece]++;
        }
    }

    /// <summary>
    /// Takes a departing peer's pieces back out of the counts. Without this a
    /// piece stays as common as the day the peer holding it left, and rarest
    /// first slowly stops meaning anything.
    /// </summary>
    public void RemoveAvailability(PieceBitfield available)
    {
        lock (_gate)
        {
            for (int piece = 0; piece < PieceCount; piece++)
            {
                if (available[piece] && _availability[piece] > 0)
                {
                    _availability[piece]--;
                }
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
            bool rarestFirst = Have.SetCount >= RandomFirstPieces;

            int chosen = -1;
            int rarest = int.MaxValue;
            int ties = 0;

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

                int rarity = rarestFirst ? _availability[piece] : 0;

                if (rarity < rarest)
                {
                    rarest = rarity;
                    chosen = piece;
                    ties = 1;
                }
                else if (rarity == rarest)
                {
                    // Reservoir sampling over the ties, so that equally rare
                    // pieces — and, before rarest first starts, every piece —
                    // are chosen uniformly in one pass. Without it every peer
                    // would be sent at the lowest-numbered candidate.
                    ties++;
                    if (_random.Next(ties) == 0)
                    {
                        chosen = piece;
                    }
                }
            }

            if (chosen < 0)
            {
                return null;
            }

            _inProgress.Add(chosen);
            return chosen;
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
