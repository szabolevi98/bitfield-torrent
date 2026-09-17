using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// File priorities, and the awkward fact underneath them: pieces and files do
/// not line up.
///
/// A piece routinely spans two or three files, so a piece belonging to a file
/// nobody wants and one somebody does is wanted — the part that matters cannot
/// be had without it. This is also where "complete" stops meaning "every
/// piece", which is the change with the longest reach in the whole client.
/// </summary>
internal static class PriorityTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Mapping(check);
        Picking(check);
        Completion(check);
    }

    private static void Mapping(Action<string, bool, string> check)
    {
        // Three files in 16 KiB pieces: 20,000 + 20,000 + 20,000 = 60,000
        // bytes, four pieces. Nothing lines up with anything.
        TestTorrents.Built built = TestTorrents.Build("split",
            [("one.bin", 20_000, false), ("two.bin", 20_000, false), ("three.bin", 20_000, false)]);

        Metainfo torrent = built.Torrent;
        check("priority: the torrent is four pieces over three files",
            torrent.PieceCount == 4, $"{torrent.PieceCount}");

        // Everything normal to begin with.
        PiecePriorities all = new(torrent);
        check("priority: by default every piece is wanted",
            all.WantsEverything && all.WantedCount == 4, $"{all.WantedCount}");
        check("priority: and every byte counts towards progress",
            all.WantedBytes == 60_000, $"{all.WantedBytes}");

        // Only the middle file. It starts at 20,000 and ends at 40,000, so it
        // touches pieces 1 and 2 — and both of those also hold bytes of files
        // nobody asked for, which is exactly the point.
        PiecePriorities middle = new(torrent,
            [FilePriority.Skip, FilePriority.Normal, FilePriority.Skip]);

        check("priority: a skipped file's own pieces are not wanted",
            !middle.Wanted(0) && !middle.Wanted(3), "");
        check("priority: but the pieces it shares with a wanted file are",
            middle.Wanted(1) && middle.Wanted(2), "");
        check("priority: so two of four pieces are wanted",
            middle.WantedCount == 2, $"{middle.WantedCount}");
        check("priority: and progress is out of the wanted file's bytes",
            middle.WantedBytes == 20_000, $"{middle.WantedBytes}");

        check("priority: a file that shares pieces says so",
            middle.SharesPieces(1), "");

        // A piece touched by a high file and a low one takes the higher: the
        // file that wants it most decides when it arrives.
        PiecePriorities mixed = new(torrent,
            [FilePriority.Low, FilePriority.High, FilePriority.Normal]);

        check("priority: a shared piece takes the highest of its files",
            mixed.PriorityOf(1) == FilePriority.High, $"{mixed.PriorityOf(1)}");
        check("priority: and a piece inside one file takes that file's",
            mixed.PriorityOf(0) == FilePriority.Low, $"{mixed.PriorityOf(0)}");

        check("priority: a priority list of the wrong length is refused",
            Refuses(() => new PiecePriorities(torrent, [FilePriority.Normal])), "");
    }

    private static void Picking(Action<string, bool, string> check)
    {
        TestTorrents.Built built = TestTorrents.Build("pick",
            [("skip.bin", 16 * 1024 * 4, false), ("want.bin", 16 * 1024 * 4, false)]);

        Metainfo torrent = built.Torrent;
        PiecePriorities priorities = new(torrent, [FilePriority.Skip, FilePriority.Normal]);

        PiecePicker picker = new(new PieceBitfield(torrent.PieceCount), new Random(1), priorities);
        PieceBitfield everything = PieceBitfield.Complete(torrent.PieceCount);

        // Whatever it hands out, a hundred times over, must be wanted.
        bool allWanted = true;
        for (int i = 0; i < 100; i++)
        {
            int? piece = picker.Take(everything);
            if (piece == null)
            {
                break;
            }

            allWanted &= priorities.Wanted(piece.Value);
            picker.Release(piece.Value);
        }

        check("picking: a skipped piece is never handed out", allWanted, "");

        // High before low, even when the low one is rarer — which is the one
        // case where priority has to beat rarest first.
        TestTorrents.Built two = TestTorrents.Build("order",
            [("low.bin", 16 * 1024 * 3, false), ("high.bin", 16 * 1024 * 3, false)]);

        PiecePriorities ordered = new(two.Torrent, [FilePriority.Low, FilePriority.High]);
        PieceBitfield held = new(two.Torrent.PieceCount);
        for (int piece = 0; piece < 4; piece++)
        {
            held.Set(piece);
        }

        PiecePicker byPriority = new(held, new Random(1), ordered);
        PieceBitfield available = PieceBitfield.Complete(two.Torrent.PieceCount);

        // Make the low-priority pieces the rarest, so rarity alone would pick
        // them.
        byPriority.AddAvailability(available);
        byPriority.AddAvailability(available);

        int? chosen = byPriority.Take(available);
        check("picking: priority beats rarity",
            chosen != null && ordered.PriorityOf(chosen.Value) == FilePriority.High,
            chosen == null ? "nothing" : $"{ordered.PriorityOf(chosen.Value)}");
    }

    private static void Completion(Action<string, bool, string> check)
    {
        TestTorrents.Built built = TestTorrents.Build("done",
            [("skip.bin", 16 * 1024 * 3, false), ("want.bin", 16 * 1024 * 3, false)]);

        Metainfo torrent = built.Torrent;
        PiecePriorities priorities = new(torrent, [FilePriority.Skip, FilePriority.Normal]);

        PieceBitfield have = new(torrent.PieceCount);
        PiecePicker picker = new(have, new Random(1), priorities);

        check("completion: nothing held is not complete", !picker.IsComplete, "");
        check("completion: the count is of wanted pieces only",
            picker.WantedCount == priorities.WantedCount && picker.WantedCount < torrent.PieceCount,
            $"{picker.WantedCount} of {torrent.PieceCount}");

        // Hold every wanted piece and nothing else.
        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            if (priorities.Wanted(piece))
            {
                picker.Completed(piece);
            }
        }

        check("completion: holding every wanted piece is complete", picker.IsComplete, "");
        check("completion: even though pieces are still missing",
            !have.IsComplete, "the torrent turned out to be fully held after all");
        check("completion: and nothing is left to fetch", picker.Remaining == 0, $"{picker.Remaining}");

        // Asking for the skipped file afterwards makes it incomplete again,
        // which is what the user asking for it means.
        picker.Priorities = new PiecePriorities(torrent, [FilePriority.Normal, FilePriority.Normal]);

        check("completion: wanting the rest makes it unfinished again",
            !picker.IsComplete && picker.Remaining > 0, $"{picker.Remaining} left");
    }

    private static bool Refuses(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
