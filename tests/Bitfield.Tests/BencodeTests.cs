using System.Text;
using Bitfield.Core.Bencode;

namespace Bitfield.Tests;

/// <summary>
/// The bencode reader and writer. Most of these are about what the parser
/// refuses: the format allows exactly one spelling of every value, and a reader
/// that accepts a second spelling will eventually read a torrent differently
/// from the client on the other end of the connection.
/// </summary>
internal static class BencodeTests
{
    public static void Run(Action<string, bool, string> check)
    {
        Integers(check);
        Strings(check);
        Lists(check);
        Dictionaries(check);
        SourceRanges(check);
        RoundTrips(check);
        Limits(check);
    }

    private static void Integers(Action<string, bool, string> check)
    {
        check("integer: zero", Parse("i0e") is BInteger { Value: 0 }, "");
        check("integer: positive", Parse("i42e") is BInteger { Value: 42 }, "");
        check("integer: negative", Parse("i-42e") is BInteger { Value: -42 }, "");
        check("integer: long.MaxValue",
            Parse("i9223372036854775807e") is BInteger { Value: long.MaxValue }, "");
        check("integer: long.MinValue",
            Parse("i-9223372036854775808e") is BInteger { Value: long.MinValue }, "");

        Rejects(check, "integer: no digits", "ie");
        Rejects(check, "integer: leading zero", "i03e");
        Rejects(check, "integer: negative zero", "i-0e");
        Rejects(check, "integer: unterminated", "i42");
        Rejects(check, "integer: past 64 bits", "i9223372036854775808e");
        Rejects(check, "integer: lone minus", "i-e");
    }

    private static void Strings(Action<string, bool, string> check)
    {
        check("string: empty", Parse("0:") is BString { Length: 0 }, "");
        check("string: text", (Parse("4:spam") as BString)?.Text == "spam", "");

        // Bencode strings are bytes. A file name that is not valid UTF-8 has to
        // survive being read and written back out unchanged.
        byte[] raw = [(byte)'2', (byte)':', 0xFF, 0xFE];
        BValue parsed = BencodeParser.Parse(raw);
        check("string: holds bytes that are not UTF-8",
            parsed is BString { Length: 2 } s && s.Span[0] == 0xFF && s.Span[1] == 0xFE, "");
        check("string: those bytes survive a round trip",
            BencodeWriter.Encode(parsed).AsSpan().SequenceEqual(raw), "");

        Rejects(check, "string: leading zero in length", "01:a");
        Rejects(check, "string: no colon", "4spam");
        Rejects(check, "string: runs past the end", "5:abc");
        Rejects(check, "string: negative length", "-1:a");
    }

    private static void Lists(Action<string, bool, string> check)
    {
        check("list: empty", Parse("le") is BList { Count: 0 }, "");

        BList? list = Parse("l4:spami42ee") as BList;
        check("list: two items", list is { Count: 2 }, "");
        check("list: items in order",
            (list?[0] as BString)?.Text == "spam" && (list?[1] as BInteger)?.Value == 42, "");

        Rejects(check, "list: unterminated", "l4:spam");
    }

    private static void Dictionaries(Action<string, bool, string> check)
    {
        check("dictionary: empty", Parse("de") is BDictionary { Count: 0 }, "");

        BDictionary? dictionary = Parse("d3:bar4:spam3:fooi42ee") as BDictionary;
        check("dictionary: two entries", dictionary is { Count: 2 }, "");
        check("dictionary: lookup by key",
            dictionary?.GetString("bar") == "spam" && dictionary?.GetInteger("foo") == 42, "");
        check("dictionary: missing key reads as null",
            dictionary?.Get("nothing") == null && dictionary?.GetInteger("nothing") == null, "");
        check("dictionary: sorted keys are reported sorted", dictionary?.KeysAreSorted == true, "");

        // Out-of-order keys break the format's rule, but re-sorting them would
        // change the file's bytes and so its infohash. They are kept as found
        // and only reported.
        BDictionary? unsorted = Parse("d3:foo4:spam3:bari42ee") as BDictionary;
        check("dictionary: unsorted keys are kept, not rejected", unsorted is { Count: 2 }, "");
        check("dictionary: unsorted keys are reported", unsorted?.KeysAreSorted == false, "");
        check("dictionary: unsorted keys keep their order",
            unsorted != null && Encoding.UTF8.GetString(unsorted.Entries[0].Key.Span) == "foo", "");

        Rejects(check, "dictionary: repeated key", "d3:fooi1e3:fooi2ee");
        Rejects(check, "dictionary: integer key", "di1e3:fooe");
        Rejects(check, "dictionary: value missing", "d3:fooe");
        Rejects(check, "dictionary: unterminated", "d3:fooi1e");
    }

    private static void SourceRanges(Action<string, bool, string> check)
    {
        // This is the property the infohash rests on: every parsed value knows
        // which bytes it came from, so the info dictionary can be hashed as it
        // arrived instead of as this client would have written it.
        const string text = "d4:infod3:fooi42eee";
        BDictionary root = (BDictionary)Parse(text);
        BValue info = root.Get("info")!;

        check("source: the info dictionary knows its own range",
            info is { SourceStart: 7, SourceLength: 11 },
            $"got {info.SourceStart}..{info.SourceStart + info.SourceLength}");

        check("source: that range is exactly the info dictionary",
            text.Substring(info.SourceStart, info.SourceLength) == "d3:fooi42ee",
            text.Substring(info.SourceStart, info.SourceLength));

        check("source: the top-level value spans the whole buffer",
            root is { SourceStart: 0, SourceLength: 19 }, "");

        check("source: a value built in memory has no range",
            new BInteger(1) is { HasSource: false, SourceStart: -1 }, "");
    }

    private static void RoundTrips(Action<string, bool, string> check)
    {
        string[] samples =
        [
            "i0e",
            "i-42e",
            "0:",
            "4:spam",
            "le",
            "l4:spami42ee",
            "de",
            "d3:bar4:spam3:fooi42ee",
            "d3:foo4:spam3:bari42ee",
            "d8:announce4:spam4:infod6:lengthi1e4:name4:testee",
            "lli1ei2eeld1:ai1eeee",
        ];

        foreach (string sample in samples)
        {
            byte[] original = Encoding.ASCII.GetBytes(sample);
            byte[] encoded = BencodeWriter.Encode(BencodeParser.Parse(original));
            check($"round trip: {sample}", encoded.AsSpan().SequenceEqual(original),
                $"got {Encoding.ASCII.GetString(encoded)}");
        }
    }

    private static void Limits(Action<string, bool, string> check)
    {
        Rejects(check, "trailing data after the value", "i1ei2e");
        Rejects(check, "empty buffer", "");
        Rejects(check, "unknown type byte", "x");

        // Deep nesting is the one input that could take the process down with
        // it rather than just failing, so the depth limit is checked from both
        // sides.
        string deep = new string('l', BencodeParser.MaxDepth + 2) + new string('e', BencodeParser.MaxDepth + 2);
        Rejects(check, $"nesting past {BencodeParser.MaxDepth} levels", deep);

        string allowed = new string('l', BencodeParser.MaxDepth) + new string('e', BencodeParser.MaxDepth);
        check($"nesting up to {BencodeParser.MaxDepth} levels is allowed",
            BencodeParser.TryParse(Encoding.ASCII.GetBytes(allowed), out _, out _), "");
    }

    private static BValue Parse(string text) => BencodeParser.Parse(Encoding.ASCII.GetBytes(text));

    private static void Rejects(Action<string, bool, string> check, string name, string text)
    {
        bool parsed = BencodeParser.TryParse(Encoding.ASCII.GetBytes(text), out _, out string? error);
        check($"rejects {name}", !parsed, parsed ? "it was accepted" : error ?? "");
    }
}
