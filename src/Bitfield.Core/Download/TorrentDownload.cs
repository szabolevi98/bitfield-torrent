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

    /// <summary>Peers that have said they want something this client holds.</summary>
    public int InterestedPeers { get; init; }

    /// <summary>Peers this client is currently answering.</summary>
    public int UnchokedPeers { get; init; }

    /// <summary>The fastest single peer, in bytes per second.</summary>
    public double FastestPeer { get; init; }

    /// <summary>
    /// The most blocks any one peer is being kept waiting on. It rises with
    /// what peers actually deliver, so seeing it above the floor of four is
    /// what adaptive pipelining looks like from outside.
    /// </summary>
    public int MostRequestsInFlight { get; init; }

    public double Fraction => PieceCount == 0 ? 0 : PiecesHeld / (double)PieceCount;
}

/// <summary>
/// Runs one torrent: finds peers, keeps a set of connections busy, writes what
/// verifies, and stops when there is nothing left to want.
/// </summary>
public sealed class TorrentDownload : IPieceReceiver, IBlockSource
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
    private long _uploadedByClosedPeers;
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

    /// <summary>The port announced to trackers, where incoming peers are accepted.</summary>
    public int Port { get; init; } = 6881;

    /// <summary>
    /// Keep the torrent running after it completes, serving it to others.
    /// Without this the download stops as soon as it has everything, which is
    /// what a one-off fetch wants and what a swarm cannot afford.
    /// </summary>
    public bool KeepSeeding { get; init; }

    /// <summary>
    /// How often the choking algorithm reconsiders. Ten seconds in the wild,
    /// slow enough that a peer cannot game its way into a slot by being briefly
    /// generous; a test swarm sets it far shorter because there it only has to
    /// happen at all.
    /// </summary>
    public TimeSpan ChokeInterval { get; init; } = TimeSpan.FromSeconds(10);

    public InfoHash InfoHash => _torrent.InfoHash;

    public PeerId PeerId => _peerId;

    public event Action<DownloadProgress>? Progress;

    public event Action<string>? Note;

    public bool IsComplete => _picker.IsComplete;

    public PieceBitfield Have => _picker.Have;

    public long Downloaded => Interlocked.Read(ref _downloaded);

    public long Uploaded =>
        Interlocked.Read(ref _uploadedByClosedPeers) + _sessions.Values.Sum(session => session.Uploaded);

    /// <summary>Adds a peer found somewhere other than a tracker.</summary>
    public void AddPeer(IPEndPoint peer) => _known.TryAdd(peer, 0);

    public DownloadProgress Snapshot() => new()
    {
        PiecesHeld = _picker.Have.SetCount,
        PieceCount = _torrent.PieceCount,
        Downloaded = Downloaded,
        TotalLength = _torrent.TotalLength,
        ConnectedPeers = _sessions.Count,
        BytesPerSecond = Downloaded / Math.Max(_clock.Elapsed.TotalSeconds, 0.001),
        FailedPieces = _failedPieces,
        InterestedPeers = _sessions.Values.Count(s => s.State.PeerInterested),
        UnchokedPeers = _sessions.Values.Count(s => !s.State.ChokingPeer),
        FastestPeer = _sessions.IsEmpty ? 0 : _sessions.Values.Max(s => s.BytesPerSecond),
        MostRequestsInFlight = _sessions.IsEmpty ? 0 : _sessions.Values.Max(s => s.RequestsInFlight),
    };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        using HttpTrackerClient tracker = new();
        TrackerTiers tiers = new(_torrent.AnnounceTiers);

        using CancellationTokenSource finished = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Task announcing = tiers.Count > 0
            ? AnnounceLoopAsync(tracker, tiers, finished.Token)
            : Task.CompletedTask;
        Task connecting = ConnectLoopAsync(finished.Token);
        Task reporting = ReportLoopAsync(finished.Token);
        Task choking = ChokeLoopAsync(finished.Token);

        try
        {
            while (!_picker.IsComplete || KeepSeeding)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await finished.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(
                Quietly(announcing),
                Quietly(connecting),
                Quietly(reporting),
                Quietly(choking)).ConfigureAwait(false);

            SaveResume();

            // The tracker is told the download finished, which is what a ratio
            // is credited from on the trackers that keep one.
            if (tiers.Count > 0)
            {
                await TellTrackerAsync(tracker, tiers, TrackerEvent.Completed, CancellationToken.None)
                    .ConfigureAwait(false);
            }
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

            PeerSession session = new(connection, _torrent, _picker, this, this);
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

            if (_sessions.TryRemove(peer, out PeerSession? closing))
            {
                Interlocked.Add(ref _uploadedByClosedPeers, closing.Uploaded);
            }

            if (connection != null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Takes over a connection a peer made to this client. The handshake has
    /// already happened; from here an incoming peer is no different from one
    /// this client dialled.
    /// </summary>
    public async Task AcceptAsync(PeerConnection connection, CancellationToken cancellationToken)
    {
        IPEndPoint peer = connection.RemoteEndPoint;

        try
        {
            PeerSession session = new(connection, _torrent, _picker, this, this);
            _sessions[peer] = session;

            await session.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A peer that stops talking is ordinary.
        }
        finally
        {
            if (_sessions.TryRemove(peer, out PeerSession? closing))
            {
                Interlocked.Add(ref _uploadedByClosedPeers, closing.Uploaded);
            }

            await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The choking algorithm, which decides the few peers worth answering.
    ///
    /// Tit for tat: every ten seconds the peers that have given the most are
    /// unchoked and everyone else is choked. Four slots, because answering
    /// everybody at once means answering everybody slowly.
    ///
    /// On its own that would be a closed shop — a peer this client has never
    /// answered can never prove it is worth answering, and a client that has
    /// just started has nothing to offer anyone. So every third round one
    /// interested peer is unchoked at random regardless of what it has given.
    /// That is how a new client gets its first piece, and how the swarm
    /// discovers connections that turn out to be better than the ones in use.
    /// </summary>
    private async Task ChokeLoopAsync(CancellationToken cancellationToken)
    {
        const int UnchokeSlots = 4;
        const int OptimisticEveryRounds = 3;

        for (int round = 0; !cancellationToken.IsCancellationRequested; round++)
        {
            try
            {
                await Task.Delay(ChokeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            PeerSession[] interested = [.. _sessions.Values.Where(session => session.State.PeerInterested)];

            // While still downloading, a peer is judged by what it sends here;
            // once complete there is nothing left to judge but what it takes,
            // which is what keeps a seed's bandwidth going to the peers that
            // can actually use it.
            bool seeding = _picker.IsComplete;

            HashSet<PeerSession> unchoked =
            [
                .. interested
                    .OrderByDescending(session => seeding ? session.UploadBytesPerSecond : session.BytesPerSecond)
                    .Take(UnchokeSlots),
            ];

            if (round % OptimisticEveryRounds == 0)
            {
                PeerSession[] rest = [.. interested.Where(session => !unchoked.Contains(session))];
                if (rest.Length > 0)
                {
                    unchoked.Add(rest[Random.Shared.Next(rest.Length)]);
                }
            }

            foreach (PeerSession session in _sessions.Values)
            {
                try
                {
                    await session.SetChokingAsync(!unchoked.Contains(session), cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // A peer that has gone away needs no telling.
                }
            }
        }
    }

    public Task ReadBlockAsync(BlockRequest block, Memory<byte> buffer, CancellationToken cancellationToken) =>
        _storage.ReadAsync(((long)block.Piece * _torrent.PieceLength) + block.Begin, buffer, cancellationToken);

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
