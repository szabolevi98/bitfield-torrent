using System.Text;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Trackers;

/// <summary>What is happening to a torrent at the moment it announces.</summary>
public enum TrackerEvent
{
    /// <summary>A routine announce at the interval the tracker asked for.</summary>
    None,

    /// <summary>The first announce for this torrent in this run.</summary>
    Started,

    /// <summary>The torrent is being stopped, so the tracker can drop this peer.</summary>
    Stopped,

    /// <summary>The download just finished. Sent once, and only if it did not start complete.</summary>
    Completed,
}

/// <summary>
/// One announce. The tracker learns from it which torrent this is, how to reach
/// this peer, and how much has moved so far — the counters are the client's own
/// word, and on a tracker that keeps ratios they are what the account is judged
/// on, so they are reported as they are rather than as would flatter.
/// </summary>
public sealed record AnnounceRequest
{
    public required InfoHash InfoHash { get; init; }

    public required PeerId PeerId { get; init; }

    /// <summary>The port this client accepts peer connections on.</summary>
    public required int Port { get; init; }

    public long Uploaded { get; init; }

    public long Downloaded { get; init; }

    /// <summary>Bytes still needed. Zero means this peer is a seed.</summary>
    public long Left { get; init; }

    public TrackerEvent Event { get; init; } = TrackerEvent.None;

    /// <summary>How many peers to ask for. Trackers cap this themselves.</summary>
    public int? NumWant { get; init; }

    /// <summary>
    /// A value the client keeps constant across announces, so a tracker can
    /// recognise it again after its address changes (BEP 23).
    /// </summary>
    public uint? Key { get; init; }

    /// <summary>Whatever the tracker sent back as <c>tracker id</c> last time.</summary>
    public string? TrackerId { get; init; }

    /// <summary>
    /// Builds the announce URL. The infohash and peer id are twenty raw bytes
    /// each, not text: they have to be percent-encoded byte by byte, and a
    /// client that hands them to a general-purpose URL encoder — which will
    /// treat them as UTF-8 and mangle anything that is not valid — gets a
    /// "torrent not registered" from every tracker it tries.
    /// </summary>
    public Uri ToUri(Uri announce)
    {
        StringBuilder query = new(announce.AbsoluteUri);
        query.Append(announce.AbsoluteUri.Contains('?') ? '&' : '?');

        query.Append("info_hash=");
        PercentEncoding.AppendEncoded(query, InfoHash.ToArray());

        query.Append("&peer_id=");
        PercentEncoding.AppendEncoded(query, PeerId.ToArray());

        query.Append("&port=").Append(Port);
        query.Append("&uploaded=").Append(Uploaded);
        query.Append("&downloaded=").Append(Downloaded);
        query.Append("&left=").Append(Left);

        // Without this a tracker may answer with a list of dictionaries, which
        // is many times the size for no benefit. Both forms are understood, but
        // there is no reason to ask for the larger one.
        query.Append("&compact=1");

        if (Event != TrackerEvent.None)
        {
            query.Append("&event=").Append(Event switch
            {
                TrackerEvent.Started => "started",
                TrackerEvent.Stopped => "stopped",
                TrackerEvent.Completed => "completed",
                _ => throw new ArgumentOutOfRangeException(nameof(Event)),
            });
        }

        if (NumWant is { } numWant)
        {
            query.Append("&numwant=").Append(numWant);
        }

        if (Key is { } key)
        {
            query.Append("&key=").Append(key);
        }

        if (TrackerId is { Length: > 0 } trackerId)
        {
            query.Append("&trackerid=");
            PercentEncoding.AppendEncoded(query, Encoding.UTF8.GetBytes(trackerId));
        }

        return new Uri(query.ToString());
    }
}
