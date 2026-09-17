using Bitfield.Core.Dht;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield;

/// <summary>What a torrent is doing, as the list shows it.</summary>
internal enum TorrentStatus
{
    Checking,
    Downloading,
    Seeding,
    Error,
}

/// <summary>
/// The shared parts of the client that every torrent needs a reference to.
/// Passed in rather than reached for, so that a session has no idea there is a
/// window anywhere.
/// </summary>
internal sealed record EngineServices
{
    public required PeerId PeerId { get; init; }

    public required int Port { get; init; }

    public DhtNode? Dht { get; init; }

    public required RateLimiter DownloadLimit { get; init; }

    public required RateLimiter UploadLimit { get; init; }

    public required PeerBudget Budget { get; init; }
}

/// <summary>
/// One torrent as the client deals with it: the storage, the download, where it
/// is going and how long it has been there.
///
/// The download itself only exists once the hashing is done, because what it
/// already holds is what the piece picker is built around. Until then the
/// session is checking, and says so.
/// </summary>
internal sealed class TorrentSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TorrentStore _store;
    private readonly EngineServices _services;
    private readonly Action<string>? _note;

    private TorrentSession(
        Metainfo torrent,
        TorrentStorage storage,
        TorrentState state,
        TorrentStore store,
        EngineServices services,
        Action<string>? note)
    {
        Torrent = torrent;
        Storage = storage;
        State = state;
        _store = store;
        _services = services;
        _note = note;
    }

    public Metainfo Torrent { get; }

    public TorrentStorage Storage { get; }

    public TorrentState State { get; }

    /// <summary>Null while the torrent is still being checked.</summary>
    public TorrentDownload? Download { get; private set; }

    public InfoHash InfoHash => Torrent.InfoHash;

    public string Name => Torrent.Name;

    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Set when the torrent stopped because something went wrong.</summary>
    public string? Error { get; private set; }

    /// <summary>How far the hashing has got, while it is happening.</summary>
    public double CheckedFraction { get; private set; }

    public bool IsChecking { get; private set; } = true;

    public TorrentStatus Status => Error != null
        ? TorrentStatus.Error
        : IsChecking ? TorrentStatus.Checking
        : Download?.IsComplete == true ? TorrentStatus.Seeding
        : TorrentStatus.Downloading;

    /// <summary>What has been downloaded, including whatever was already there.</summary>
    public long Downloaded => Download == null
        ? 0
        : ((long)Download.Have.SetCount * Torrent.PieceLength) is var held && held > Torrent.TotalLength
            ? Torrent.TotalLength
            : held;

    public long Uploaded => Download?.Uploaded ?? 0;

    public double Fraction => IsChecking
        ? CheckedFraction
        : Download == null ? 0 : Download.Have.SetCount / (double)Torrent.PieceCount;

    public static TorrentSession Open(
        Metainfo torrent,
        TorrentState state,
        TorrentStore store,
        EngineServices services,
        Action<string>? note)
    {
        TorrentStorage storage = new(torrent, state.SavePath);
        storage.Create();

        TorrentSession session = new(torrent, storage, state, store, services, note);
        session.Running = Task.Run(() => session.RunAsync(session._stop.Token), CancellationToken.None);
        return session;
    }

    /// <summary>
    /// Works out what is on disk, then runs the torrent. The hashing happens on
    /// this task rather than the caller's, so adding ten torrents at once does
    /// not wait on the first one being checked.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            string resumePath = _store.ResumePath(InfoHash);
            ResumeData? resume = ResumeData.TryLoad(resumePath, Torrent, Storage);

            PieceBitfield have;

            if (resume != null)
            {
                have = resume.Pieces;
                _note?.Invoke($"{Name}: resumed with {have}");
            }
            else
            {
                Progress<double> progress = new(fraction => CheckedFraction = fraction);
                have = await Storage.VerifyAsync(progress, cancellationToken).ConfigureAwait(false);
                _note?.Invoke(have.IsEmpty ? $"{Name}: nothing on disk yet" : $"{Name}: found {have} on disk");
            }

            TorrentDownload download = new(Torrent, Storage, have, _services.PeerId, resumePath)
            {
                KeepSeeding = true,
                Dht = _services.Dht,
                Port = _services.Port,
                DownloadLimit = _services.DownloadLimit,
                UploadLimit = _services.UploadLimit,
                Budget = _services.Budget,
            };

            if (_note != null)
            {
                download.Note += text => _note($"{Name}: {text}");
            }

            Download = download;
            IsChecking = false;

            await download.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopping is not a failure.
        }
        catch (Exception e)
        {
            IsChecking = false;
            Error = e.Message;
            _note?.Invoke($"{Name}: stopped — {e.Message}");
        }
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
