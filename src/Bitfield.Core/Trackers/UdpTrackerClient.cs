using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Bitfield.Core.Trackers;

/// <summary>
/// Announces over UDP (BEP 15), which is what most public trackers run.
///
/// It exists because HTTP announces cost a TCP handshake and a set of headers
/// per peer per torrent, and a tracker serving millions of them notices. The
/// exchange here is two datagrams: a connect that returns a connection id, and
/// the announce itself. The connection id is the anti-spoofing measure — a
/// forged announce would need to have received the connect reply, which an
/// attacker forging a source address does not — and it expires after a minute,
/// so it is cached for rather less than that.
/// </summary>
public sealed class UdpTrackerClient : IDisposable
{
    /// <summary>The fixed value every connect request opens with.</summary>
    private const long ProtocolId = 0x0000_0417_2710_1980;

    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionError = 3;

    /// <summary>
    /// A connection id is good for a minute; anything older is refused. Fifty
    /// seconds leaves room for the announce that uses it to arrive in time.
    /// </summary>
    private static readonly TimeSpan ConnectionIdLifetime = TimeSpan.FromSeconds(50);

    private readonly Dictionary<IPEndPoint, (long Id, DateTime Taken)> _connections = [];
    private readonly SemaphoreSlim _gate = new(1);

    /// <summary>
    /// How long to wait for each reply, and how many times to ask. The
    /// specification's schedule starts at 15 seconds and doubles to over an
    /// hour, which suits a client that has nothing else to do; a client with
    /// other trackers to try is better off failing over.
    /// </summary>
    public TimeSpan FirstTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public int Attempts { get; init; } = 3;

    public async Task<AnnounceResponse> AnnounceAsync(
        Uri announce,
        AnnounceRequest request,
        CancellationToken cancellationToken = default)
    {
        if (announce.Scheme != "udp")
        {
            throw new TrackerException($"{announce.Scheme} is not a UDP tracker");
        }

        IPEndPoint tracker = await ResolveAsync(announce, cancellationToken).ConfigureAwait(false);

        using Socket socket = new(tracker.AddressFamily, SocketType.Dgram, ProtocolType.Udp);

        long connectionId = await ConnectionIdAsync(socket, tracker, cancellationToken).ConfigureAwait(false);

        try
        {
            return await AnnounceAsync(socket, tracker, connectionId, request, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TrackerException)
        {
            // A connection id that has just expired looks exactly like a
            // tracker refusing the announce, so it is worth one more go with a
            // fresh one before the failure is believed. If the refusal was real
            // it simply happens again.
            if (!await ForgetAsync(tracker, cancellationToken).ConfigureAwait(false))
            {
                throw;
            }

            long fresh = await ConnectionIdAsync(socket, tracker, cancellationToken).ConfigureAwait(false);
            return await AnnounceAsync(socket, tracker, fresh, request, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<AnnounceResponse> AnnounceAsync(
        Socket socket,
        IPEndPoint tracker,
        long connectionId,
        AnnounceRequest request,
        CancellationToken cancellationToken)
    {
        int transaction = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        byte[] datagram = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(datagram, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), ActionAnnounce);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(12), transaction);
        request.InfoHash.WriteTo(datagram.AsSpan(16));
        request.PeerId.WriteTo(datagram.AsSpan(36));
        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(datagram.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(80), (int)request.Event);
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(84), 0); // IP: let the tracker use the source address
        BinaryPrimitives.WriteUInt32BigEndian(datagram.AsSpan(88), request.Key ?? 0);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(92), request.NumWant ?? -1);
        BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(96), (ushort)request.Port);

        byte[] reply = await ExchangeAsync(socket, tracker, datagram, transaction, ActionAnnounce, cancellationToken)
            .ConfigureAwait(false);

        if (reply.Length < 20)
        {
            throw new TrackerException($"the announce reply is {reply.Length} bytes, which is too short to be one");
        }

        List<IPEndPoint> peers = [];
        for (int offset = 20; offset + 6 <= reply.Length; offset += 6)
        {
            IPAddress address = new(reply.AsSpan(offset, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(offset + 4, 2));

            if (port > 0 && !IPAddress.Any.Equals(address))
            {
                IPEndPoint peer = new(address, port);
                if (!peers.Contains(peer))
                {
                    peers.Add(peer);
                }
            }
        }

        int interval = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(8));

        return new AnnounceResponse
        {
            Interval = TimeSpan.FromSeconds(interval is > 0 and <= 86_400 ? interval : 1800),
            Leechers = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(12)),
            Seeders = BinaryPrimitives.ReadInt32BigEndian(reply.AsSpan(16)),
            Peers = peers,
        };
    }

    private async Task<long> ConnectionIdAsync(Socket socket, IPEndPoint tracker, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(tracker, out (long Id, DateTime Taken) cached)
                && DateTime.UtcNow - cached.Taken < ConnectionIdLifetime)
            {
                return cached.Id;
            }
        }
        finally
        {
            _gate.Release();
        }

        int transaction = RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

        byte[] datagram = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(datagram, ProtocolId);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(8), ActionConnect);
        BinaryPrimitives.WriteInt32BigEndian(datagram.AsSpan(12), transaction);

        byte[] reply = await ExchangeAsync(socket, tracker, datagram, transaction, ActionConnect, cancellationToken)
            .ConfigureAwait(false);

        if (reply.Length < 16)
        {
            throw new TrackerException($"the connect reply is {reply.Length} bytes, not 16");
        }

        long connectionId = BinaryPrimitives.ReadInt64BigEndian(reply.AsSpan(8));

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _connections[tracker] = (connectionId, DateTime.UtcNow);
        }
        finally
        {
            _gate.Release();
        }

        return connectionId;
    }

    /// <summary>
    /// Sends a datagram and waits for the answer to it. UDP has no connection
    /// and no ordering, so the transaction id is what says a reply belongs to
    /// this request: anything else that arrives is somebody else's answer, or
    /// somebody's attempt to be mistaken for one, and is dropped.
    /// </summary>
    private async Task<byte[]> ExchangeAsync(
        Socket socket,
        IPEndPoint tracker,
        byte[] datagram,
        int transaction,
        int expectedAction,
        CancellationToken cancellationToken)
    {
        TimeSpan timeout = FirstTimeout;
        byte[] buffer = new byte[2048];

        for (int attempt = 0; attempt < Attempts; attempt++)
        {
            await socket.SendToAsync(datagram, SocketFlags.None, tracker, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            waiting.CancelAfter(timeout);

            try
            {
                while (true)
                {
                    SocketReceiveFromResult received = await socket
                        .ReceiveFromAsync(buffer, SocketFlags.None, tracker, waiting.Token)
                        .ConfigureAwait(false);

                    if (received.ReceivedBytes < 8)
                    {
                        continue;
                    }

                    int action = BinaryPrimitives.ReadInt32BigEndian(buffer);
                    if (BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(4)) != transaction)
                    {
                        continue;
                    }

                    if (action == ActionError)
                    {
                        string message = System.Text.Encoding.UTF8.GetString(
                            buffer.AsSpan(8, received.ReceivedBytes - 8)).Trim('\0');
                        throw new TrackerException(message.Length > 0 ? message : "the tracker refused the announce")
                        {
                            FromTracker = true,
                        };
                    }

                    if (action != expectedAction)
                    {
                        continue;
                    }

                    return buffer[..received.ReceivedBytes];
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                timeout *= 2;
            }
            catch (SocketException e)
            {
                throw new TrackerException($"{tracker} could not be reached: {e.SocketErrorCode}");
            }
        }

        throw new TrackerException($"{tracker} did not answer after {Attempts} attempts");
    }

    private static async Task<IPEndPoint> ResolveAsync(Uri announce, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(announce.Host, out IPAddress? address))
        {
            return new IPEndPoint(address, announce.Port);
        }

        try
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(announce.Host, cancellationToken)
                .ConfigureAwait(false);

            if (addresses.Length == 0)
            {
                throw new TrackerException($"{announce.Host} does not resolve");
            }

            return new IPEndPoint(addresses[0], announce.Port);
        }
        catch (SocketException e)
        {
            throw new TrackerException($"{announce.Host} does not resolve: {e.SocketErrorCode}");
        }
    }

    private async Task<bool> ForgetAsync(IPEndPoint tracker, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _connections.Remove(tracker);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
