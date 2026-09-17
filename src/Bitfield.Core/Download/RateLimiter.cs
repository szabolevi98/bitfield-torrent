using System.Diagnostics;

namespace Bitfield.Core.Download;

/// <summary>
/// A token bucket, for holding a torrent to a chosen number of bytes a second.
///
/// The bucket fills at the limit and empties as bytes go out; asking for more
/// than is in it waits until it has refilled. A bucket rather than a fixed
/// delay per send, because traffic is bursty by nature — sixteen kilobytes
/// arrive at once — and a limiter that forbids bursts entirely spends its time
/// throttling a connection that is under the limit on average.
///
/// The depth is a second's worth, so a connection that has been idle can send
/// one second's traffic immediately and no more.
/// </summary>
public sealed class RateLimiter
{
    private readonly Lock _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private long _bytesPerSecond;
    private double _available;
    private double _lastRefill;

    public RateLimiter(long bytesPerSecond = 0)
    {
        _bytesPerSecond = Math.Max(0, bytesPerSecond);
        _available = _bytesPerSecond;
    }

    /// <summary>Bytes a second, or zero for no limit at all.</summary>
    public long BytesPerSecond
    {
        get
        {
            lock (_gate)
            {
                return _bytesPerSecond;
            }
        }

        set
        {
            lock (_gate)
            {
                long limit = Math.Max(0, value);

                // A limit put on a connection that had none starts with a full
                // bucket, so that the first second is governed by the limit
                // rather than spent waiting for a bucket to fill — an idle
                // connection has not used anything and should not be charged
                // for it. Lowering an existing limit only ever takes tokens
                // away, or a limit could be raised and lowered repeatedly to
                // refill the bucket at will.
                _available = _bytesPerSecond == 0 ? limit : Math.Min(_available, limit);
                _bytesPerSecond = limit;
                _lastRefill = _clock.Elapsed.TotalSeconds;
            }
        }
    }

    public bool IsLimited => BytesPerSecond > 0;

    /// <summary>
    /// Waits until this many bytes may pass, then counts them. Returns at once
    /// when there is no limit, which is the usual case and costs a lock.
    /// </summary>
    public async Task WaitAsync(int bytes, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan wait;

            lock (_gate)
            {
                if (_bytesPerSecond == 0)
                {
                    return;
                }

                Refill();

                if (_available >= bytes)
                {
                    _available -= bytes;
                    return;
                }

                // How long until the bucket holds enough. Capped so that a
                // limit lowered while something is waiting is noticed rather
                // than slept through.
                double missing = bytes - _available;
                wait = TimeSpan.FromSeconds(Math.Min(missing / _bytesPerSecond, 0.25));
            }

            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

    private void Refill()
    {
        double now = _clock.Elapsed.TotalSeconds;
        double elapsed = now - _lastRefill;
        _lastRefill = now;

        _available = Math.Min(_available + (elapsed * _bytesPerSecond), _bytesPerSecond);
    }
}
