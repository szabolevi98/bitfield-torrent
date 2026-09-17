using Bitfield.Core.Download;
using Bitfield.Core.Peers;

namespace Bitfield.Tests;

/// <summary>
/// Handing pieces out to peers. Two jobs: not giving the same piece to two
/// peers, which spends both connections on bytes that will arrive twice, and
/// asking for the rare pieces before the common ones, so that a download does
/// not end up needing something only one departed peer ever had.
///
/// The randomness is seeded here, so the answers are the same every run.
/// </summary>
internal static class PickerTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Reservations(check);
        Rarity(check);
        Endgame(check);
    }

    private static void Reservations(Action<string, bool, string> check)
    {
        PiecePicker picker = new(new PieceBitfield(20), new Random(1));
        PieceBitfield everything = PieceBitfield.Complete(20);

        check("picker: starts wanting everything",
            picker is { Remaining: 20, IsComplete: false }, $"{picker.Remaining}");

        int first = picker.Take(everything) ?? -1;
        int second = picker.Take(everything) ?? -1;

        check("picker: hands out a piece the peer holds", first is >= 0 and < 20, $"{first}");
        check("picker: and not the same one to the next peer", second != first, $"{first} twice");

        // A peer holding only part of the torrent is asked for what it has.
        PieceBitfield partial = new(20);
        partial.Set(9);
        partial.Set(15);

        int fromPartial = picker.Take(partial) ?? -1;
        check("picker: a peer is only asked for what it holds", fromPartial is 9 or 15, $"{fromPartial}");

        // Reserving both leaves nothing for a third peer holding the same two.
        int alsoFromPartial = picker.Take(partial) ?? -1;
        check("picker: both of a peer's pieces can be reserved",
            alsoFromPartial is 9 or 15 && alsoFromPartial != fromPartial, $"{alsoFromPartial}");
        check("picker: and then there is nothing left to reserve",
            picker.Take(partial) == null, "");

        picker.Release(fromPartial);
        check("picker: a released piece is handed out again",
            picker.Take(partial) == fromPartial, "");

        picker.Release(fromPartial);
        picker.Release(alsoFromPartial);

        picker.Completed(9);
        check("picker: a completed piece is held", picker.Have[9], "");
        check("picker: and is not handed out any more", picker.Take(partial) == 15, "");
        check("picker: the count comes down", picker.Remaining == 19, $"{picker.Remaining}");

        // A peer with nothing this client wants is told nothing rather than
        // handed a piece it cannot serve.
        PieceBitfield only9 = new(20);
        only9.Set(9);
        check("picker: a peer with nothing wanted gets nothing", picker.Take(only9) == null, "");
    }

    /// <summary>
    /// Rarest first, and the counting it rests on.
    /// </summary>
    private static void Rarity(Action<string, bool, string> check)
    {
        PieceBitfield everything = PieceBitfield.Complete(10);

        PiecePicker picker = new(Held(10, 4), new Random(1));
        picker.AddAvailability(everything);
        picker.AddAvailability(everything);
        picker.AddAvailability(Only(10, 7));

        check("rarity: a piece every peer holds is counted for each of them",
            picker.AvailabilityOf(5) == 2, $"{picker.AvailabilityOf(5)}");
        check("rarity: and one only some hold is counted for those",
            picker.AvailabilityOf(7) == 3, $"{picker.AvailabilityOf(7)}");

        // Two peers hold everything but piece 8; one more holds only piece 9.
        // Piece 8 is therefore the rarest of the ones still wanted.
        PiecePicker rarestFirst = new(Held(10, 4), new Random(1));
        PieceBitfield allButEight = PieceBitfield.Complete(10);
        PieceBitfield onlyNine = Only(10, 9);

        rarestFirst.AddAvailability(allButEight);
        rarestFirst.AddAvailability(onlyNine);
        rarestFirst.AddAvailability(onlyNine);
        rarestFirst.AddAvailability(onlyNine);

        check("rarity: the rarest piece still wanted goes first",
            rarestFirst.Take(everything) is 4 or 5 or 6 or 7 or 8,
            $"{rarestFirst.AvailabilityOf(8)} vs {rarestFirst.AvailabilityOf(9)}");
        check("rarity: and not the one three extra peers hold",
            rarestFirst.AvailabilityOf(9) > rarestFirst.AvailabilityOf(8),
            $"{rarestFirst.AvailabilityOf(9)} vs {rarestFirst.AvailabilityOf(8)}");

        // A peer leaving takes its pieces back out of the counts, or a piece
        // stays as common as the day its last holder left.
        PiecePicker leaving = new(Held(10, 4), new Random(1));
        leaving.AddAvailability(everything);
        leaving.AddAvailability(Only(10, 6));

        check("rarity: a piece two peers hold counts twice",
            leaving.AvailabilityOf(6) == 2, $"{leaving.AvailabilityOf(6)}");

        leaving.RemoveAvailability(Only(10, 6));
        check("rarity: and once after one of them leaves",
            leaving.AvailabilityOf(6) == 1, $"{leaving.AvailabilityOf(6)}");

        leaving.RemoveAvailability(everything);
        check("rarity: counts never go below zero",
            leaving.AvailabilityOf(6) == 0 && leaving.AvailabilityOf(0) == 0, "");

        // Before anything is in hand, picking is random rather than rarest
        // first: the counts are worth little that early, and every fresh client
        // agreeing on the same rare piece would pile them all onto one peer.
        HashSet<int> opened = [];
        for (int run = 0; run < 40; run++)
        {
            PiecePicker fresh = new(new PieceBitfield(10), new Random(run));
            fresh.AddAvailability(Only(10, 3));
            fresh.AddAvailability(everything);

            if (fresh.Take(everything) is { } piece)
            {
                opened.Add(piece);
            }
        }

        check("rarity: the opening pieces are not all the same one",
            opened.Count > 1, $"every run picked {string.Join(", ", opened)}");
        check("rarity: and are not all the rarest one either",
            opened.Count > 2, $"only {string.Join(", ", opened)}");
    }

    private static void Endgame(Action<string, bool, string> check)
    {
        PieceBitfield everything = PieceBitfield.Complete(20);

        // With almost everything done, the same piece goes to several peers at
        // once so that one slow connection cannot hold up the finish.
        PiecePicker ending = new(new PieceBitfield(20), new Random(1));
        for (int piece = 0; piece < 17; piece++)
        {
            ending.Completed(piece);
        }

        int once = ending.Take(everything) ?? -1;
        int twice = ending.Take(everything) ?? -1;
        check("endgame: near the end the same piece can go to more than one peer",
            once >= 0 && twice >= 0, $"{once}, {twice}");

        PiecePicker plenty = new(new PieceBitfield(20), new Random(1));
        check("endgame: which does not happen while there is plenty left",
            plenty.Take(everything) != plenty.Take(everything), "");

        PiecePicker small = new(new PieceBitfield(2), new Random(1));
        small.Completed(0);
        small.Completed(1);
        check("endgame: complete when every piece is held",
            small is { IsComplete: true, Remaining: 0 }, "");
    }

    private static PieceBitfield Held(int pieceCount, int howMany)
    {
        PieceBitfield have = new(pieceCount);
        for (int piece = 0; piece < howMany; piece++)
        {
            have.Set(piece);
        }

        return have;
    }

    private static PieceBitfield Only(int pieceCount, int piece)
    {
        PieceBitfield bitfield = new(pieceCount);
        bitfield.Set(piece);
        return bitfield;
    }
}
