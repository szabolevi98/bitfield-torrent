using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Peers;

/// <summary>
/// Fetching a torrent's own description from the peers serving it (BEP 9),
/// over the extension protocol (BEP 10).
///
/// This is what makes a magnet link work. The link carries only an infohash;
/// the description that would normally come from a <c>.torrent</c> file is
/// asked of the swarm instead, in 16 KiB pieces, and then checked: its SHA-1
/// has to be the infohash that was asked for. Without that check a peer could
/// hand over a description of something else entirely and the client would
/// download it.
/// </summary>
public static class MetadataExchange
{
    /// <summary>The number this client gives ut_metadata in its own handshake.</summary>
    public const byte OurMetadataExtension = 1;

    private const int MetadataPieceSize = 16 * 1024;

    /// <summary>
    /// A torrent's description is not usually large; a cap keeps a peer from
    /// announcing one that would have this client allocate whatever it likes.
    /// Eight megabytes is far past the largest real torrent.
    /// </summary>
    private const int MaxMetadataSize = 8 * 1024 * 1024;

    private const int RequestType = 0;
    private const int DataType = 1;
    private const int RejectType = 2;

    /// <summary>The extension handshake this client sends.</summary>
    public static PeerMessage Handshake(int port, int? metadataSize = null)
    {
        List<(string Key, BValue Value)> entries =
        [
            ("m", Dictionary(("ut_metadata", new BInteger(OurMetadataExtension)))),
            ("p", new BInteger(port)),
            ("reqq", new BInteger(250)),
            ("v", new BString("Bitfield 1.0.1")),
        ];

        if (metadataSize is { } size)
        {
            entries.Add(("metadata_size", new BInteger(size)));
        }

        return PeerMessage.Extended(0, BencodeWriter.Encode(Dictionary([.. entries])));
    }

    /// <summary>What a peer said it can do, read from its extension handshake.</summary>
    public sealed record Support(byte MetadataExtension, int MetadataSize)
    {
        public bool HasMetadata => MetadataExtension != 0 && MetadataSize > 0;
    }

    public static Support? ReadHandshake(ReadOnlyMemory<byte> payload)
    {
        if (!BencodeParser.TryParse(payload, out BValue? value, out _) || value is not BDictionary handshake)
        {
            return null;
        }

        long extension = handshake.GetDictionary("m")?.GetInteger("ut_metadata") ?? 0;
        long size = handshake.GetInteger("metadata_size") ?? 0;

        if (extension is <= 0 or > byte.MaxValue || size is < 0 or > MaxMetadataSize)
        {
            return new Support(0, 0);
        }

        return new Support((byte)extension, (int)size);
    }

    /// <summary>
    /// Asks one peer for the whole description and checks it. Returns the raw
    /// info dictionary, or null if this peer could not or would not provide it.
    ///
    /// The bytes come back exactly as the peer sent them, because that is what
    /// the infohash is taken over — the same reason the parser keeps the range
    /// of an info dictionary read from a file.
    /// </summary>
    public static async Task<byte[]?> FetchAsync(
        PeerConnection connection,
        InfoHash infoHash,
        int port,
        CancellationToken cancellationToken)
    {
        if (!connection.RemoteReserved.SupportsExtensionProtocol)
        {
            return null;
        }

        await connection.SendAsync(Handshake(port), cancellationToken).ConfigureAwait(false);

        Support? support = null;
        byte[]? metadata = null;
        bool[]? received = null;
        int outstanding = 0;

        while (true)
        {
            PeerMessage message = await connection.ReceiveAsync(cancellationToken).ConfigureAwait(false);

            if (message.Id != MessageId.Extended)
            {
                continue;
            }

            (byte extension, ReadOnlyMemory<byte> payload) = message.ReadExtended();

            if (extension == 0)
            {
                support = ReadHandshake(payload);
                if (support is not { HasMetadata: true })
                {
                    return null;
                }

                metadata = new byte[support.MetadataSize];
                received = new bool[(support.MetadataSize + MetadataPieceSize - 1) / MetadataPieceSize];

                for (int piece = 0; piece < received.Length; piece++)
                {
                    await connection.SendAsync(
                        PeerMessage.Extended(support.MetadataExtension, Request(piece)),
                        cancellationToken).ConfigureAwait(false);
                    outstanding++;
                }

                continue;
            }

            // Anything else is a message for an extension this client offered,
            // and ut_metadata is the only one it does.
            if (extension != OurMetadataExtension || support == null || metadata == null || received == null)
            {
                continue;
            }

            if (!TryReadPiece(payload, out int index, out ReadOnlyMemory<byte> data, out bool rejected))
            {
                continue;
            }

            if (rejected)
            {
                // A peer entitled to say no. Another one will not.
                return null;
            }

            if ((uint)index >= (uint)received.Length || received[index])
            {
                continue;
            }

            int begin = index * MetadataPieceSize;
            int expected = Math.Min(MetadataPieceSize, metadata.Length - begin);
            if (data.Length != expected)
            {
                continue;
            }

            data.Span.CopyTo(metadata.AsSpan(begin));
            received[index] = true;
            outstanding--;

            if (outstanding > 0 || received.Any(piece => !piece))
            {
                continue;
            }

            // The whole point of the exercise: the description has to be the
            // one the infohash named, or it is a description of something else.
            return InfoHash.ComputeSha1(metadata) == infoHash ? metadata : null;
        }
    }

    /// <summary>
    /// Reads a peer's request for one piece of the description. Returns false
    /// for anything that is not a request, including the data and reject
    /// messages this client sends itself.
    /// </summary>
    public static bool TryReadRequest(ReadOnlyMemory<byte> payload, out int piece)
    {
        piece = -1;

        if (!TryParsePrefix(payload, out BDictionary? header, out _))
        {
            return false;
        }

        long index = header!.GetInteger("piece") ?? -1;
        if (header.GetInteger("msg_type") != RequestType || index is < 0 or > int.MaxValue)
        {
            return false;
        }

        piece = (int)index;
        return true;
    }

    /// <summary>
    /// One piece of the description: a bencoded header with the bytes appended
    /// after it. Returns the reject message instead for a piece that does not
    /// exist, which is the answer a peer is entitled to and expects.
    /// </summary>
    public static byte[] DataMessage(int piece, ReadOnlyMemory<byte> metadata)
    {
        int begin = piece * MetadataPieceSize;
        if (piece < 0 || begin >= metadata.Length)
        {
            return BencodeWriter.Encode(Dictionary(
                ("msg_type", new BInteger(RejectType)),
                ("piece", new BInteger(piece))));
        }

        int length = Math.Min(MetadataPieceSize, metadata.Length - begin);

        byte[] header = BencodeWriter.Encode(Dictionary(
            ("msg_type", new BInteger(DataType)),
            ("piece", new BInteger(piece)),
            ("total_size", new BInteger(metadata.Length))));

        byte[] message = new byte[header.Length + length];
        header.CopyTo(message, 0);
        metadata.Span.Slice(begin, length).CopyTo(message.AsSpan(header.Length));
        return message;
    }

    private static byte[] Request(int piece) => BencodeWriter.Encode(Dictionary(
        ("msg_type", new BInteger(RequestType)),
        ("piece", new BInteger(piece))));

    /// <summary>
    /// A data message is a bencoded dictionary with the piece's bytes appended
    /// after it, so the dictionary's own length is what says where the bytes
    /// begin. The parser records that, which is why it can be read back here.
    /// </summary>
    private static bool TryReadPiece(
        ReadOnlyMemory<byte> payload,
        out int index,
        out ReadOnlyMemory<byte> data,
        out bool rejected)
    {
        index = -1;
        data = default;
        rejected = false;

        if (!TryParsePrefix(payload, out BDictionary? header, out int headerLength))
        {
            return false;
        }

        long type = header!.GetInteger("msg_type") ?? -1;
        long piece = header.GetInteger("piece") ?? -1;

        if (piece is < 0 or > int.MaxValue)
        {
            return false;
        }

        index = (int)piece;

        if (type == RejectType)
        {
            rejected = true;
            return true;
        }

        if (type != DataType)
        {
            return false;
        }

        data = payload[headerLength..];
        return true;
    }

    private static bool TryParsePrefix(ReadOnlyMemory<byte> payload, out BDictionary? header, out int length)
    {
        header = null;
        length = 0;

        // The dictionary is a handful of bytes and the piece after it can be
        // sixteen kilobytes, so the search for where one ends and the other
        // begins is bounded rather than run over the whole payload.
        int limit = Math.Min(payload.Length, 256);

        for (int end = 2; end <= limit; end++)
        {
            if (payload.Span[end - 1] != (byte)'e')
            {
                continue;
            }

            if (BencodeParser.TryParse(payload[..end], out BValue? value, out _) && value is BDictionary dictionary)
            {
                header = dictionary;
                length = end;
                return true;
            }
        }

        return false;
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(
                Encoding.ASCII.GetBytes(entry.Key), entry.Value))]);
}
