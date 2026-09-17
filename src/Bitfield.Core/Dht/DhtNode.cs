using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Dht;

/// <summary>
/// A node in the BitTorrent DHT: the distributed directory that lets a client
/// find a torrent's peers with no tracker involved.
///
/// The idea is that the list of peers for a torrent is held by the nodes whose
/// ids are numerically closest to its infohash. Nobody knows the whole network,
/// so a lookup works by asking the closest nodes it knows for nodes closer
/// still, and repeating. Each round roughly halves the distance, so a few
/// million nodes are traversed in about twenty questions.
/// </summary>
public sealed class DhtNode : IAsyncDisposable
{
    /// <summary>How many nodes a lookup keeps, and asks, at a time.</summary>
    private const int Closest = 8;

    private const int Concurrency = 3;

    private const int MaxLookupRounds = 12;

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(4);

    /// <summary>
    /// How long a token this node hands out stays good. Tokens stop a node
    /// being told that some third party is a peer: the announcer has to quote
    /// the token it was given, which it can only have received at its own
    /// address.
    /// </summary>
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(5);

    private readonly Socket _socket;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<KrpcMessage>> _pending = new();
    private readonly ConcurrentDictionary<InfoHash, ConcurrentDictionary<IPEndPoint, DateTime>> _peers = new();

    private byte[] _tokenSecret = RandomNumberGenerator.GetBytes(16);
    private byte[] _previousTokenSecret = RandomNumberGenerator.GetBytes(16);
    private DateTime _tokenRotated = DateTime.UtcNow;
    private int _transactionCounter;

    public DhtNode(NodeId? id = null, int port = 0, IPAddress? address = null)
    {
        Id = id ?? NodeId.Random();
        Table = new RoutingTable(Id);

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(address ?? IPAddress.Any, port));
    }

    public NodeId Id { get; }

    public RoutingTable Table { get; }

    public int Port => ((IPEndPoint)_socket.LocalEndPoint!).Port;

    /// <summary>The port this client accepts peer connections on, announced to the DHT.</summary>
    public int PeerPort { get; init; } = 6881;

    public event Action<string>? Note;

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            SocketReceiveFromResult received;
            try
            {
                received = await _socket
                    .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (SocketException)
            {
                // An ICMP rejection from an earlier datagram surfaces here; the
                // node it came from simply is not there.
                continue;
            }

            if (!KrpcMessage.TryParse(buffer.AsMemory(0, received.ReceivedBytes), out KrpcMessage? message))
            {
                continue;
            }

            IPEndPoint from = (IPEndPoint)received.RemoteEndPoint;

            if (message!.SenderId is { } senderId)
            {
                Table.Add(new DhtContact(senderId, from));
            }

            if (message.Kind == KrpcKind.Query)
            {
                await AnswerAsync(message, from, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // An answer nobody is waiting for is either late or somebody's
            // attempt to be mistaken for an answer.
            if (_pending.TryRemove(Key(message.TransactionId), out TaskCompletionSource<KrpcMessage>? waiting))
            {
                waiting.TrySetResult(message);
            }
        }
    }

    // ------------------------------------------------------------- answering

    private async Task AnswerAsync(KrpcMessage query, IPEndPoint from, CancellationToken cancellationToken)
    {
        KrpcMessage reply = query.Query switch
        {
            "ping" => KrpcMessage.MakeResponse(query.TransactionId, ("id", new BString(Id.ToArray()))),
            "find_node" => FindNode(query),
            "get_peers" => GetPeers(query, from),
            "announce_peer" => AnnouncePeer(query, from),
            _ => KrpcMessage.MakeError(query.TransactionId, 204, "unknown query"),
        };

        await SendAsync(reply, from, cancellationToken).ConfigureAwait(false);
    }

    private KrpcMessage FindNode(KrpcMessage query)
    {
        if (query.Body?.GetByteString("target") is not { Length: NodeId.Size } target)
        {
            return KrpcMessage.MakeError(query.TransactionId, 203, "no target");
        }

        return KrpcMessage.MakeResponse(
            query.TransactionId,
            ("id", new BString(Id.ToArray())),
            ("nodes", new BString(Compact(Table.Closest(new NodeId(target.Span))))));
    }

    private KrpcMessage GetPeers(KrpcMessage query, IPEndPoint from)
    {
        if (query.Body?.GetByteString("info_hash") is not { Length: InfoHash.Size } hash)
        {
            return KrpcMessage.MakeError(query.TransactionId, 203, "no infohash");
        }

        InfoHash infoHash = new(hash.Span);
        List<(string, BValue)> values =
        [
            ("id", new BString(Id.ToArray())),
            ("token", new BString(TokenFor(from))),
        ];

        // Peers if this node has any, and the nodes to ask next if it does not.
        // Nothing stops it sending both, but a lookup only follows the nodes
        // when it has no peers yet.
        if (_peers.TryGetValue(infoHash, out ConcurrentDictionary<IPEndPoint, DateTime>? known) && !known.IsEmpty)
        {
            values.Add(("values", new BList(
            [
                .. known.Keys.Take(50).Select(peer => (BValue)new BString(CompactPeer(peer))),
            ])));
        }
        else
        {
            values.Add(("nodes", new BString(Compact(Table.Closest(NodeId.From(infoHash))))));
        }

        return KrpcMessage.MakeResponse(query.TransactionId, [.. values]);
    }

    private KrpcMessage AnnouncePeer(KrpcMessage query, IPEndPoint from)
    {
        if (query.Body?.GetByteString("info_hash") is not { Length: InfoHash.Size } hash
            || query.Body.GetByteString("token") is not { } token)
        {
            return KrpcMessage.MakeError(query.TransactionId, 203, "no infohash or token");
        }

        // The token proves the announcer received this node's earlier reply at
        // the address it is announcing, which is what stops the DHT being used
        // to list somebody else as a peer.
        if (!IsOurToken(token.Span, from))
        {
            return KrpcMessage.MakeError(query.TransactionId, 203, "bad token");
        }

        long port = query.Body.GetInteger("port") ?? 0;
        if (query.Body.GetInteger("implied_port") == 1)
        {
            port = from.Port;
        }

        if (port is > 0 and <= ushort.MaxValue)
        {
            _peers.GetOrAdd(new InfoHash(hash.Span), _ => new ConcurrentDictionary<IPEndPoint, DateTime>())
                [new IPEndPoint(from.Address, (int)port)] = DateTime.UtcNow;
        }

        return KrpcMessage.MakeResponse(query.TransactionId, ("id", new BString(Id.ToArray())));
    }

    // -------------------------------------------------------------- querying

    public async Task<KrpcMessage?> QueryAsync(
        IPEndPoint node,
        string query,
        (string Key, BValue Value)[] arguments,
        CancellationToken cancellationToken)
    {
        byte[] transaction = BitConverter.GetBytes((ushort)Interlocked.Increment(ref _transactionCounter));

        (string, BValue)[] withId = [("id", new BString(Id.ToArray())), .. arguments];
        KrpcMessage message = KrpcMessage.MakeQuery(transaction, query, withId);

        TaskCompletionSource<KrpcMessage> waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[Key(transaction)] = waiting;

        try
        {
            await SendAsync(message, node, cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(QueryTimeout);

            return await waiting.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            _pending.TryRemove(Key(transaction), out _);
        }
    }

    /// <summary>
    /// Joins the network. A node with an empty table has to be told about one
    /// other node; from there, asking for the nodes closest to its own id fills
    /// in a neighbourhood.
    /// </summary>
    public async Task<int> BootstrapAsync(IEnumerable<string> routers, CancellationToken cancellationToken)
    {
        // A table restored from the last run already is a way in, and the
        // bootstrap routers carry the first question of every client on the
        // network — there is no reason to ask them again.
        bool needRouters = Table.Count < RoutingTable.BucketSize;

        foreach (string router in needRouters ? routers : [])
        {
            string[] parts = router.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            {
                continue;
            }

            try
            {
                IPAddress[] addresses = await Dns.GetHostAddressesAsync(parts[0], cancellationToken)
                    .ConfigureAwait(false);

                foreach (IPAddress address in addresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(1))
                {
                    KrpcMessage? reply = await QueryAsync(
                        new IPEndPoint(address, port),
                        "find_node",
                        [("target", new BString(Id.ToArray()))],
                        cancellationToken).ConfigureAwait(false);

                    if (reply?.Body?.GetByteString("nodes") is { } nodes)
                    {
                        foreach (DhtContact contact in DhtContact.ReadCompact(nodes.Span))
                        {
                            Table.Add(contact);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // A router that is down is one of several.
            }
        }

        Note?.Invoke(needRouters
            ? $"bootstrapped through the routers to {Table.Count} nodes"
            : $"started from {Table.Count} saved nodes, routers not needed");

        // One lookup for this node's own id fills the near buckets, which is
        // what makes the table useful for answering other people's questions.
        await LookupAsync(Id, null, cancellationToken).ConfigureAwait(false);

        return Table.Count;
    }

    /// <summary>Finds a torrent's peers, asking nobody but the DHT.</summary>
    public async Task<IReadOnlyList<IPEndPoint>> FindPeersAsync(
        InfoHash infoHash,
        CancellationToken cancellationToken)
    {
        LookupResult result = await LookupAsync(NodeId.From(infoHash), infoHash, cancellationToken)
            .ConfigureAwait(false);

        return result.Peers;
    }

    /// <summary>
    /// Tells the DHT that this client is a peer for a torrent, so that others
    /// looking for it are given this address.
    /// </summary>
    public async Task<int> AnnounceAsync(InfoHash infoHash, CancellationToken cancellationToken)
    {
        LookupResult result = await LookupAsync(NodeId.From(infoHash), infoHash, cancellationToken)
            .ConfigureAwait(false);

        int announced = 0;

        foreach ((DhtContact node, byte[] token) in result.Tokens.Take(Closest))
        {
            KrpcMessage? reply = await QueryAsync(
                node.EndPoint,
                "announce_peer",
                [
                    ("info_hash", new BString(infoHash.ToArray())),
                    ("port", new BInteger(PeerPort)),
                    ("token", new BString(token)),
                ],
                cancellationToken).ConfigureAwait(false);

            if (reply is { Kind: KrpcKind.Response })
            {
                announced++;
            }
        }

        return announced;
    }

    private sealed record LookupResult(
        IReadOnlyList<IPEndPoint> Peers,
        IReadOnlyList<(DhtContact Node, byte[] Token)> Tokens);

    /// <summary>
    /// The iterative lookup at the heart of the whole thing. Ask the closest
    /// nodes known for nodes closer to the target; whatever comes back that is
    /// closer becomes the next thing to ask. It stops when a round turns up
    /// nothing nearer, which means the neighbourhood of the target has been
    /// reached.
    /// </summary>
    private async Task<LookupResult> LookupAsync(
        NodeId target,
        InfoHash? infoHash,
        CancellationToken cancellationToken)
    {
        Dictionary<NodeId, DhtContact> shortlist = [];
        HashSet<NodeId> asked = [];
        List<IPEndPoint> peers = [];
        List<(DhtContact, byte[])> tokens = [];

        foreach (DhtContact contact in Table.Closest(target, Closest * 2))
        {
            shortlist[contact.Id] = contact;
        }

        for (int round = 0; round < MaxLookupRounds && shortlist.Count > 0; round++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            DhtContact[] next =
            [
                .. shortlist.Values
                    .Where(contact => !asked.Contains(contact.Id))
                    .Order(Comparer<DhtContact>.Create((a, b) => NodeId.CompareDistance(target, a.Id, b.Id)))
                    .Take(Concurrency),
            ];

            if (next.Length == 0)
            {
                break;
            }

            foreach (DhtContact contact in next)
            {
                asked.Add(contact.Id);
            }

            KrpcMessage?[] replies = await Task.WhenAll(next.Select(contact => infoHash is { } hash
                ? QueryAsync(contact.EndPoint, "get_peers", [("info_hash", new BString(hash.ToArray()))], cancellationToken)
                : QueryAsync(contact.EndPoint, "find_node", [("target", new BString(target.ToArray()))], cancellationToken)))
                .ConfigureAwait(false);

            for (int i = 0; i < replies.Length; i++)
            {
                KrpcMessage? reply = replies[i];

                if (reply is not { Kind: KrpcKind.Response, Body: { } body })
                {
                    Table.Failed(next[i].Id);
                    continue;
                }

                if (body.GetByteString("token") is { } token)
                {
                    tokens.Add((next[i], token.Bytes.ToArray()));
                }

                if (body.GetList("values") is { } values)
                {
                    foreach (BValue value in values.Items)
                    {
                        if (value is BString { Length: 6 } compact)
                        {
                            IPEndPoint peer = ReadPeer(compact.Span);
                            if (!peers.Contains(peer))
                            {
                                peers.Add(peer);
                            }
                        }
                    }
                }

                if (body.GetByteString("nodes") is { } nodes)
                {
                    foreach (DhtContact contact in DhtContact.ReadCompact(nodes.Span))
                    {
                        shortlist.TryAdd(contact.Id, contact);
                        Table.Add(contact);
                    }
                }
            }

            // Enough peers to be getting on with; the rest of the
            // neighbourhood can wait for the announce that follows.
            if (peers.Count >= 50)
            {
                break;
            }
        }

        tokens.Sort((a, b) => NodeId.CompareDistance(target, a.Item1.Id, b.Item1.Id));
        return new LookupResult(peers, tokens);
    }

    /// <summary>
    /// The nodes this client knows, written out so that the next run can skip
    /// the bootstrap routers entirely. A saved table is also the polite thing
    /// to keep: those routers carry the whole network's first question.
    /// </summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        IReadOnlyList<DhtContact> contacts = Table.All();
        byte[] nodes = new byte[contacts.Count * DhtContact.CompactSize];

        for (int i = 0; i < contacts.Count; i++)
        {
            contacts[i].WriteTo(nodes.AsSpan(i * DhtContact.CompactSize));
        }

        BDictionary saved = new(
        [
            new KeyValuePair<ReadOnlyMemory<byte>, BValue>("id"u8.ToArray(), new BString(Id.ToArray())),
            new KeyValuePair<ReadOnlyMemory<byte>, BValue>("nodes"u8.ToArray(), new BString(nodes)),
        ]);

        string temporary = path + ".new";
        File.WriteAllBytes(temporary, BencodeWriter.Encode(saved));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The node id a saved table was written under.
    ///
    /// A node has to keep its id across runs for the saved table to be worth
    /// anything: the buckets are cut by distance from that id, so restoring
    /// them under a fresh one drops most of what was saved — and other nodes'
    /// tables go on pointing at the old id, which is the other half of the
    /// waste.
    /// </summary>
    public static NodeId? SavedId(string path)
    {
        try
        {
            if (File.Exists(path)
                && BencodeParser.Parse(File.ReadAllBytes(path)) is BDictionary saved
                && saved.GetByteString("id") is { Length: NodeId.Size } id)
            {
                return new NodeId(id.Span);
            }
        }
        catch (Exception e) when (e is BencodeException or IOException)
        {
            // A saved id that cannot be read is a fresh one.
        }

        return null;
    }

    /// <summary>Reads a saved table back. Anything unreadable is simply skipped.</summary>
    public int Load(string path)
    {
        try
        {
            if (!File.Exists(path)
                || BencodeParser.Parse(File.ReadAllBytes(path)) is not BDictionary saved
                || saved.GetByteString("nodes") is not { } nodes)
            {
                return 0;
            }

            int added = 0;
            foreach (DhtContact contact in DhtContact.ReadCompact(nodes.Span))
            {
                if (Table.Add(contact))
                {
                    added++;
                }
            }

            return added;
        }
        catch (Exception e) when (e is BencodeException or IOException)
        {
            return 0;
        }
    }

    /// <summary>The routers a client with no saved table has to start from.</summary>
    public static IReadOnlyList<string> DefaultRouters =>
    [
        "router.bittorrent.com:6881",
        "dht.transmissionbt.com:6881",
        "router.utorrent.com:6881",
        "dht.libtorrent.org:25401",
    ];

    // --------------------------------------------------------------- helpers

    private async Task SendAsync(KrpcMessage message, IPEndPoint to, CancellationToken cancellationToken)
    {
        try
        {
            await _socket.SendToAsync(message.Encode(), SocketFlags.None, to, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A datagram that cannot be sent is a node that is not there.
        }
    }

    private static byte[] Compact(IReadOnlyList<DhtContact> contacts)
    {
        byte[] bytes = new byte[contacts.Count * DhtContact.CompactSize];

        for (int i = 0; i < contacts.Count; i++)
        {
            contacts[i].WriteTo(bytes.AsSpan(i * DhtContact.CompactSize));
        }

        return bytes;
    }

    private static byte[] CompactPeer(IPEndPoint peer)
    {
        byte[] bytes = new byte[6];
        peer.Address.TryWriteBytes(bytes, out _);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4), (ushort)peer.Port);
        return bytes;
    }

    private static IPEndPoint ReadPeer(ReadOnlySpan<byte> compact) => new(
        new IPAddress(compact[..4]),
        System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(compact[4..6]));

    private static string Key(byte[] transaction) => Convert.ToHexStringLower(transaction);

    private byte[] TokenFor(IPEndPoint node)
    {
        RotateTokens();
        return Token(_tokenSecret, node);
    }

    private bool IsOurToken(ReadOnlySpan<byte> token, IPEndPoint node)
    {
        RotateTokens();

        return token.SequenceEqual(Token(_tokenSecret, node))
            || token.SequenceEqual(Token(_previousTokenSecret, node));
    }

    private void RotateTokens()
    {
        if (DateTime.UtcNow - _tokenRotated < TokenLifetime)
        {
            return;
        }

        _previousTokenSecret = _tokenSecret;
        _tokenSecret = RandomNumberGenerator.GetBytes(16);
        _tokenRotated = DateTime.UtcNow;
    }

    private static byte[] Token(byte[] secret, IPEndPoint node) =>
        SHA1.HashData([.. secret, .. Encoding.ASCII.GetBytes(node.Address.ToString())]).AsSpan(0, 8).ToArray();

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        return ValueTask.CompletedTask;
    }
}
