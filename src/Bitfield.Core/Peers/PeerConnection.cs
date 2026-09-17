using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Peers;

/// <summary>
/// A connection to one peer: the handshake, and messages in both directions.
///
/// Framing is all this layer does. What the messages mean, and what to ask for
/// next, belongs to whatever drives the connection.
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    /// <summary>
    /// A cap on any single message. The largest legitimate one is a block of
    /// 16 KiB with nine bytes of header; this leaves room for a peer that sends
    /// larger blocks than it should, while refusing a length prefix that would
    /// have this client allocate whatever a hostile peer asks it to.
    /// </summary>
    public const int MaxMessageLength = 128 * 1024;

    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly byte[] _lengthPrefix = new byte[4];

    private PeerConnection(Socket socket, IPEndPoint remote, PeerHandshake handshake)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
        RemoteEndPoint = remote;
        RemotePeerId = handshake.PeerId;
        RemoteReserved = handshake.Reserved;
    }

    public IPEndPoint RemoteEndPoint { get; }

    public PeerId RemotePeerId { get; }

    public ReservedBits RemoteReserved { get; }

    /// <summary>
    /// Connects and exchanges handshakes. Both sides send theirs at once rather
    /// than taking turns, so the exchange costs one round trip rather than two.
    /// </summary>
    public static async Task<PeerConnection> ConnectAsync(
        IPEndPoint peer,
        InfoHash infoHash,
        PeerId peerId,
        CancellationToken cancellationToken = default)
    {
        Socket socket = new(peer.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            await socket.ConnectAsync(peer, cancellationToken).ConfigureAwait(false);

            NetworkStream stream = new(socket, ownsSocket: false);

            PeerHandshake ours = new()
            {
                InfoHash = infoHash,
                PeerId = peerId,
                Reserved = ReservedBits.Ours,
            };

            await stream.WriteAsync(ours.ToArray(), cancellationToken).ConfigureAwait(false);

            byte[] reply = new byte[PeerHandshake.Size];
            await stream.ReadExactlyAsync(reply, cancellationToken).ConfigureAwait(false);

            PeerHandshake theirs = PeerHandshake.Parse(reply);

            // A peer serving a different torrent is no use, and this is the
            // first moment it can be told.
            if (theirs.InfoHash != infoHash)
            {
                throw new PeerProtocolException(
                    $"the peer answered for torrent {theirs.InfoHash}, not {infoHash}");
            }

            return new PeerConnection(socket, peer, theirs);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    public async ValueTask SendAsync(PeerMessage message, CancellationToken cancellationToken = default) =>
        await _stream.WriteAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reads the next message. A keep-alive — a length of zero and nothing
    /// else — comes back as one rather than being swallowed, because the caller
    /// is the one timing the connection out.
    /// </summary>
    public async ValueTask<PeerMessage> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        await _stream.ReadExactlyAsync(_lengthPrefix, cancellationToken).ConfigureAwait(false);

        int length = BinaryPrimitives.ReadInt32BigEndian(_lengthPrefix);
        if (length == 0)
        {
            return PeerMessage.KeepAlive;
        }

        if (length < 0 || length > MaxMessageLength)
        {
            throw new PeerProtocolException($"the peer announced a message of {length} bytes");
        }

        // One allocation per message. Worth pooling once many connections are
        // running at once; at one block in flight it is not what limits
        // anything.
        byte[] frame = new byte[length];
        await _stream.ReadExactlyAsync(frame, cancellationToken).ConfigureAwait(false);

        return PeerMessage.FromFrame(frame);
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _socket.Dispose();
    }
}
