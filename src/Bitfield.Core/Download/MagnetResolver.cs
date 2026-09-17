using System.Net;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Core.Download;

/// <summary>
/// Turns a magnet link into a torrent, by asking the swarm for the description
/// the link does not carry.
///
/// A magnet link names a torrent by its infohash and nothing else. To download
/// it a client first has to find peers — from the trackers the link happens to
/// list, from peers named in the link itself, or from the DHT — and then ask
/// one of them for the description, which arrives over the extension protocol
/// and is checked against the infohash that asked for it.
/// </summary>
public static class MagnetResolver
{
    public static async Task<Metainfo> ResolveAsync(
        MagnetLink link,
        PeerId peerId,
        int port = 6881,
        IEnumerable<IPEndPoint>? extraPeers = null,
        Action<string>? note = null,
        CancellationToken cancellationToken = default)
    {
        List<IPEndPoint> peers = [.. link.Peers, .. extraPeers ?? []];

        if (link.Trackers.Count > 0)
        {
            peers.AddRange(await AskTrackersAsync(link, peerId, port, note, cancellationToken).ConfigureAwait(false));
        }

        if (peers.Count == 0)
        {
            throw new MetainfoException("no peers to ask for the torrent's description");
        }

        note?.Invoke($"asking {peers.Count} peers for the description");

        return await FetchAsync(link, peers, peerId, port, note, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<IPEndPoint>> AskTrackersAsync(
        MagnetLink link,
        PeerId peerId,
        int port,
        Action<string>? note,
        CancellationToken cancellationToken)
    {
        using TrackerClient client = new();
        TrackerTiers tiers = new(link.Trackers.Select(tracker => new[] { tracker }));

        AnnounceRequest request = new()
        {
            InfoHash = link.InfoHash,
            PeerId = peerId,
            Port = port,

            // Nothing is known about the torrent's size yet, so there is no
            // honest figure to send. Trackers take this as "still wants it",
            // which is exactly right.
            Left = 0,
            Event = TrackerEvent.Started,
            NumWant = 100,
        };

        try
        {
            (Uri tracker, AnnounceResponse response) = await tiers
                .AnnounceAsync(request, client.AnnounceAsync, cancellationToken)
                .ConfigureAwait(false);

            note?.Invoke($"{tracker.Host} gave {response.Peers.Count} peers");
            return response.Peers;
        }
        catch (TrackerException e)
        {
            note?.Invoke($"tracker: {e.Message}");
            return [];
        }
    }

    /// <summary>
    /// Asks several peers at once, because most of a tracker's list will not
    /// answer and the first description that checks out is as good as any.
    /// </summary>
    private static async Task<Metainfo> FetchAsync(
        MagnetLink link,
        IReadOnlyList<IPEndPoint> peers,
        PeerId peerId,
        int port,
        Action<string>? note,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource found = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using SemaphoreSlim slots = new(8);

        Task<byte[]?>[] attempts = [.. peers.Take(40).Select(async peer =>
        {
            await slots.WaitAsync(found.Token).ConfigureAwait(false);
            try
            {
                using CancellationTokenSource attempt =
                    CancellationTokenSource.CreateLinkedTokenSource(found.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(20));

                await using PeerConnection connection = await PeerConnection
                    .ConnectAsync(peer, link.InfoHash, peerId, attempt.Token)
                    .ConfigureAwait(false);

                byte[]? metadata = await MetadataExchange
                    .FetchAsync(connection, link.InfoHash, port, attempt.Token)
                    .ConfigureAwait(false);

                if (metadata != null)
                {
                    note?.Invoke($"{peer} ({connection.RemotePeerId.ClientName()}) gave the description, {metadata.Length:N0} bytes");
                    await found.CancelAsync().ConfigureAwait(false);
                }

                return metadata;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                slots.Release();
            }
        })];

        byte[]?[] results;
        try
        {
            results = await Task.WhenAll(attempts).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            results = [.. attempts.Where(task => task.IsCompletedSuccessfully).Select(task => task.Result)];
        }

        if (results.FirstOrDefault(metadata => metadata != null) is not { } description)
        {
            throw new MetainfoException("no peer would hand over the torrent's description");
        }

        return Metainfo.FromInfoDictionary(description, link.Trackers);
    }
}
