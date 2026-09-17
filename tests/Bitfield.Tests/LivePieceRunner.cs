using System.Diagnostics;
using System.Net;
using System.Reflection;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Tests;

/// <summary>
/// Downloads one piece from a real peer and checks it against the torrent's own
/// hash — the thing milestone 3 exists to prove, and the first point at which
/// content actually crosses the network.
///
/// Peers are tried several at a time, because most of them will keep a client
/// that has nothing to offer choked for a while, and the first one to unchoke
/// is the one worth having.
///
///     dotnet run --project tests/Bitfield.Tests -- piece [path to a .torrent]
/// </summary>
internal static class LivePieceRunner
{
    private const int PeersToTry = 24;
    private const int AtOnce = 8;
    private const int RequestsInFlight = 5;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan UnchokeTimeout = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan BlockTimeout = TimeSpan.FromSeconds(20);

    public static int Run(string[] args)
    {
        Metainfo torrent = LoadTorrent(args.Length > 1 ? args[1] : null);
        PeerId peerId = PeerId.Generate();

        Console.WriteLine($"torrent   {torrent.Name}");
        Console.WriteLine($"infohash  {torrent.InfoHash}");
        Console.WriteLine($"pieces    {torrent.PieceCount:N0} of {torrent.PieceLength:N0} bytes");
        Console.WriteLine();

        IReadOnlyList<IPEndPoint> peers = FindPeers(torrent, peerId);
        if (peers.Count == 0)
        {
            Console.Error.WriteLine("no peers to try");
            return 1;
        }

        Console.WriteLine($"peers     {peers.Count} from the tracker, trying up to {PeersToTry}, {AtOnce} at a time");
        Console.WriteLine();

        return Download(torrent, peerId, peers) ? 0 : 1;
    }

    private static bool Download(Metainfo torrent, PeerId peerId, IReadOnlyList<IPEndPoint> peers)
    {
        using CancellationTokenSource finished = new();
        using SemaphoreSlim slots = new(AtOnce);
        object console = new();

        Stopwatch clock = Stopwatch.StartNew();
        int attempted = 0;
        int connected = 0;
        int unchoked = 0;

        Task<Result?>[] attempts = [.. peers.Take(PeersToTry).Select(async peer =>
        {
            await slots.WaitAsync(finished.Token).ConfigureAwait(false);
            Interlocked.Increment(ref attempted);
            try
            {
                Result result = await TryPeerAsync(torrent, peerId, peer, finished.Token).ConfigureAwait(false);

                Interlocked.Add(ref connected, result.Connected ? 1 : 0);
                Interlocked.Add(ref unchoked, result.Unchoked ? 1 : 0);

                lock (console)
                {
                    Console.WriteLine($"  {peer,-24} {result.Note}");
                }

                if (result.Verified)
                {
                    await finished.CancelAsync().ConfigureAwait(false);
                    return result;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception e)
            {
                lock (console)
                {
                    Console.WriteLine($"  {peer,-24} {Describe(e)}");
                }

                return null;
            }
            finally
            {
                slots.Release();
            }
        })];

        Result? winner = null;
        try
        {
            winner = Task.WhenAll(attempts).GetAwaiter().GetResult().FirstOrDefault(r => r is { Verified: true });
        }
        catch (OperationCanceledException)
        {
            winner = attempts.FirstOrDefault(t => t.IsCompletedSuccessfully && t.Result is { Verified: true })?.Result;
        }

        clock.Stop();

        Console.WriteLine();
        Console.WriteLine($"tried     {attempted} peers before stopping, {connected} connected, {unchoked} unchoked us");

        if (winner == null)
        {
            Console.Error.WriteLine("no peer sent a verified piece");
            return false;
        }

        Console.WriteLine();
        Console.WriteLine($"piece     {winner.Piece} of {torrent.PieceCount:N0}, {winner.Length:N0} bytes in {winner.Blocks} blocks");
        Console.WriteLine($"from      {winner.Peer} ({winner.Client})");
        Console.WriteLine($"took      {winner.Milliseconds:N0} ms from connect to verified");
        Console.WriteLine($"sha-1     {winner.Hash}");
        Console.WriteLine($"expected  {winner.ExpectedHash}");
        Console.WriteLine($"verified  {winner.Verified}");
        Console.WriteLine();
        Console.WriteLine($"total     {clock.ElapsedMilliseconds:N0} ms");

        return true;
    }

    private sealed record Result
    {
        public required IPEndPoint Peer { get; init; }

        public required string Note { get; init; }

        public bool Connected { get; init; }

        public bool Unchoked { get; init; }

        public bool Verified { get; init; }

        public string Client { get; init; } = "";

        public int Piece { get; init; }

        public int Length { get; init; }

        public int Blocks { get; init; }

        public long Milliseconds { get; init; }

        public string Hash { get; init; } = "";

        public string ExpectedHash { get; init; } = "";
    }

    private static async Task<Result> TryPeerAsync(
        Metainfo torrent,
        PeerId peerId,
        IPEndPoint peer,
        CancellationToken cancellationToken)
    {
        Stopwatch clock = Stopwatch.StartNew();

        using CancellationTokenSource connecting = Deadline(cancellationToken, ConnectTimeout);
        await using PeerConnection connection = await PeerConnection
            .ConnectAsync(peer, torrent.InfoHash, peerId, connecting.Token)
            .ConfigureAwait(false);

        string client = connection.RemotePeerId.ClientName();
        PeerState state = new(torrent.PieceCount);

        await connection.SendAsync(PeerMessage.Interested, cancellationToken).ConfigureAwait(false);
        state.SetInterested(true);

        // Wait for the peer to say what it has and to let this client ask for
        // it. Most peers hand over a bitfield at once and unchoke later, if at
        // all — a client with nothing to trade waits for an optimistic slot.
        using CancellationTokenSource waiting = Deadline(cancellationToken, UnchokeTimeout);
        while (!state.CanRequest || state.Available.IsEmpty)
        {
            PeerMessage message = await connection.ReceiveAsync(waiting.Token).ConfigureAwait(false);
            state.Apply(message);
        }

        int piece = FirstPieceHeldBy(state, torrent.PieceCount);
        if (piece < 0)
        {
            return new Result { Peer = peer, Connected = true, Unchoked = true, Note = $"{client}: holds nothing" };
        }

        PieceAssembler assembler = new(piece, torrent.PieceLengthAt(piece), torrent.PieceHash(piece));
        await FetchAsync(connection, state, assembler, cancellationToken).ConfigureAwait(false);

        clock.Stop();

        byte[] hash = System.Security.Cryptography.SHA1.HashData(assembler.Data.Span);
        bool verified = assembler.Verify();

        return new Result
        {
            Peer = peer,
            Connected = true,
            Unchoked = true,
            Verified = verified,
            Client = client,
            Piece = piece,
            Length = assembler.Length,
            Blocks = assembler.BlockCount,
            Milliseconds = clock.ElapsedMilliseconds,
            Hash = Convert.ToHexStringLower(hash),
            ExpectedHash = Convert.ToHexStringLower(torrent.PieceHash(piece)),
            Note = verified
                ? $"{client}: piece {piece} verified in {clock.ElapsedMilliseconds:N0} ms"
                : $"{client}: piece {piece} failed its hash",
        };
    }

    /// <summary>
    /// Asks for every block of the piece, keeping several requests outstanding
    /// so that the connection is not idle for a round trip after each one.
    /// </summary>
    private static async Task FetchAsync(
        PeerConnection connection,
        PeerState state,
        PieceAssembler assembler,
        CancellationToken cancellationToken)
    {
        Queue<BlockRequest> wanted = new(assembler.MissingBlocks());
        int outstanding = 0;

        while (!assembler.IsComplete)
        {
            while (outstanding < RequestsInFlight && wanted.Count > 0 && state.CanRequest)
            {
                await connection.SendAsync(PeerMessage.Request(wanted.Dequeue()), cancellationToken)
                    .ConfigureAwait(false);
                outstanding++;
            }

            using CancellationTokenSource waiting = Deadline(cancellationToken, BlockTimeout);
            PeerMessage message = await connection.ReceiveAsync(waiting.Token).ConfigureAwait(false);

            if (state.Apply(message))
            {
                continue;
            }

            if (message.Id != MessageId.Piece)
            {
                continue;
            }

            (int piece, int begin, ReadOnlyMemory<byte> block) = message.ReadPiece();
            outstanding--;

            if (piece == assembler.Index)
            {
                assembler.Add(begin, block.Span);
            }
        }
    }

    private static int FirstPieceHeldBy(PeerState state, int pieceCount)
    {
        for (int piece = 0; piece < pieceCount; piece++)
        {
            if (state.Available[piece])
            {
                return piece;
            }
        }

        return -1;
    }

    private static IReadOnlyList<IPEndPoint> FindPeers(Metainfo torrent, PeerId peerId)
    {
        using HttpTrackerClient client = new();
        TrackerTiers tiers = new(torrent.AnnounceTiers);

        AnnounceRequest request = new()
        {
            InfoHash = torrent.InfoHash,
            PeerId = peerId,
            Port = 6881,
            Left = torrent.TotalLength,
            Event = TrackerEvent.Started,
            NumWant = 100,
        };

        try
        {
            (Uri tracker, AnnounceResponse response) = tiers
                .AnnounceAsync(request, client.AnnounceAsync)
                .GetAwaiter().GetResult();

            Console.WriteLine($"tracker   {tracker}");

            // Say goodbye before spending a minute on peers, so the tracker is
            // not handing this client's address around while it is not
            // listening for anything.
            try
            {
                client.AnnounceAsync(tracker, request with { Event = TrackerEvent.Stopped, NumWant = 0 })
                    .GetAwaiter().GetResult();
            }
            catch (TrackerException)
            {
                // Not worth failing the run over.
            }

            return response.Peers;
        }
        catch (TrackerException e)
        {
            Console.Error.WriteLine($"tracker   {e.Message}");
            return [];
        }
    }

    private static Metainfo LoadTorrent(string? path)
    {
        if (path != null)
        {
            return Metainfo.Load(path);
        }

        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("debian-13.7.0-amd64-netinst.iso.torrent")!;
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return Metainfo.Parse(buffer.ToArray());
    }

    private static CancellationTokenSource Deadline(CancellationToken cancellationToken, TimeSpan timeout)
    {
        CancellationTokenSource source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout);
        return source;
    }

    private static string Describe(Exception e) => e switch
    {
        PeerProtocolException => $"protocol: {e.Message}",
        System.Net.Sockets.SocketException socket => socket.SocketErrorCode.ToString(),
        EndOfStreamException => "closed the connection",
        _ => e.GetType().Name,
    };
}
