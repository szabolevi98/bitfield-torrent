using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Core.Download;

/// <summary>A snapshot of how a download is going.</summary>
public sealed record DownloadProgress
{
    public required int PiecesHeld { get; init; }

    public required int PieceCount { get; init; }

    public required long Downloaded { get; init; }

    public required long TotalLength { get; init; }

    public required int ConnectedPeers { get; init; }

    public required double BytesPerSecond { get; init; }

    public int FailedPieces { get; init; }

    public double Fraction => PieceCount == 0 ? 0 : PiecesHeld / (double)PieceCount;
}

/// <summary>
/// Runs one torrent: finds peers, keeps a set of connections busy, writes what
/// verifies, and stops when there is nothing left to want.
/// </summary>
public sealed class TorrentDownload : IPieceReceiver
{
    private readonly Metainfo _torrent;
    private readonly TorrentStorage _storage;
    private readonly PiecePicker _picker;
    private readonly PeerId _peerId;
    private readonly string? _resumePath;

    private readonly ConcurrentDictionary<IPEndPoint, PeerSession> _sessions = new();
    private readonly ConcurrentDictionary<IPEndPoint, byte> _known = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Lock _writeGate = new();

    private long _downloaded;
    private int _failedPieces;
    private int _connecting;

    public TorrentDownload(
        Metainfo torrent,
        TorrentStorage storage,
        PieceBitfield have,
        PeerId peerId,
        string? resumePath = null)
    {
        _torrent = torrent;
        _storage = storage;
        _picker = new PiecePicker(have);
        _peerId = peerId;
        _resumePath = resumePath;
    }

    /// <summary>How many connections to keep open at once.</summary>
    public int MaxPeers { get; init; } = 30;

    /// <summary>The port announced to trackers. Nothing listens on it yet.</summary>
    public int Port { get; init; } = 6881;

    public event Action<DownloadProgress>? Progress;

    public event Action<string>? Note;

    public bool IsComplete => _picker.IsComplete;

    public PieceBitfield Have => _picker.Have;

    public long Downloaded => Interlocked.Read(ref _downloaded);

    public DownloadProgress Snapshot() => new()
    {
        PiecesHeld = _picker.Have.SetCount,
        PieceCount = _torrent.PieceCount,
        Downloaded = Downloaded,
        TotalLength = _torrent.TotalLength,
        ConnectedPeers = _sessions.Count,
        BytesPerSecond = Downloaded / Math.Max(_clock.Elapsed.TotalSeconds, 0.001),
        FailedPieces = _failedPieces,
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using HttpTrackerClient tracker = new();
        TrackerTiers tiers = new(_torrent.AnnounceTiers);

        using CancellationTokenSource finished = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task announcing = AnnounceLoopAsync(tracker, tiers, finished.Token);
        Task connecting = ConnectLoopAsync(finished.Token);
        Task reporting = ReportLoopAsync(finished.Token);

        try
        {
            while (!_picker.IsComplete)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(Quietly(announcing), Quietly(connecting), Quietly(reporting)).ConfigureAwait(false);

            SaveResume();

            // The tracker is told the download finished, which is what a ratio
            // is credited from on the trackers that keep one.
            await TellTrackerAsync(tracker, tiers, TrackerEvent.Completed, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Asks the tracker for peers, waits the interval it asked for, and asks
    /// again. Announcing more often than a tracker allows is how a client gets
    /// itself banned, so the interval it sends is obeyed.
    /// </summary>
    private async Task AnnounceLoopAsync(HttpTrackerClient tracker, TrackerTiers tiers, CancellationToken cancellationToken)
    {
        TrackerEvent next = TrackerEvent.Started;

        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait = TimeSpan.FromMinutes(15);

            try
            {
                (Uri url, AnnounceResponse response) = await tiers
                    .AnnounceAsync(Request(next), tracker.AnnounceAsync, cancellationToken)
                    .ConfigureAwait(false);

                foreach (IPEndPoint peer in response.Peers)
                {
                    _known.TryAdd(peer, 0);
                }

                Note?.Invoke($"{url.Host} gave {response.Peers.Count} peers, next in {response.Interval.TotalSeconds:N0} s");
                wait = response.Interval;
                next = TrackerEvent.None;
            }
            catch (TrackerException e)
            {
                Note?.Invoke($"tracker: {e.Message}");
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectLoopAsync(CancellationToken cancellationToken)
    {
        HashSet<IPEndPoint> tried = [];

        while (!cancellationToken.IsCancellationRequested)
        {
            foreach (IPEndPoint peer in _known.Keys)
            {
                // Connections still being made count towards the limit. A peer
                // only reaches _sessions once its handshake is done, so
                // counting sessions alone starts dozens of connections at once
                // and overshoots the limit by however many are in flight.
                if (_sessions.Count + Volatile.Read(ref _connecting) >= MaxPeers)
                {
                    break;
                }

                if (!tried.Add(peer))
                {
                    continue;
                }

                Interlocked.Increment(ref _connecting);
                _ = RunPeerAsync(peer, cancellationToken);
            }

            // Every peer the tracker gave has been tried and none are left to
            // connect to; wait for the next announce to bring more.
            if (tried.Count >= _known.Count && _sessions.IsEmpty)
            {
                tried.Clear();
            }

            try
            {
                await Task.Delay(1_000, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task RunPeerAsync(IPEndPoint peer, CancellationToken cancellationToken)
    {
        PeerConnection? connection = null;
        bool connected = false;

        try
        {
            using CancellationTokenSource connecting =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            connecting.CancelAfter(TimeSpan.FromSeconds(10));

            connection = await PeerConnection
                .ConnectAsync(peer, _torrent.InfoHash, _peerId, connecting.Token)
                .ConfigureAwait(false);

            PeerSession session = new(connection, _torrent, _picker, this);
            _sessions[peer] = session;
            Interlocked.Decrement(ref _connecting);
            connected = true;

            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A peer that will not talk is ordinary. There are more.
        }
        finally
        {
            if (!connected)
            {
                Interlocked.Decrement(ref _connecting);
            }

            _sessions.TryRemove(peer, out _);

            if (connection != null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ReportLoopAsync(CancellationToken cancellationToken)
    {
        int sinceSave = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            Progress?.Invoke(Snapshot());

            // Saved often enough that an interrupted download loses seconds of
            // work rather than minutes.
            if (++sinceSave >= 20)
            {
                sinceSave = 0;
                SaveResume();
            }
        }
    }

    public async Task PieceVerifiedAsync(int piece, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        // Near the end the same piece is asked of several peers at once, so the
        // second copy to arrive is a piece already held. Writing it again would
        // be harmless and counting it would not: the download would report more
        // bytes than it fetched.
        if (!_picker.Wants(piece))
        {
            return;
        }

        await _storage.WritePieceAsync(piece, data, cancellationToken).ConfigureAwait(false);

        _picker.Completed(piece);
        Interlocked.Add(ref _downloaded, data.Length);

        // Telling peers what arrived is what makes this client worth connecting
        // to, and it is how a swarm spreads a piece instead of everyone
        // fetching it from the same seed.
        foreach (PeerSession session in _sessions.Values)
        {
            try
            {
                await session.AnnounceHaveAsync(piece, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A peer that has gone away is not this operation's problem.
            }
        }
    }

    public void PieceFailed(int piece, IPEndPoint peer)
    {
        Interlocked.Increment(ref _failedPieces);
        Note?.Invoke($"piece {piece} from {peer} failed its hash, fetching it again");
    }

    private void SaveResume()
    {
        if (_resumePath == null)
        {
            return;
        }

        lock (_writeGate)
        {
            try
            {
                ResumeData.Capture(_torrent, _storage, _picker.Have, Downloaded, uploaded: 0).Save(_resumePath);
            }
            catch (IOException e)
            {
                Note?.Invoke($"resume could not be saved: {e.Message}");
            }
        }
    }

    private async Task TellTrackerAsync(
        HttpTrackerClient tracker,
        TrackerTiers tiers,
        TrackerEvent what,
        CancellationToken cancellationToken)
    {
        try
        {
            await tiers.AnnounceAsync(Request(what), tracker.AnnounceAsync, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Nothing here is worth failing a finished download over.
        }
    }

    private AnnounceRequest Request(TrackerEvent what) => new()
    {
        InfoHash = _torrent.InfoHash,
        PeerId = _peerId,
        Port = Port,
        Downloaded = Downloaded,
        Left = Math.Max(0, _torrent.TotalLength - Downloaded),
        Event = what,
        NumWant = what == TrackerEvent.Stopped ? 0 : 100,
    };

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // These loops end by cancellation, which is not a failure.
        }
    }
}
