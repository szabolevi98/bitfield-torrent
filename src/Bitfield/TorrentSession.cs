using Bitfield.Core.Dht;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield;

/// <summary>
/// One torrent as the window deals with it: the storage, the download and the
/// task running them, started together and stopped together.
/// </summary>
internal sealed class TorrentSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();

    private TorrentSession(Metainfo torrent, TorrentStorage storage, TorrentDownload download, string directory)
    {
        Torrent = torrent;
        Storage = storage;
        Download = download;
        Directory = directory;
    }

    public Metainfo Torrent { get; }

    public TorrentStorage Storage { get; }

    public TorrentDownload Download { get; }

    public string Directory { get; }

    public Task Running { get; private set; } = Task.CompletedTask;

    public static async Task<TorrentSession> OpenAsync(
        Metainfo torrent,
        string directory,
        PeerId peerId,
        DhtNode? dht,
        Action<string>? note,
        IProgress<double>? verifying,
        CancellationToken cancellationToken)
    {
        TorrentStorage storage = new(torrent, directory);
        storage.Create();

        string resumePath = ResumeData.PathFor(Path.Combine(directory, ".bitfield"), torrent.InfoHash);

        // The resume file when it can be believed, and hashing everything when
        // it cannot — which is also what happens the first time a torrent that
        // is already on disk is opened.
        ResumeData? resume = ResumeData.TryLoad(resumePath, torrent, storage);
        PieceBitfield have;

        if (resume != null)
        {
            have = resume.Pieces;
            note?.Invoke($"resumed with {have}");
        }
        else
        {
            have = await storage.VerifyAsync(verifying, cancellationToken).ConfigureAwait(false);
            note?.Invoke(have.IsEmpty ? "nothing on disk yet" : $"found {have} already on disk");
        }

        TorrentDownload download = new(torrent, storage, have, peerId, resumePath)
        {
            KeepSeeding = true,
            Dht = dht,
        };

        TorrentSession session = new(torrent, storage, download, directory);

        if (note != null)
        {
            download.Note += note;
        }

        session.Running = Task.Run(() => download.RunAsync(session._stop.Token), CancellationToken.None);
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await Running.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Stopping by cancellation, or taking too long about it.
        }

        await Storage.DisposeAsync().ConfigureAwait(false);
        _stop.Dispose();
    }
}
