using System.Security.Cryptography;
using System.Text;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// The peer wire protocol: the handshake, the messages, and the bookkeeping
/// that decides whether anything can be asked for. Everything here is settled
/// on byte arrays, so the checks say what goes on the wire rather than what a
/// peer happened to accept on the day.
/// </summary>
internal static class PeerProtocolTests
{
    private const string DebianInfoHash = "7acf8fb590b2060dd9c3146ef770169d593433b0";

    public static void Run(Action<string, bool, string> check)
    {
        Handshakes(check);
        Messages(check);
        Bitfields(check);
        Assembly(check);
        States(check);
    }

    private static void Handshakes(Action<string, bool, string> check)
    {
        PeerHandshake handshake = new()
        {
            InfoHash = InfoHash.Parse(DebianInfoHash),
            PeerId = Id("-BF1000-abcdefghijkl"),
            Reserved = ReservedBits.Ours,
        };

        byte[] bytes = handshake.ToArray();

        check("handshake: sixty-eight bytes", bytes.Length == 68, $"{bytes.Length}");
        check("handshake: length-prefixed protocol name",
            bytes[0] == 19 && Encoding.ASCII.GetString(bytes, 1, 19) == "BitTorrent protocol",
            Encoding.ASCII.GetString(bytes, 1, 19));
        check("handshake: infohash in place",
            Convert.ToHexStringLower(bytes.AsSpan(28, 20)) == DebianInfoHash,
            Convert.ToHexStringLower(bytes.AsSpan(28, 20)));
        check("handshake: peer id in place",
            Encoding.ASCII.GetString(bytes, 48, 20) == "-BF1000-abcdefghijkl", "");

        PeerHandshake parsed = PeerHandshake.Parse(bytes);
        check("handshake: reads back what it wrote",
            parsed.InfoHash == handshake.InfoHash && parsed.PeerId == handshake.PeerId, "");

        // The reserved bytes are how peers find out what else they can say to
        // each other.
        check("handshake: this client advertises the extension protocol and DHT",
            parsed.Reserved is { SupportsExtensionProtocol: true, SupportsDht: true }, "");
        check("handshake: and not the fast extension, which is not implemented",
            !parsed.Reserved.SupportsFastExtension, "");
        check("handshake: reserved bits read from a peer",
            new ReservedBits([0, 0, 0, 0, 0, 0x10, 0, 0x05]) is
                { SupportsExtensionProtocol: true, SupportsDht: true, SupportsFastExtension: true }, "");
        check("handshake: a peer advertising nothing",
            new ReservedBits(new byte[8]).ToString() == "none", "");

        check("handshake: a wrong protocol length is refused",
            Refuses(() => PeerHandshake.Parse(Corrupt(bytes, 0, 20))), "");
        check("handshake: a wrong protocol name is refused",
            Refuses(() => PeerHandshake.Parse(Corrupt(bytes, 5, (byte)'X'))), "");
        check("handshake: a short handshake is refused",
            Refuses(() => PeerHandshake.Parse(bytes.AsSpan(0, 67))), "");
    }

    private static void Messages(Action<string, bool, string> check)
    {
        // A keep-alive is a length of zero and nothing else. It is what tells a
        // peer that a connection with nothing to say is still alive.
        check("message: a keep-alive is four zero bytes",
            PeerMessage.KeepAlive.ToArray() is [0, 0, 0, 0], "");
        check("message: and reads back as one",
            PeerMessage.FromFrame(ReadOnlyMemory<byte>.Empty).IsKeepAlive, "");

        check("message: unchoke is one byte of payloadless message",
            PeerMessage.Unchoke.ToArray() is [0, 0, 0, 1, 1], "");
        check("message: interested",
            PeerMessage.Interested.ToArray() is [0, 0, 0, 1, 2], "");

        check("message: have carries a piece index",
            PeerMessage.Have(1).ToArray() is [0, 0, 0, 5, 4, 0, 0, 0, 1], "");
        check("message: have reads back", RoundTrip(PeerMessage.Have(3_023)).ReadHave() == 3_023, "");

        BlockRequest block = new(Piece: 2, Begin: 16_384, Length: 16_384);
        check("message: request carries three integers",
            PeerMessage.Request(block).ToArray() is
                [0, 0, 0, 13, 6, 0, 0, 0, 2, 0, 0, 0x40, 0, 0, 0, 0x40, 0], "");
        check("message: request reads back", RoundTrip(PeerMessage.Request(block)).ReadRequest() == block, "");
        check("message: cancel reads back as the same block",
            RoundTrip(PeerMessage.Cancel(block)).ReadRequest() == block, "");

        byte[] payload = [1, 2, 3, 4];
        PeerMessage piece = RoundTrip(PeerMessage.Piece(7, 32_768, payload));
        (int index, int begin, ReadOnlyMemory<byte> data) = piece.ReadPiece();
        check("message: a block says which piece and how far in",
            index == 7 && begin == 32_768 && data.Span.SequenceEqual(payload), $"{index}/{begin}");

        check("message: port reads back", RoundTrip(PeerMessage.Port(6881)).ReadPort() == 6881, "");

        check("message: a bitfield carries its bytes",
            RoundTrip(PeerMessage.Bitfield(new byte[] { 0xFF, 0x80 })).Payload.Length == 2, "");

        // Reading a message as the wrong kind, or one whose payload is the
        // wrong size, is a peer to drop rather than a value to guess at.
        check("message: reading the wrong kind is refused",
            Refuses(() => PeerMessage.Unchoke.ReadHave()), "");
        check("message: a have of the wrong length is refused",
            Refuses(() => PeerMessage.FromFrame(new byte[] { 4, 0, 0 }).ReadHave()), "");
        check("message: a request of the wrong length is refused",
            Refuses(() => PeerMessage.FromFrame(new byte[] { 6, 0, 0, 0, 1 }).ReadRequest()), "");
        check("message: a block with no header is refused",
            Refuses(() => PeerMessage.FromFrame(new byte[] { 7, 0, 0 }).ReadPiece()), "");

        // A message this client does not know is carried rather than treated as
        // a protocol violation: the protocol has grown, and peers speak later
        // dialects than this one.
        PeerMessage unknown = PeerMessage.FromFrame(new byte[] { 200, 1, 2, 3 });
        check("message: an unknown id is carried, not rejected",
            unknown.Id == (MessageId)200 && unknown.Payload.Length == 3, "");
    }

    private static void Bitfields(Action<string, bool, string> check)
    {
        PieceBitfield bitfield = new(12);

        check("bitfield: starts empty", bitfield is { IsEmpty: true, SetCount: 0 }, "");
        check("bitfield: twelve pieces take two bytes", PieceBitfield.ByteCount(12) == 2, "");

        bitfield.Set(0);
        bitfield.Set(11);

        check("bitfield: the first piece is the top bit of the first byte",
            bitfield.ToArray()[0] == 0x80, $"0x{bitfield.ToArray()[0]:X2}");
        check("bitfield: the twelfth piece is the fourth bit of the second byte",
            bitfield.ToArray()[1] == 0x10, $"0x{bitfield.ToArray()[1]:X2}");
        check("bitfield: reads back what was set",
            bitfield[0] && bitfield[11] && !bitfield[5], "");
        check("bitfield: counts what is held", bitfield.SetCount == 2, $"{bitfield.SetCount}");
        check("bitfield: setting twice does not count twice",
            Repeat(bitfield, 0).SetCount == 2, "");
        check("bitfield: asking past the end is refused",
            Refuses(() => _ = bitfield[12]), "");

        check("bitfield: a complete one says so",
            PieceBitfield.Complete(12) is { IsComplete: true, SetCount: 12 }, "");
        check("bitfield: a seed's bitfield has its spare bits clear",
            PieceBitfield.Complete(12).ToArray() is [0xFF, 0xF0], "");

        check("bitfield: read from a peer",
            PieceBitfield.FromBytes([0xFF, 0xF0], 12) is { SetCount: 12 }, "");

        // The two ways a peer's bitfield can be wrong, both of which the
        // specification says to drop the peer over.
        check("bitfield: one of the wrong length is refused",
            Refuses(() => PieceBitfield.FromBytes([0xFF], 12)), "");
        check("bitfield: spare bits claiming pieces that do not exist are refused",
            Refuses(() => PieceBitfield.FromBytes([0xFF, 0xF1], 12)), "");
        check("bitfield: an exact multiple of eight has no spare bits to check",
            PieceBitfield.FromBytes([0xFF], 8) is { SetCount: 8 }, "");
    }

    private static void Assembly(Action<string, bool, string> check)
    {
        // A piece of 40,000 bytes is two full blocks and a short one.
        byte[] content = new byte[40_000];
        Random.Shared.NextBytes(content);
        byte[] hash = SHA1.HashData(content);

        PieceAssembler assembler = new(0, content.Length, hash);

        check("assembly: block count", assembler.BlockCount == 3, $"{assembler.BlockCount}");
        check("assembly: full blocks are 16 KiB",
            assembler.BlockAt(0).Length == 16_384 && assembler.BlockAt(1).Length == 16_384, "");
        check("assembly: the last block is short",
            assembler.BlockAt(2).Length == 40_000 - (2 * 16_384), $"{assembler.BlockAt(2).Length}");
        check("assembly: every block is listed as missing",
            assembler.MissingBlocks().Count() == 3, "");

        // Blocks arrive in whatever order peers answer in.
        check("assembly: takes the last block first",
            assembler.Add(32_768, content.AsSpan(32_768)), "");
        check("assembly: an incomplete piece does not verify", !assembler.Verify(), "");
        check("assembly: the received block is no longer missing",
            assembler.MissingBlocks().Select(b => b.Begin).ToArray() is [0, 16_384],
            string.Join(", ", assembler.MissingBlocks().Select(b => b.Begin)));

        check("assembly: the same block twice is refused",
            !assembler.Add(32_768, content.AsSpan(32_768)), "");
        check("assembly: a block at an offset that is not a block boundary is refused",
            !assembler.Add(100, content.AsSpan(100, 16_384)), "");
        check("assembly: a block past the end is refused",
            !assembler.Add(49_152, content.AsSpan(0, 16)), "");
        check("assembly: a block of the wrong length is refused",
            !assembler.Add(0, content.AsSpan(0, 1_000)), "");

        assembler.Add(0, content.AsSpan(0, 16_384));
        assembler.Add(16_384, content.AsSpan(16_384, 16_384));

        check("assembly: complete once every block has arrived", assembler.IsComplete, "");
        check("assembly: verifies against the torrent's hash", assembler.Verify(), "");
        check("assembly: the assembled bytes are the content",
            assembler.Data.Span.SequenceEqual(content), "");

        // The point of the whole exercise: a peer that sends plausible bytes
        // that are not the right ones gets caught here and nowhere else.
        PieceAssembler tampered = new(0, content.Length, hash);
        byte[] corrupted = content.ToArray();
        corrupted[20_000] ^= 0xFF;
        tampered.Add(0, corrupted.AsSpan(0, 16_384));
        tampered.Add(16_384, corrupted.AsSpan(16_384, 16_384));
        tampered.Add(32_768, corrupted.AsSpan(32_768));

        check("assembly: one flipped byte fails the hash", tampered.IsComplete && !tampered.Verify(), "");

        tampered.Reset();
        check("assembly: a failed piece can be started over",
            tampered is { ReceivedBlocks: 0, IsComplete: false }
            && tampered.MissingBlocks().Count() == 3, "");

        // A piece smaller than one block still has one.
        PieceAssembler small = new(9, 100, SHA1.HashData(new byte[100]));
        check("assembly: a piece shorter than a block is one short block",
            small.BlockCount == 1 && small.BlockAt(0).Length == 100, "");
        small.Add(0, new byte[100]);
        check("assembly: and verifies", small.Verify(), "");
    }

    private static void States(Action<string, bool, string> check)
    {
        PeerState state = new(12);

        // Nothing can be asked for until this client says it wants something
        // and the peer agrees to answer.
        check("state: a new connection is choked and uninterested",
            state is { ChokedByPeer: true, ChokingPeer: true, InterestedInPeer: false, PeerInterested: false }, "");
        check("state: and cannot request anything", !state.CanRequest, "");

        state.Apply(PeerMessage.Unchoke);
        check("state: unchoke is taken", !state.ChokedByPeer, "");
        check("state: but interest is still needed", !state.CanRequest, "");

        state.SetInterested(true);
        check("state: interested and unchoked can request", state.CanRequest, "");

        state.Apply(PeerMessage.Choke);
        check("state: choke stops it again", !state.CanRequest, "");

        state.Apply(PeerMessage.Interested);
        check("state: the peer's own interest is tracked", state.PeerInterested, "");
        state.Apply(PeerMessage.NotInterested);
        check("state: and withdrawn", !state.PeerInterested, "");

        state.Apply(PeerMessage.Have(4));
        check("state: have adds to what the peer holds",
            state.Available[4] && state.Available.SetCount == 1, "");

        check("state: a block is not state and is handed back to the caller",
            !state.Apply(PeerMessage.Piece(0, 0, [1, 2, 3])), "");

        check("state: a have for a piece the torrent does not have is refused",
            Refuses(() => new PeerState(12).Apply(PeerMessage.Have(12))), "");

        PeerState fresh = new(12);
        fresh.Apply(PeerMessage.Bitfield(new byte[] { 0xFF, 0xF0 }));
        check("state: a bitfield fills in everything the peer holds at once",
            fresh.Available.IsComplete, "");

        // A bitfield is only meaningful as the opening message.
        PeerState late = new(12);
        late.Apply(PeerMessage.Have(0));
        check("state: a bitfield arriving after a have is refused",
            Refuses(() => late.Apply(PeerMessage.Bitfield(new byte[] { 0xFF, 0xF0 }))), "");
    }

    // ------------------------------------------------------------- helpers

    private static PeerId Id(string text) => new(Encoding.ASCII.GetBytes(text));

    /// <summary>Sends a message through its own frame format and back.</summary>
    private static PeerMessage RoundTrip(PeerMessage message)
    {
        byte[] frame = message.ToArray();
        return PeerMessage.FromFrame(frame.AsMemory(4));
    }

    private static byte[] Corrupt(byte[] bytes, int offset, byte value)
    {
        byte[] copy = bytes.ToArray();
        copy[offset] = value;
        return copy;
    }

    private static PieceBitfield Repeat(PieceBitfield bitfield, int piece)
    {
        bitfield.Set(piece);
        return bitfield;
    }

    private static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (Exception e) when (e is PeerProtocolException or ArgumentException)
        {
            return true;
        }
    }
}
