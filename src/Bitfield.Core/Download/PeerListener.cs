using System.Net;
using System.Net.Sockets;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Download;

/// <summary>
/// Accepts peers that connect to this client.
///
/// Without it a client can only ever reach out, which is enough to download
/// and almost useless for uploading: the peers that would take pieces from it
/// mostly cannot dial in, and on a tracker that keeps ratios that is the
/// difference between contributing and not.
/// </summary>
public sealed class PeerListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<InfoHash, TorrentDownload?> _lookup;

    public PeerListener(int port, Func<InfoHash, TorrentDownload?> lookup, IPAddress? address = null)
    {
        _lookup = lookup;
        _listener = new TcpListener(address ?? IPAddress.Any, port);
        _listener.Start();
    }

    /// <summary>The port actually bound, which matters when port 0 was asked for.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = HandleAsync(socket, cancellationToken);
        }
    }

    private async Task HandleAsync(Socket socket, CancellationToken cancellationToken)
    {
        TorrentDownload? torrent = null;

        try
        {
            using CancellationTokenSource handshaking =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshaking.CancelAfter(TimeSpan.FromSeconds(10));

            PeerConnection connection = await PeerConnection.AcceptAsync(
                socket,
                infoHash =>
                {
                    torrent = _lookup(infoHash);
                    return torrent?.PeerId;
                },
                handshaking.Token).ConfigureAwait(false);

            if (torrent == null)
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                return;
            }

            await torrent.AcceptAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Anything that connects and then does not speak the protocol is
            // simply not a peer, and there is nothing to do about it.
            socket.Dispose();
        }
    }

    public ValueTask DisposeAsync()
    {
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}
