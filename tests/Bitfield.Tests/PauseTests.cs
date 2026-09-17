using System.Net;
using Bitfield.Core.Client;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Pausing a torrent and starting it again.
///
/// The thing worth proving is what pausing does <em>not</em> do: a resumed
/// torrent must not hash its files again. Rehashing a half-finished download
/// that was paused for a second is minutes of disk for nothing, and it is the
/// obvious way to implement this wrongly — tear the download down, build a new
/// one, and let it work out what is on disk the way it does at startup.
/// </summary>
internal static class PauseTests
{
    private const int ContentSize = 3 * 1024 * 1024;
    private const int PieceLength = 32 * 1024;

    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "bitfield-pause", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            RunAsync(check, root).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            check("pause: ran to completion", false, $"{e.GetType().Name}: {e.Message}");
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

    private static async Task RunAsync(Action<string, bool, string> check, string root)
    {
        TestTorrents.Built built = TestTorrents.BuildSingle(
            "paused.bin", ContentSize, PieceLength, announce: null);

        Metainfo torrent = built.Torrent;

        using CancellationTokenSource stop = new(Patience);

        // A seed, as in the swarm test: a plain download that already holds
        // everything, with something listening in front of it.
        await using TorrentStorage seedStorage = new(torrent, Path.Combine(root, "seed"));
        seedStorage.Create();

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            await seedStorage.WritePieceAsync(
                piece,
                built.Content.AsMemory(piece * torrent.PieceLength, torrent.PieceLengthAt(piece)),
                stop.Token).ConfigureAwait(false);
        }

        PieceBitfield seedHas = await seedStorage.VerifyAsync(cancellationToken: stop.Token).ConfigureAwait(false);

        TorrentDownload seed = new(torrent, seedStorage, seedHas, PeerId.Generate())
        {
            KeepSeeding = true,
            ChokeInterval = TimeSpan.FromMilliseconds(250),

            // Slow enough that the leecher can be caught part way through, fast
            // enough that the check does not take all day.
            UploadLimit = new RateLimiter(400 * 1024),
        };

        await using PeerListener listener = new(0, hash => hash == torrent.InfoHash ? seed : null, IPAddress.Loopback);

        Task seeding = Task.Run(() => seed.RunAsync(stop.Token), CancellationToken.None);
        Task listening = Task.Run(() => listener.RunAsync(stop.Token), CancellationToken.None);

        IPEndPoint seedEndPoint = new(IPAddress.Loopback, listener.Port);

        // The leecher is a session, because pausing is a session's job.
        TorrentStore store = new(Path.Combine(root, "client"));
        TorrentState state = new() { SavePath = Path.Combine(root, "leecher") };

        // What the engine does when a torrent is added; the session only ever
        // updates the state beside it.
        store.Save(torrent, state);

        EngineServices services = new()
        {
            PeerId = PeerId.Generate(),
            Port = 0,
            DownloadLimit = new RateLimiter(),
            UploadLimit = new RateLimiter(),
            Budget = new PeerBudget(20),
        };

        await using TorrentSession session = TorrentSession.Open(torrent, state, store, services, null);
        session.AddPeer(seedEndPoint);

        // ------------------------------------------------------- pausing

        bool started = await WaitForAsync(() => session.Have is { SetCount: > 4 }, stop.Token).ConfigureAwait(false);
        check("pause: the torrent got going", started, $"{session.Have?.SetCount ?? 0} pieces");

        int heldWhenPaused = session.Have?.SetCount ?? 0;
        session.Pause();

        check("pause: it says it is paused", session.Status == TorrentStatus.Paused, $"{session.Status}");

        bool stopped = await WaitForAsync(() => session.Download == null, stop.Token).ConfigureAwait(false);
        check("pause: the download is torn down", stopped, "");

        check("pause: what it held is still known",
            session.Have?.SetCount >= heldWhenPaused, $"{session.Have?.SetCount} vs {heldWhenPaused}");

        // Nothing should arrive while it is paused.
        int heldAfterPause = session.Have?.SetCount ?? 0;
        await Task.Delay(1500, stop.Token).ConfigureAwait(false);

        check("pause: and nothing arrives while it is stopped",
            session.Have?.SetCount == heldAfterPause, $"{session.Have?.SetCount} vs {heldAfterPause}");

        check("pause: the state file says so",
            store.Load().FirstOrDefault()?.State.Paused == true, "");

        // ------------------------------------------------------ resuming

        bool hashedAgain = false;
        using CancellationTokenSource watching = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);

        Task watcher = Task.Run(async () =>
        {
            while (!watching.Token.IsCancellationRequested)
            {
                if (session.IsChecking)
                {
                    hashedAgain = true;
                }

                await Task.Delay(10, watching.Token).ConfigureAwait(false);
            }
        }, CancellationToken.None);

        session.Resume();
        check("pause: resuming says it is running again", !session.Paused, "");

        bool finished = await WaitForAsync(
            () => session.Have is { IsComplete: true }, stop.Token).ConfigureAwait(false);

        await watching.CancelAsync().ConfigureAwait(false);

        check("pause: it finishes after resuming", finished, $"{session.Have?.SetCount}/{torrent.PieceCount}");

        // The point of the whole exercise.
        check("pause: and it never hashed the file again", !hashedAgain, "it went back to checking");

        check("pause: the resumed copy is the content, byte for byte",
            Matches(session, built.Content), "");

        check("pause: the counters carried across the pause",
            session.Downloaded > 0, $"{session.Downloaded} bytes");

        await stop.CancelAsync().ConfigureAwait(false);

        foreach (Task task in new[] { seeding, listening, watcher })
        {
            try
            {
                await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Ending by cancellation.
            }
        }
    }

    private static bool Matches(TorrentSession session, byte[] content)
    {
        using FileStream file = new(session.Storage.Files[0].FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes.AsSpan().SequenceEqual(content);
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
}
