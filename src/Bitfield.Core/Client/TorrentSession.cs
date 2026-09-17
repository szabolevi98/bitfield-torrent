using System.Net;
using Bitfield.Core.Dht;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Client;

/// <summary>What a torrent is doing, as the list shows it.</summary>
public enum TorrentStatus
{
    /// <summary>Hashing what is on disk, before anything can be asked for.</summary>
    Checking,

    Downloading,

    /// <summary>Wants pieces and has nobody to ask, which is not the same as working.</summary>
    Stalled,

    Seeding,

    /// <summary>Stopped by the user, and staying stopped until they say otherwise.</summary>
    Paused,

    Error,
}

/// <summary>
/// The shared parts of the client that every torrent needs a reference to.
/// Passed in rather than reached for, so that a session has no idea there is a
/// window anywhere.
/// </summary>
public sealed record EngineServices
{
    public required PeerId PeerId { get; init; }

    public required int Port { get; init; }

    public DhtNode? Dht { get; init; }

    public required RateLimiter DownloadLimit { get; init; }

    public required RateLimiter UploadLimit { get; init; }

    public required PeerBudget Budget { get; init; }
}

/// <summary>
/// One torrent as the client deals with it.
///
/// Pausing tears the download down and resuming builds a new one around the
/// pieces already held — which is why a resumed torrent does not hash anything
/// again. What it holds is kept here, not in the download, precisely so that
/// the download can come and go.
/// </summary>
public sealed class TorrentSession : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TorrentStore _store;
    private readonly EngineServices _services;
    private readonly Action<string>? _note;
    private readonly Lock _gate = new();

    private readonly List<IPEndPoint> _knownPeers = [];

    private CancellationTokenSource _run;
    private PieceBitfield? _have;
    private long _downloadedBefore;
    private long _uploadedBefore;

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
        _run = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
    }

    public Metainfo Torrent { get; }

    public TorrentStorage Storage { get; }

    public TorrentState State { get; private set; }

    /// <summary>Null while the torrent is being checked, and while it is paused.</summary>
    public TorrentDownload? Download { get; private set; }

    public InfoHash InfoHash => Torrent.InfoHash;

    public string Name => Torrent.Name;

    public Task Running { get; private set; } = Task.CompletedTask;

    public string? Error { get; private set; }

    public double CheckedFraction { get; private set; }

    public bool IsChecking { get; private set; }

    public bool Paused { get; private set; }

    public TorrentStatus Status
    {
        get
        {
            if (Error != null)
            {
                return TorrentStatus.Error;
            }

            if (Paused)
            {
                return TorrentStatus.Paused;
            }

            if (IsChecking)
            {
                return TorrentStatus.Checking;
            }

            if (Download is not { } download)
            {
                return TorrentStatus.Stalled;
            }

            return download.IsComplete
                ? TorrentStatus.Seeding
                : download.ConnectedPeerCount > 0 ? TorrentStatus.Downloading : TorrentStatus.Stalled;
        }
    }

    /// <summary>Held pieces, kept across a pause so that resuming rehashes nothing.</summary>
    public PieceBitfield? Have => Download?.Have ?? _have;

    public long Downloaded => _downloadedBefore + (Download?.Downloaded ?? 0);

    public long Uploaded => _uploadedBefore + (Download?.Uploaded ?? 0);

    public int PieceCount => Torrent.PieceCount;

    public double Fraction => IsChecking
        ? CheckedFraction
        : Have is { } have ? have.SetCount / (double)Torrent.PieceCount : 0;

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

        if (state.Paused)
        {
            // A torrent that was paused when the client closed comes back
            // paused. Starting it because the client restarted would be the
            // client overruling the user.
            session.Paused = true;
            note?.Invoke($"{torrent.Name}: paused");
        }
        else
        {
            session.Start();
        }

        return session;
    }

    /// <summary>
    /// Stops the torrent without forgetting it: the connections go, the tracker
    /// is told, the resume file is written, and what has been downloaded stays
    /// exactly where it is.
    /// </summary>
    public void Pause()
    {
        lock (_gate)
        {
            if (Paused)
            {
                return;
            }

            Paused = true;
        }

        _run.Cancel();
        _note?.Invoke($"{Name}: paused");
        SaveState();
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!Paused)
            {
                return;
            }

            Paused = false;
            Error = null;
        }

        _run = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        Start();

        _note?.Invoke($"{Name}: resumed");
        SaveState();
    }

    /// <summary>
    /// A peer found somewhere other than this torrent's own trackers. Kept, so
    /// that a torrent paused and resumed does not lose the peers it knew — the
    /// download it hands them to is a different object each time.
    /// </summary>
    public void AddPeer(IPEndPoint peer)
    {
        lock (_gate)
        {
            if (!_knownPeers.Contains(peer))
            {
                _knownPeers.Add(peer);
            }
        }

        Download?.AddPeer(peer);
    }

    private void Start() => Running = Task.Run(() => RunAsync(_run.Token), CancellationToken.None);

    private void SaveState()
    {
        State = State with { Paused = Paused };

        try
        {
            _store.SaveState(InfoHash, State);
        }
        catch (IOException e)
        {
            _note?.Invoke($"{Name}: the state could not be saved — {e.Message}");
        }
    }

    /// <summary>
    /// Works out what is on disk if it is not already known, then runs the
    /// torrent until it is paused or the client closes. What it held on the way
    /// out is kept, which is the whole trick behind resuming without hashing.
    /// </summary>
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        TorrentDownload? download = null;

        try
        {
            string resumePath = _store.ResumePath(InfoHash);

            if (_have == null)
            {
                ResumeData? resume = ResumeData.TryLoad(resumePath, Torrent, Storage);

                if (resume != null)
                {
                    _have = resume.Pieces;
                    _note?.Invoke($"{Name}: resumed with {_have}");
                }
                else
                {
                    IsChecking = true;
                    Progress<double> progress = new(fraction => CheckedFraction = fraction);

                    _have = await Storage.VerifyAsync(progress, cancellationToken).ConfigureAwait(false);
                    IsChecking = false;

                    _note?.Invoke(_have.IsEmpty ? $"{Name}: nothing on disk yet" : $"{Name}: found {_have} on disk");
                }
            }

            download = new TorrentDownload(Torrent, Storage, _have, _services.PeerId, resumePath)
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

            lock (_gate)
            {
                foreach (IPEndPoint peer in _knownPeers)
                {
                    download.AddPeer(peer);
                }
            }

            Download = download;

            await download.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Pausing, or the client closing.
        }
        catch (Exception e)
        {
            IsChecking = false;
            Error = e.Message;
            _note?.Invoke($"{Name}: stopped — {e.Message}");
        }
        finally
        {
            if (download != null)
            {
                // Everything the torn-down download did is carried forward, or
                // a paused torrent would appear to have downloaded nothing.
                _have = download.Have;
                _downloadedBefore += download.Downloaded;
                _uploadedBefore += download.Uploaded;
            }

            Download = null;
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
        _run.Dispose();
        _stop.Dispose();
    }
}
