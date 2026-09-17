using Bitfield.Core.Download;
using Bitfield.Core.Peers;

namespace Bitfield.Tests;

/// <summary>
/// Handing pieces out to peers. The job is mostly about not handing the same
/// one to two peers, which wastes both connections on bytes that will arrive
/// twice — except at the very end, where doing exactly that is what stops a
/// finished download waiting on one slow peer.
/// </summary>
internal static class PickerTests
{
    public static void Run(Action<string, bool, string> check)
    {
        PiecePicker picker = new(new PieceBitfield(20));
        PieceBitfield everything = PieceBitfield.Complete(20);

        check("picker: starts wanting everything",
            picker is { Remaining: 20, IsComplete: false }, $"{picker.Remaining}");

        check("picker: hands out the first piece a peer holds", picker.Take(everything) == 0, "");
        check("picker: and not the same one again", picker.Take(everything) == 1, "");

        // A peer holding only part of the torrent gets what it actually has.
        PieceBitfield partial = new(20);
        partial.Set(9);
        partial.Set(15);
        check("picker: a peer is only asked for what it holds", picker.Take(partial) == 9, "");

        picker.Release(9);
        check("picker: a released piece is handed out again", picker.Take(partial) == 9, "");

        picker.Completed(9);
        check("picker: a completed piece is held", picker.Have[9], "");
        check("picker: and is not handed out any more", picker.Take(partial) == 15, "");
        check("picker: the count comes down", picker.Remaining == 19, $"{picker.Remaining}");

        // A peer with nothing this client wants is told nothing rather than
        // given a piece it cannot serve.
        PieceBitfield only9 = new(20);
        only9.Set(9);
        check("picker: a peer with nothing wanted gets nothing", picker.Take(only9) == null, "");

        // With almost everything done, the same piece goes to several peers at
        // once so that one slow connection cannot hold up the finish.
        PiecePicker ending = new(new PieceBitfield(20));
        for (int piece = 0; piece < 17; piece++)
        {
            ending.Completed(piece);
        }

        check("picker: near the end the same piece goes to more than one peer",
            ending.Take(everything) == 17 && ending.Take(everything) == 17, "");

        check("picker: which does not happen while there is plenty left",
            picker.Take(everything) != picker.Take(everything), "");

        // Completing everything ends the download.
        PiecePicker small = new(new PieceBitfield(2));
        small.Completed(0);
        small.Completed(1);
        check("picker: complete when every piece is held", small is { IsComplete: true, Remaining: 0 }, "");
    }
}
