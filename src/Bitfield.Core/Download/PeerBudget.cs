namespace Bitfield.Core.Download;

/// <summary>
/// How many peer connections may be open across every torrent at once.
///
/// A per-torrent limit is not enough once there is more than one torrent: ten
/// torrents each allowed thirty connections is three hundred sockets, which is
/// a client that hurts the machine it runs on and the router it sits behind. So
/// the limit is shared, and a torrent takes from it rather than counting on its
/// own.
/// </summary>
public sealed class PeerBudget
{
    private readonly Lock _gate = new();
    private int _taken;
    private int _total;

    public PeerBudget(int total = 200) => Total = total;

    public int Total
    {
        get
        {
            lock (_gate)
            {
                return _total;
            }
        }

        set
        {
            lock (_gate)
            {
                _total = Math.Max(1, value);
            }
        }
    }

    public int InUse
    {
        get
        {
            lock (_gate)
            {
                return _taken;
            }
        }
    }

    public int Available
    {
        get
        {
            lock (_gate)
            {
                return Math.Max(0, _total - _taken);
            }
        }
    }

    /// <summary>
    /// Takes one connection's worth, or returns false when the budget is spent.
    /// A torrent that cannot take one simply tries again on its next round,
    /// which is what makes the sharing fair enough without anybody queueing.
    /// </summary>
    public bool TryTake()
    {
        lock (_gate)
        {
            if (_taken >= _total)
            {
                return false;
            }

            _taken++;
            return true;
        }
    }

    public void Return()
    {
        lock (_gate)
        {
            _taken = Math.Max(0, _taken - 1);
        }
    }
}
