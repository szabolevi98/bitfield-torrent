namespace Bitfield.Core.Trackers;

/// <summary>
/// The tracker list of one torrent, and the order to try it in (BEP 12).
///
/// Tiers are tried in order, and within a tier the trackers are shuffled once
/// at startup so that a swarm does not all pile onto whichever tracker happens
/// to be listed first. Whichever one answers is moved to the front of its tier,
/// so later announces go straight to it and the dead entries are only retried
/// when it stops answering.
/// </summary>
public sealed class TrackerTiers
{
    private readonly List<List<Uri>> _tiers;

    public TrackerTiers(IEnumerable<IEnumerable<string>> tiers, bool shuffle = true)
    {
        _tiers = [];

        foreach (IEnumerable<string> tier in tiers)
        {
            List<Uri> trackers = [];
            foreach (string url in tier)
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && !trackers.Contains(uri))
                {
                    trackers.Add(uri);
                }
            }

            if (trackers.Count == 0)
            {
                continue;
            }

            if (shuffle && trackers.Count > 1)
            {
                Random.Shared.Shuffle(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(trackers));
            }

            _tiers.Add(trackers);
        }
    }

    public IReadOnlyList<IReadOnlyList<Uri>> Tiers => _tiers;

    public int Count => _tiers.Sum(tier => tier.Count);

    /// <summary>
    /// Announces to the first tracker that answers, and remembers which one it
    /// was. Throws only when every tracker in every tier has failed, with all
    /// of their complaints, since a torrent whose trackers are all down is
    /// something the user has to be told about plainly.
    /// </summary>
    public async Task<(Uri Tracker, AnnounceResponse Response)> AnnounceAsync(
        AnnounceRequest request,
        Func<Uri, AnnounceRequest, CancellationToken, Task<AnnounceResponse>> announce,
        CancellationToken cancellationToken = default)
    {
        if (_tiers.Count == 0)
        {
            throw new TrackerException("the torrent has no trackers");
        }

        List<string> failures = [];

        foreach (List<Uri> tier in _tiers)
        {
            for (int i = 0; i < tier.Count; i++)
            {
                Uri tracker = tier[i];
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    AnnounceResponse response = await announce(tracker, request, cancellationToken)
                        .ConfigureAwait(false);

                    if (i > 0)
                    {
                        tier.RemoveAt(i);
                        tier.Insert(0, tracker);
                    }

                    return (tracker, response);
                }
                catch (TrackerException e)
                {
                    failures.Add($"{tracker}: {e.Message}");
                }
            }
        }

        throw new TrackerException($"no tracker answered:{Environment.NewLine}  {string.Join(Environment.NewLine + "  ", failures)}");
    }
}
