using System.Buffers.Binary;
using System.Net;
using System.Text;
using Bitfield.Core.Bencode;

namespace Bitfield.Core.Dht;

/// <summary>One node in the DHT, as a routing table remembers it.</summary>
public sealed record DhtContact(NodeId Id, IPEndPoint EndPoint)
{
    /// <summary>The twenty-six byte form the protocol packs nodes into.</summary>
    public const int CompactSize = 26;

    public void WriteTo(Span<byte> destination)
    {
        Id.WriteTo(destination);
        EndPoint.Address.TryWriteBytes(destination[20..24], out _);
        BinaryPrimitives.WriteUInt16BigEndian(destination[24..26], (ushort)EndPoint.Port);
    }

    public static IReadOnlyList<DhtContact> ReadCompact(ReadOnlySpan<byte> bytes)
    {
        List<DhtContact> contacts = [];

        for (int offset = 0; offset + CompactSize <= bytes.Length; offset += CompactSize)
        {
            NodeId id = new(bytes.Slice(offset, 20));
            IPAddress address = new(bytes.Slice(offset + 20, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(bytes.Slice(offset + 24, 2));

            if (port > 0 && !IPAddress.Any.Equals(address))
            {
                contacts.Add(new DhtContact(id, new IPEndPoint(address, port)));
            }
        }

        return contacts;
    }

    public override string ToString() => $"{Id} at {EndPoint}";
}

/// <summary>What a DHT message is asking or answering.</summary>
public enum KrpcKind
{
    Query,
    Response,
    Error,
}

/// <summary>
/// A KRPC message — the DHT's own protocol: bencoded dictionaries over UDP,
/// four queries and their answers.
///
/// Every message carries a transaction id chosen by whoever asked. UDP has no
/// connection, so that is the only thing tying an answer to its question, and
/// an answer arriving with an id nobody is waiting for is either somebody
/// else's or somebody's attempt to be mistaken for one.
/// </summary>
public sealed record KrpcMessage
{
    public required KrpcKind Kind { get; init; }

    public required byte[] TransactionId { get; init; }

    /// <summary>The query's name — <c>ping</c>, <c>find_node</c> and so on.</summary>
    public string? Query { get; init; }

    /// <summary>The arguments of a query, or the values of a response.</summary>
    public BDictionary? Body { get; init; }

    public int ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>The sender's own node id, which every message carries.</summary>
    public NodeId? SenderId =>
        Body?.GetByteString("id") is { Length: NodeId.Size } id ? new NodeId(id.Span) : null;

    public static KrpcMessage MakeQuery(byte[] transactionId, string query, params (string Key, BValue Value)[] arguments) =>
        new()
        {
            Kind = KrpcKind.Query,
            TransactionId = transactionId,
            Query = query,
            Body = Dictionary(arguments),
        };

    public static KrpcMessage MakeResponse(byte[] transactionId, params (string Key, BValue Value)[] values) =>
        new()
        {
            Kind = KrpcKind.Response,
            TransactionId = transactionId,
            Body = Dictionary(values),
        };

    public static KrpcMessage MakeError(byte[] transactionId, int code, string message) =>
        new()
        {
            Kind = KrpcKind.Error,
            TransactionId = transactionId,
            ErrorCode = code,
            ErrorMessage = message,
        };

    public byte[] Encode()
    {
        List<(string Key, BValue Value)> entries = [("t", new BString(TransactionId))];

        switch (Kind)
        {
            case KrpcKind.Query:
                entries.Add(("y", new BString("q")));
                entries.Add(("q", new BString(Query!)));
                entries.Add(("a", Body ?? Dictionary()));
                break;

            case KrpcKind.Response:
                entries.Add(("y", new BString("r")));
                entries.Add(("r", Body ?? Dictionary()));
                break;

            case KrpcKind.Error:
                entries.Add(("y", new BString("e")));
                entries.Add(("e", new BList([new BInteger(ErrorCode), new BString(ErrorMessage ?? "")])));
                break;
        }

        return BencodeWriter.Encode(Dictionary([.. entries]));
    }

    public static bool TryParse(ReadOnlyMemory<byte> datagram, out KrpcMessage? message)
    {
        message = null;

        if (!BencodeParser.TryParse(datagram, out BValue? value, out _)
            || value is not BDictionary root
            || root.GetByteString("t") is not { } transaction
            || root.GetString("y") is not { } kind)
        {
            return false;
        }

        switch (kind)
        {
            case "q" when root.GetString("q") is { } query:
                message = new KrpcMessage
                {
                    Kind = KrpcKind.Query,
                    TransactionId = transaction.Bytes.ToArray(),
                    Query = query,
                    Body = root.GetDictionary("a"),
                };
                return true;

            case "r":
                message = new KrpcMessage
                {
                    Kind = KrpcKind.Response,
                    TransactionId = transaction.Bytes.ToArray(),
                    Body = root.GetDictionary("r"),
                };
                return true;

            case "e" when root.GetList("e") is { Count: >= 2 } error:
                message = new KrpcMessage
                {
                    Kind = KrpcKind.Error,
                    TransactionId = transaction.Bytes.ToArray(),
                    ErrorCode = error[0] is BInteger code ? (int)code.Value : 0,
                    ErrorMessage = (error[1] as BString)?.Text ?? "",
                };
                return true;

            default:
                return false;
        }
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(
                Encoding.ASCII.GetBytes(entry.Key), entry.Value))]);

    public override string ToString() => Kind switch
    {
        KrpcKind.Query => $"{Query} query",
        KrpcKind.Response => "response",
        _ => $"error {ErrorCode}: {ErrorMessage}",
    };
}
