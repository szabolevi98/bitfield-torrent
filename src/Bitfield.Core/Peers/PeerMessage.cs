using System.Buffers.Binary;

namespace Bitfield.Core.Peers;

public enum MessageId : byte
{
    Choke = 0,
    Unchoke = 1,
    Interested = 2,
    NotInterested = 3,
    Have = 4,
    Bitfield = 5,
    Request = 6,
    Piece = 7,
    Cancel = 8,

    /// <summary>The sender's DHT port (BEP 5).</summary>
    Port = 9,

    /// <summary>
    /// A message from an extension negotiated by name (BEP 10). Its first
    /// payload byte says which extension, and zero is the negotiation itself.
    /// </summary>
    Extended = 20,
}

/// <summary>
/// One block of one piece: which piece, how far into it, and how much. Peers
/// trade in these rather than whole pieces, because a piece is commonly a
/// quarter of a megabyte and asking for it in one go would leave a connection
/// idle for most of the round trip.
/// </summary>
public readonly record struct BlockRequest(int Piece, int Begin, int Length)
{
    /// <summary>
    /// The block size every client uses. Nothing requires it, but a peer asked
    /// for more than 16 KiB at a time is entitled to refuse, and most do.
    /// </summary>
    public const int BlockSize = 16 * 1024;

    public override string ToString() => $"piece {Piece} at {Begin} for {Length}";
}

/// <summary>
/// A message on a peer connection: a four byte length, and — unless the length
/// is zero, which is the keep-alive — a one byte id and its payload.
/// </summary>
public readonly struct PeerMessage
{
    private PeerMessage(MessageId? id, ReadOnlyMemory<byte> payload)
    {
        Id = id;
        Payload = payload;
    }

    /// <summary>Null for a keep-alive, which carries no id at all.</summary>
    public MessageId? Id { get; }

    public ReadOnlyMemory<byte> Payload { get; }

    public bool IsKeepAlive => Id == null;

    /// <summary>The whole frame, length prefix included.</summary>
    public int FrameLength => 4 + (Id == null ? 0 : 1 + Payload.Length);

    public static PeerMessage KeepAlive => new(null, default);

    public static PeerMessage Choke => new(MessageId.Choke, default);

    public static PeerMessage Unchoke => new(MessageId.Unchoke, default);

    public static PeerMessage Interested => new(MessageId.Interested, default);

    public static PeerMessage NotInterested => new(MessageId.NotInterested, default);

    public static PeerMessage Have(int piece)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        return new PeerMessage(MessageId.Have, payload);
    }

    public static PeerMessage Bitfield(ReadOnlyMemory<byte> bits) => new(MessageId.Bitfield, bits);

    public static PeerMessage Request(BlockRequest block) => Block(MessageId.Request, block);

    public static PeerMessage Cancel(BlockRequest block) => Block(MessageId.Cancel, block);

    public static PeerMessage Piece(int piece, int begin, ReadOnlySpan<byte> block)
    {
        byte[] payload = new byte[8 + block.Length];
        BinaryPrimitives.WriteInt32BigEndian(payload, piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), begin);
        block.CopyTo(payload.AsSpan(8));
        return new PeerMessage(MessageId.Piece, payload);
    }

    /// <summary>
    /// A message belonging to a negotiated extension. The id is the number the
    /// <em>receiving</em> peer asked for in its handshake, not the one this
    /// client uses — each side names its own.
    /// </summary>
    public static PeerMessage Extended(byte extension, ReadOnlySpan<byte> payload)
    {
        byte[] bytes = new byte[1 + payload.Length];
        bytes[0] = extension;
        payload.CopyTo(bytes.AsSpan(1));
        return new PeerMessage(MessageId.Extended, bytes);
    }

    public static PeerMessage Port(int port)
    {
        byte[] payload = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)port);
        return new PeerMessage(MessageId.Port, payload);
    }

    private static PeerMessage Block(MessageId id, BlockRequest block)
    {
        byte[] payload = new byte[12];
        BinaryPrimitives.WriteInt32BigEndian(payload, block.Piece);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), block.Begin);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(8), block.Length);
        return new PeerMessage(id, payload);
    }

    /// <summary>Reads a message from a frame that has had its length prefix removed.</summary>
    public static PeerMessage FromFrame(ReadOnlyMemory<byte> frame)
    {
        if (frame.Length == 0)
        {
            return KeepAlive;
        }

        // An id this client does not know is not an error. The protocol has
        // grown over the years and a peer may be speaking a later dialect, so
        // the message is carried through for a caller that might know it and
        // otherwise ignored.
        return new PeerMessage((MessageId)frame.Span[0], frame[1..]);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < FrameLength)
        {
            throw new ArgumentException($"need {FrameLength} bytes", nameof(destination));
        }

        if (Id == null)
        {
            BinaryPrimitives.WriteInt32BigEndian(destination, 0);
            return;
        }

        BinaryPrimitives.WriteInt32BigEndian(destination, 1 + Payload.Length);
        destination[4] = (byte)Id.Value;
        Payload.Span.CopyTo(destination[5..]);
    }

    public byte[] ToArray()
    {
        byte[] bytes = new byte[FrameLength];
        WriteTo(bytes);
        return bytes;
    }

    public int ReadHave() => ReadInt32(MessageId.Have, 4, 0);

    public (byte Extension, ReadOnlyMemory<byte> Payload) ReadExtended()
    {
        Expect(MessageId.Extended, minimumLength: 1);
        return (Payload.Span[0], Payload[1..]);
    }

    public int ReadPort()
    {
        Expect(MessageId.Port, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(Payload.Span);
    }

    public BlockRequest ReadRequest()
    {
        MessageId id = Id ?? throw new PeerProtocolException("a keep-alive is not a request");
        if (id is not (MessageId.Request or MessageId.Cancel))
        {
            throw new PeerProtocolException($"{id} is not a request");
        }

        if (Payload.Length != 12)
        {
            throw new PeerProtocolException($"a {id} carries 12 bytes, got {Payload.Length}");
        }

        return new BlockRequest(
            BinaryPrimitives.ReadInt32BigEndian(Payload.Span),
            BinaryPrimitives.ReadInt32BigEndian(Payload.Span[4..]),
            BinaryPrimitives.ReadInt32BigEndian(Payload.Span[8..]));
    }

    public (int Piece, int Begin, ReadOnlyMemory<byte> Block) ReadPiece()
    {
        Expect(MessageId.Piece, minimumLength: 8);

        return (
            BinaryPrimitives.ReadInt32BigEndian(Payload.Span),
            BinaryPrimitives.ReadInt32BigEndian(Payload.Span[4..]),
            Payload[8..]);
    }

    private int ReadInt32(MessageId expected, int length, int offset)
    {
        Expect(expected, length);
        return BinaryPrimitives.ReadInt32BigEndian(Payload.Span[offset..]);
    }

    private void Expect(MessageId expected, int length = -1, int minimumLength = -1)
    {
        if (Id != expected)
        {
            throw new PeerProtocolException($"expected {expected}, got {(Id?.ToString() ?? "a keep-alive")}");
        }

        if (length >= 0 && Payload.Length != length)
        {
            throw new PeerProtocolException($"a {expected} carries {length} bytes, got {Payload.Length}");
        }

        if (minimumLength >= 0 && Payload.Length < minimumLength)
        {
            throw new PeerProtocolException(
                $"a {expected} carries at least {minimumLength} bytes, got {Payload.Length}");
        }
    }

    public override string ToString() => Id == null
        ? "keep-alive"
        : Payload.Length == 0 ? Id.ToString()! : $"{Id} ({Payload.Length} bytes)";
}
