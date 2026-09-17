using System.Net;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// A swarm of this client talking to itself on the loopback interface: one seed
/// and two leechers, and then a third leecher told about nobody but the two
/// that just finished.
///
/// That second phase is the point. Anything can be made to download from a seed
/// that has everything; a client only really works when a copy it produced can
/// be served onwards by peers that started with nothing, which needs the
/// uploading side, the choking algorithm and the incoming connections all to be
/// right at once. It runs offline in about a second, so it can be part of every
/// build rather than something to remember to try.
/// </summary>
internal static class SwarmTests
{
    /// <summary>
    /// Two megabytes in 32 KiB pieces: enough pieces for peers to have
    /// different ones and to have to trade, small enough to finish instantly
    /// over loopback.
    /// </summary>
    private const int ContentSize = 2 * 1024 * 1024;

    private const int PieceLength = 32 * 1024;

    /// <summary>
    /// The choking round is ten seconds in the real world, where the point is
    /// to be slow enough that peers cannot game it. Here it only has to happen.
    /// </summary>
    private static readonly TimeSpan ChokeInterval = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "bitfield-swarm", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunAsync(check, root).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            check("swarm: ran to completion", false, $"{e.GetType().Name}: {e.Message}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing over.
            }
        }
    }

    private sealed record Node(
        string Name,
        TorrentStorage Storage,
        TorrentDownload Download,
        PeerListener Listener,
        Task Running,
        Task Listening)
    {
        public IPEndPoint EndPoint => new(IPAddress.Loopback, Listener.Port);
    }

    private static async Task RunAsync(Action<string, bool, string> check, string root)
    {
        // A torrent with no tracker at all: the swarm here is wired up by hand,
        // and a tracker that does not exist would only be something to wait for.
        TestTorrents.Built built = TestTorrents.BuildSingle(
            "shared.bin", ContentSize, PieceLength, announce: null);

        Metainfo torrent = built.Torrent;

        check("swarm: the torrent has pieces to trade",
            torrent.PieceCount == 64, $"{torrent.PieceCount}");
        check("swarm: and no tracker to depend on",
            torrent.AnnounceTiers.Count == 0, $"{torrent.AnnounceTiers.Count}");

        using CancellationTokenSource swarm = new(Patience);
        List<Node> nodes = [];

        try
        {
            // ---------------------------------------------- one seed, two leechers

            Node seed = await StartAsync(torrent, root, "seed", built.Content, seeding: true, swarm.Token)
                .ConfigureAwait(false);
            nodes.Add(seed);

            Node first = await StartAsync(torrent, root, "first", null, seeding: true, swarm.Token)
                .ConfigureAwait(false);
            Node second = await StartAsync(torrent, root, "second", null, seeding: true, swarm.Token)
                .ConfigureAwait(false);
            nodes.Add(first);
            nodes.Add(second);

            foreach (Node leecher in new[] { first, second })
            {
                leecher.Download.AddPeer(seed.EndPoint);
            }

            first.Download.AddPeer(second.EndPoint);
            second.Download.AddPeer(first.EndPoint);

            bool finished = await WaitForAsync(() => first.Download.IsComplete && second.Download.IsComplete, swarm.Token)
                .ConfigureAwait(false);

            check("swarm: both leechers finished", finished,
                $"{first.Download.Have} and {second.Download.Have}");

            check("swarm: and their copies are the content, byte for byte",
                Matches(first, built.Content) && Matches(second, built.Content), "");

            check("swarm: the seed actually served the data",
                seed.Download.Uploaded >= ContentSize,
                $"{seed.Download.Uploaded:N0} bytes");

            // ------------------------------------- a leecher served by leechers

            // The seed is taken out of the swarm entirely, so that the only
            // copies left are the ones this client produced and is now serving.
            await StopAsync(seed).ConfigureAwait(false);
            nodes.Remove(seed);

            Node third = await StartAsync(torrent, root, "third", null, seeding: false, swarm.Token)
                .ConfigureAwait(false);
            nodes.Add(third);

            third.Download.AddPeer(first.EndPoint);
            third.Download.AddPeer(second.EndPoint);

            bool servedOnwards = await WaitForAsync(() => third.Download.IsComplete, swarm.Token)
                .ConfigureAwait(false);

            check("swarm: a third leecher finished with no seed in the swarm", servedOnwards,
                $"{third.Download.Have}");
            check("swarm: and its copy is the content too", Matches(third, built.Content), "");
            check("swarm: the peers that served it had downloaded it themselves",
                first.Download.Uploaded + second.Download.Uploaded >= ContentSize,
                $"{first.Download.Uploaded:N0} + {second.Download.Uploaded:N0} bytes");
        }
        finally
        {
            await swarm.CancelAsync().ConfigureAwait(false);

            foreach (Node node in nodes)
            {
                await StopAsync(node).ConfigureAwait(false);
            }
        }
    }

    private static async Task<Node> StartAsync(
        Metainfo torrent,
        string root,
        string name,
        byte[]? content,
        bool seeding,
        CancellationToken cancellationToken)
    {
        string directory = Path.Combine(root, name);
        TorrentStorage storage = new(torrent, directory);
        storage.Create();

        if (content != null)
        {
            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                await storage.WritePieceAsync(
                    piece,
                    content.AsMemory(piece * torrent.PieceLength, torrent.PieceLengthAt(piece)),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        PieceBitfield have = await storage.VerifyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        TorrentDownload download = new(torrent, storage, have, PeerId.Generate())
        {
            KeepSeeding = seeding,
            ChokeInterval = ChokeInterval,
            MaxPeers = 8,
        };

        // Loopback only: binding every interface would have Windows ask about
        // the firewall in the middle of a build.
        PeerListener listener = new(0, infoHash => infoHash == download.InfoHash ? download : null, IPAddress.Loopback);

        return new Node(
            name,
            storage,
            download,
            listener,
            Task.Run(() => download.RunAsync(cancellationToken), CancellationToken.None),
            Task.Run(() => listener.RunAsync(cancellationToken), CancellationToken.None));
    }

    private static async Task StopAsync(Node node)
    {
        await node.Listener.DisposeAsync().ConfigureAwait(false);

        try
        {
            await node.Running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cancelled or timed out; either way the swarm is being taken down.
        }

        await node.Storage.DisposeAsync().ConfigureAwait(false);
    }

    private static async Task<bool> WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (condition())
            {
                return true;
            }

            try
            {
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return condition();
    }

    private static bool Matches(Node node, byte[] content)
    {
        string path = node.Storage.Files[0].FullPath;

        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[file.Length];
        file.ReadExactly(bytes);

        return bytes.AsSpan().SequenceEqual(content);
    }
}
