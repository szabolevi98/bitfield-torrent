using System.Net;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Download;

/// <summary>Where a verified piece goes once a session has one.</summary>
public interface IPieceReceiver
{
    Task PieceVerifiedAsync(int piece, ReadOnlyMemory<byte> data, CancellationToken cancellationToken);

    /// <summary>A piece that failed its hash, so the download can count it.</summary>
    void PieceFailed(int piece, IPEndPoint peer);
}

/// <summary>
/// One peer, driven for as long as it is useful: ask for a piece, keep several
/// block requests outstanding so the connection is never idle for a round trip,
/// hand over whatever verifies, and take the next one.
/// </summary>
public sealed class PeerSession
{
    /// <summary>
    /// How many blocks to keep outstanding, at the least and at the most. The
    /// floor keeps a connection that has not proved anything yet from going
    /// idle for a round trip after every block; the ceiling keeps a fast peer
    /// from being asked to hold two megabytes of requests, which some will
    /// refuse and others will answer slowly.
    /// </summary>
    private const int MinRequestsInFlight = 4;

    private const int MaxRequestsInFlight = 96;

    /// <summary>
    /// How much of a second's worth of blocks to keep in flight. Requesting
    /// exactly one second's worth means the pipeline empties whenever the peer
    /// speeds up; half again as much absorbs that without asking for more than
    /// the peer can send before the requests go stale.
    /// </summary>
    private const double SecondsInFlight = 0.5;

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private readonly PeerConnection _connection;
    private readonly Metainfo _torrent;
    private readonly PiecePicker _picker;
    private readonly IPieceReceiver _receiver;
    private readonly PeerState _state;

    private readonly HashSet<int> _requested = [];
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private PieceAssembler? _piece;
    private int _outstanding;
    private long _blockBytes;
    private bool _counted;

    public PeerSession(
        PeerConnection connection,
        Metainfo torrent,
        PiecePicker picker,
        IPieceReceiver receiver)
    {
        _connection = connection;
        _torrent = torrent;
        _picker = picker;
        _receiver = receiver;
        _state = new PeerState(torrent.PieceCount);
    }

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public string ClientName => _connection.RemotePeerId.ClientName();

    /// <summary>Bytes of verified piece data this peer contributed.</summary>
    public long Downloaded { get; private set; }

    /// <summary>Blocks arriving per second, whether or not their pieces verified.</summary>
    public double BytesPerSecond => _blockBytes / Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

    /// <summary>
    /// How many requests this peer is worth keeping outstanding, from what it
    /// has actually delivered. A peer sending 4 MB/s is asked for far more at
    /// once than one sending 40 KB/s, and neither number has to be guessed at
    /// or configured.
    /// </summary>
    public int RequestsInFlight => Math.Clamp(
        (int)(BytesPerSecond * SecondsInFlight / BlockRequest.BlockSize),
        MinRequestsInFlight,
        MaxRequestsInFlight);

    public PeerState State => _state;

    /// <summary>
    /// Tells this peer about a piece that has just been verified. Called from
    /// the download rather than from this session's own loop, which is why the
    /// connection serialises its writes.
    /// </summary>
    public ValueTask AnnounceHaveAsync(int piece, CancellationToken cancellationToken) =>
        _connection.SendAsync(PeerMessage.Have(piece), cancellationToken);

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _connection.SendAsync(PeerMessage.Interested, cancellationToken).ConfigureAwait(false);
            _state.SetInterested(true);

            // Peers want to know what this client already has, so that they can
            // decide whether it is worth anything to them.
            if (!_picker.Have.IsEmpty)
            {
                await _connection.SendAsync(PeerMessage.Bitfield(_picker.Have.ToArray()), cancellationToken)
                    .ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested && !_picker.IsComplete)
            {
                await FillPipelineAsync(cancellationToken).ConfigureAwait(false);

                using CancellationTokenSource waiting =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                waiting.CancelAfter(MessageTimeout);

                PeerMessage message = await _connection.ReceiveAsync(waiting.Token).ConfigureAwait(false);
                await HandleAsync(message, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Whatever was half-finished goes back, or no other peer will be
            // allowed to fetch it.
            if (_piece != null)
            {
                _picker.Release(_piece.Index);
                _piece = null;
            }

            // And this peer's pieces stop counting towards how rare anything
            // is, now that it is no longer somewhere to get them.
            if (_counted)
            {
                _picker.RemoveAvailability(_state.Available);
                _counted = false;
            }
        }
    }

    private async Task HandleAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        // Rarest first is only as good as its counts, so every claim a peer
        // makes about what it holds goes into them as it arrives — but only
        // once. A peer repeating a piece it already announced in its bitfield
        // would otherwise be counted twice and taken away once, and the piece
        // would look commoner than it is for as long as the client runs.
        if (message.Id == MessageId.Have)
        {
            int announced = message.ReadHave();
            bool alreadyKnown = (uint)announced < (uint)_torrent.PieceCount && _state.Available[announced];

            _state.Apply(message);

            if (!alreadyKnown)
            {
                _picker.AddAvailability(announced);
                _counted = true;
            }

            return;
        }

        if (_state.Apply(message))
        {
            if (message.Id == MessageId.Bitfield)
            {
                _picker.AddAvailability(_state.Available);
                _counted = true;
            }

            // Being choked cancels every outstanding request at the far end, so
            // the piece has to be started over and given back in the meantime.
            if (message.Id == MessageId.Choke && _piece != null)
            {
                _picker.Release(_piece.Index);
                _piece = null;
                _requested.Clear();
                _outstanding = 0;
            }

            return;
        }

        if (message.Id != MessageId.Piece)
        {
            return;
        }

        (int index, int begin, ReadOnlyMemory<byte> block) = message.ReadPiece();
        _outstanding = Math.Max(0, _outstanding - 1);
        _blockBytes += block.Length;

        if (_piece == null || index != _piece.Index || !_piece.Add(begin, block.Span))
        {
            return;
        }

        if (!_piece.IsComplete)
        {
            return;
        }

        PieceAssembler finished = _piece;
        _piece = null;

        if (finished.Verify())
        {
            Downloaded += finished.Length;
            await _receiver.PieceVerifiedAsync(finished.Index, finished.Data, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // Somebody sent bytes that were not the ones asked for. The piece
            // goes back to be fetched again, from whoever answers next.
            _receiver.PieceFailed(finished.Index, RemoteEndPoint);
            _picker.Release(finished.Index);
        }
    }

    private async Task FillPipelineAsync(CancellationToken cancellationToken)
    {
        if (!_state.CanRequest)
        {
            return;
        }

        if (_piece == null)
        {
            int? next = _picker.Take(_state.Available);
            if (next == null)
            {
                return;
            }

            _piece = new PieceAssembler(next.Value, _torrent.PieceLengthAt(next.Value), _torrent.PieceHash(next.Value));
            _requested.Clear();
            _outstanding = 0;
        }

        // Which blocks are still wanted and not already asked for. Tracking the
        // requests rather than counting them matters because blocks come back
        // in whatever order the peer sends them, and anything derived from the
        // order would either ask twice or never ask at all.
        int wanted = RequestsInFlight;
        for (int block = 0; block < _piece.BlockCount && _outstanding < wanted; block++)
        {
            if (_piece.HasBlock(block) || !_requested.Add(block))
            {
                continue;
            }

            await _connection.SendAsync(PeerMessage.Request(_piece.BlockAt(block)), cancellationToken)
                .ConfigureAwait(false);
            _outstanding++;
        }
    }
}
