using System.Diagnostics;
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

/// <summary>Where a block comes from when a peer asks for one.</summary>
public interface IBlockSource
{
    Task ReadBlockAsync(BlockRequest block, Memory<byte> buffer, CancellationToken cancellationToken);
}

/// <summary>
/// One peer, in both directions: ask for a piece and keep several block
/// requests outstanding so the connection is never idle for a round trip, and
/// answer whatever the peer asks for while it is unchoked.
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
    /// speeds up; half a second's absorbs that without asking for more than the
    /// peer can send before the requests go stale.
    /// </summary>
    private const double SecondsInFlight = 0.5;

    /// <summary>
    /// The largest block this client will serve. Everything asks for 16 KiB;
    /// anything much larger is either a broken client or an attempt to have
    /// this one read a great deal of disk per request.
    /// </summary>
    private const int MaxServedBlock = 32 * 1024;

    private static readonly TimeSpan MessageTimeout = TimeSpan.FromSeconds(30);

    private readonly PeerConnection _connection;
    private readonly Metainfo _torrent;
    private readonly PiecePicker _picker;
    private readonly IPieceReceiver _receiver;
    private readonly IBlockSource? _blocks;
    private readonly RateLimiter? _downloadLimit;
    private readonly RateLimiter? _uploadLimit;
    private readonly PeerState _state;

    private readonly HashSet<int> _requested = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private PieceAssembler? _piece;
    private int _outstanding;
    private long _blockBytes;
    private bool _counted;
    private byte _theirMetadataExtension;

    public PeerSession(
        PeerConnection connection,
        Metainfo torrent,
        PiecePicker picker,
        IPieceReceiver receiver,
        IBlockSource? blocks = null,
        int port = 6881,
        RateLimiter? downloadLimit = null,
        RateLimiter? uploadLimit = null)
    {
        Port = port;
        _downloadLimit = downloadLimit;
        _uploadLimit = uploadLimit;
        _connection = connection;
        _torrent = torrent;
        _picker = picker;
        _receiver = receiver;
        _blocks = blocks;
        _state = new PeerState(torrent.PieceCount);
    }

    /// <summary>The port this client tells peers it accepts connections on.</summary>
    public int Port { get; }

    public IPEndPoint RemoteEndPoint => _connection.RemoteEndPoint;

    public string ClientName => _connection.RemotePeerId.ClientName();

    /// <summary>Bytes of verified piece data this peer contributed.</summary>
    public long Downloaded { get; private set; }

    /// <summary>Bytes of block data served to this peer.</summary>
    public long Uploaded { get; private set; }

    /// <summary>Blocks arriving per second, whether or not their pieces verified.</summary>
    public double BytesPerSecond => _blockBytes / Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

    /// <summary>Blocks served per second.</summary>
    public double UploadBytesPerSecond => Uploaded / Math.Max(_clock.Elapsed.TotalSeconds, 0.001);

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

    /// <summary>
    /// Chokes or unchokes this peer. Called by the choking algorithm, which
    /// decides which few peers are worth answering at any moment.
    /// </summary>
    public async ValueTask SetChokingAsync(bool choking, CancellationToken cancellationToken)
    {
        if (choking == _state.ChokingPeer)
        {
            return;
        }

        _state.SetChoking(choking);
        await _connection.SendAsync(choking ? PeerMessage.Choke : PeerMessage.Unchoke, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A client that already has everything wants nothing, and saying
            // otherwise is a claim it would never follow up on.
            if (!_picker.IsComplete)
            {
                await _connection.SendAsync(PeerMessage.Interested, cancellationToken).ConfigureAwait(false);
                _state.SetInterested(true);
            }

            // A peer that speaks the extension protocol is told what this
            // client can do, including that it can hand over the torrent's own
            // description — which is how a magnet link gets started.
            if (_connection.RemoteReserved.SupportsExtensionProtocol)
            {
                await _connection.SendAsync(
                    MetadataExchange.Handshake(Port, _torrent.RawInfo.Length),
                    cancellationToken).ConfigureAwait(false);
            }

            // Peers want to know what this client already has, so that they can
            // decide whether it is worth anything to them.
            if (!_picker.Have.IsEmpty)
            {
                await _connection.SendAsync(PeerMessage.Bitfield(_picker.Have.ToArray()), cancellationToken)
                    .ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested)
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

        switch (message.Id)
        {
            case MessageId.Request:
                await ServeAsync(message.ReadRequest(), cancellationToken).ConfigureAwait(false);
                return;

            case MessageId.Cancel:
                // Requests are answered as they arrive rather than queued, so
                // by the time a cancel gets here the block has already gone.
                // Nothing to undo.
                return;

            case MessageId.Piece:
                await TakeBlockAsync(message, cancellationToken).ConfigureAwait(false);
                return;

            case MessageId.Extended:
                await HandleExtendedAsync(message, cancellationToken).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// The extension protocol, of which this client offers one extension: it
    /// will hand over the torrent's description to a peer that only has the
    /// infohash.
    /// </summary>
    private async Task HandleExtendedAsync(PeerMessage message, CancellationToken cancellationToken)
    {
        (byte extension, ReadOnlyMemory<byte> payload) = message.ReadExtended();

        if (extension == 0)
        {
            if (MetadataExchange.ReadHandshake(payload) is { } support)
            {
                _theirMetadataExtension = support.MetadataExtension;
            }

            return;
        }

        if (extension != MetadataExchange.OurMetadataExtension
            || _theirMetadataExtension == 0
            || !MetadataExchange.TryReadRequest(payload, out int piece))
        {
            return;
        }

        await _connection.SendAsync(
            PeerMessage.Extended(_theirMetadataExtension, MetadataExchange.DataMessage(piece, _torrent.RawInfo)),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a peer's request for a block. Every field is checked before a
    /// byte is read: the request names an offset and a length that this client
    /// would otherwise go and read from disk on the strength of a stranger's
    /// say-so.
    /// </summary>
    private async Task ServeAsync(BlockRequest request, CancellationToken cancellationToken)
    {
        if (_blocks == null || _state.ChokingPeer)
        {
            return;
        }

        if ((uint)request.Piece >= (uint)_torrent.PieceCount || !_picker.Have[request.Piece])
        {
            return;
        }

        int pieceLength = _torrent.PieceLengthAt(request.Piece);
        if (request.Length is <= 0 or > MaxServedBlock
            || request.Begin < 0
            || request.Begin + request.Length > pieceLength)
        {
            return;
        }

        byte[] block = new byte[request.Length];
        await _blocks.ReadBlockAsync(request, block, cancellationToken).ConfigureAwait(false);

        // The upload limit is applied here, where the bytes are about to leave.
        if (_uploadLimit != null)
        {
            await _uploadLimit.WaitAsync(block.Length, cancellationToken).ConfigureAwait(false);
        }

        await _connection.SendAsync(PeerMessage.Piece(request.Piece, request.Begin, block), cancellationToken)
            .ConfigureAwait(false);

        Uploaded += block.Length;
    }

    private async Task TakeBlockAsync(PeerMessage message, CancellationToken cancellationToken)
    {
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
        // Finishing the torrent mid-connection turns this side into a seed, and
        // the peer is told so rather than left expecting requests that will
        // never come.
        if (_picker.IsComplete)
        {
            if (_state.InterestedInPeer)
            {
                _state.SetInterested(false);
                await _connection.SendAsync(PeerMessage.NotInterested, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

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

            // A download cannot be un-received, so the limit is applied to what
            // is asked for rather than to what arrives: hold the request back
            // and the bytes never come. It is an approximation — a block
            // already requested still turns up — but it settles at the right
            // rate within a second or so.
            if (_downloadLimit is { IsLimited: true })
            {
                await _downloadLimit.WaitAsync(_piece.BlockAt(block).Length, cancellationToken).ConfigureAwait(false);
            }

            await _connection.SendAsync(PeerMessage.Request(_piece.BlockAt(block)), cancellationToken)
                .ConfigureAwait(false);
            _outstanding++;
        }
    }
}
