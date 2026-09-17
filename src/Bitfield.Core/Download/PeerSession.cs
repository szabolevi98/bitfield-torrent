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
    /// Blocks outstanding at once. Sixteen is a quarter of a megabyte in
    /// flight, which keeps a connection busy across an ordinary round trip
    /// without asking a peer to hold more than it will.
    /// </summary>
    private const int RequestsInFlight = 16;

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private readonly PeerConnection _connection;
    private readonly Metainfo _torrent;
    private readonly PiecePicker _picker;
    private readonly IPieceReceiver _receiver;
    private readonly PeerState _state;

    private readonly HashSet<int> _requested = [];

    private PieceAssembler? _piece;
    private int _outstanding;

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
        }
    }

    private async Task HandleAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        if (_state.Apply(message))
        {
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
        for (int block = 0; block < _piece.BlockCount && _outstanding < RequestsInFlight; block++)
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
