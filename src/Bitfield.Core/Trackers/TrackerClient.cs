namespace Bitfield.Core.Trackers;

/// <summary>
/// Announces to whichever kind of tracker a URL names. A torrent's tier list
/// routinely mixes HTTP and UDP trackers, and which one a given entry is should
/// not be anybody else's problem.
/// </summary>
public sealed class TrackerClient : IDisposable
{
    private readonly HttpTrackerClient _http = new();
    private readonly UdpTrackerClient _udp = new();

    public Task<AnnounceResponse> AnnounceAsync(
        Uri tracker,
        AnnounceRequest request,
        CancellationToken cancellationToken = default) => tracker.Scheme switch
        {
            "http" or "https" => _http.AnnounceAsync(tracker, request, cancellationToken),
            "udp" => _udp.AnnounceAsync(tracker, request, cancellationToken),
            _ => throw new TrackerException($"{tracker.Scheme} is not a tracker this client speaks"),
        };

    public void Dispose()
    {
        _http.Dispose();
        _udp.Dispose();
    }
}
