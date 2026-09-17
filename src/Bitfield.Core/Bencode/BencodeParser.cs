using System.Buffers.Text;

namespace Bitfield.Core.Bencode;

/// <summary>
/// Reads bencode, the format torrent files and tracker replies are written in.
/// It has four types: integers, byte strings, lists and dictionaries.
///
/// The parser is strict about what the format calls illegal — leading zeros, a
/// negative zero, a repeated dictionary key, trailing bytes after the top-level
/// value — because a torrent file is untrusted input, and a lenient reader here
/// becomes a wrong infohash later.
/// </summary>
public static class BencodeParser
{
    /// <summary>
    /// How deeply lists and dictionaries may nest. The parser recurses, so this
    /// keeps a hostile file from running the stack out. Real torrents nest four
    /// or five levels at most.
    /// </summary>
    public const int MaxDepth = 64;

    public static BValue Parse(ReadOnlyMemory<byte> data)
    {
        if (!TryParse(data, out BValue? value, out string? error))
        {
            throw new BencodeException(error!);
        }

        return value!;
    }

    public static bool TryParse(ReadOnlyMemory<byte> data, out BValue? value, out string? error)
    {
        Cursor cursor = new(data);
        value = cursor.ParseValue(0);

        if (value == null)
        {
            error = cursor.Error ?? "not bencode";
            return false;
        }

        if (cursor.Position != data.Length)
        {
            value = null;
            error = $"{data.Length - cursor.Position} bytes of trailing data after the top-level value";
            return false;
        }

        error = null;
        return true;
    }

    private sealed class Cursor
    {
        private readonly ReadOnlyMemory<byte> _data;

        public Cursor(ReadOnlyMemory<byte> data) => _data = data;

        public int Position { get; private set; }

        public string? Error { get; private set; }

        private ReadOnlySpan<byte> Span => _data.Span;

        public BValue? ParseValue(int depth)
        {
            if (depth > MaxDepth)
            {
                return Fail($"nested deeper than {MaxDepth} levels at offset {Position}");
            }

            if (Position >= _data.Length)
            {
                return Fail($"a value was expected at offset {Position} but the buffer ended");
            }

            int start = Position;
            byte first = Span[Position];

            BValue? value = first switch
            {
                (byte)'i' => ParseInteger(),
                (byte)'l' => ParseList(depth),
                (byte)'d' => ParseDictionary(depth),
                >= (byte)'0' and <= (byte)'9' => ParseString(),
                _ => Fail($"unexpected byte 0x{first:X2} at offset {Position}"),
            };

            if (value != null)
            {
                value.SourceStart = start;
                value.SourceLength = Position - start;
            }

            return value;
        }

        private BValue? ParseInteger()
        {
            int start = Position;
            Position++;

            bool negative = Position < _data.Length && Span[Position] == (byte)'-';
            if (negative)
            {
                Position++;
            }

            int digitsStart = Position;
            while (Position < _data.Length && IsDigit(Span[Position]))
            {
                Position++;
            }

            int digits = Position - digitsStart;
            if (digits == 0)
            {
                return Fail($"the integer at offset {start} has no digits");
            }

            if (digits > 1 && Span[digitsStart] == (byte)'0')
            {
                return Fail($"the integer at offset {start} has a leading zero");
            }

            if (negative && digits == 1 && Span[digitsStart] == (byte)'0')
            {
                return Fail($"the integer at offset {start} is a negative zero");
            }

            if (Position >= _data.Length || Span[Position] != (byte)'e')
            {
                return Fail($"the integer at offset {start} is not terminated");
            }

            int numberStart = negative ? digitsStart - 1 : digitsStart;
            ReadOnlySpan<byte> text = Span[numberStart..Position];
            if (!Utf8Parser.TryParse(text, out long result, out int consumed) || consumed != text.Length)
            {
                return Fail($"the integer at offset {start} does not fit in 64 bits");
            }

            Position++;
            return new BInteger(result);
        }

        private BValue? ParseString()
        {
            int start = Position;
            while (Position < _data.Length && IsDigit(Span[Position]))
            {
                Position++;
            }

            int digits = Position - start;
            if (digits > 1 && Span[start] == (byte)'0')
            {
                return Fail($"the string length at offset {start} has a leading zero");
            }

            if (digits > 18)
            {
                return Fail($"the string length at offset {start} is absurd");
            }

            if (!Utf8Parser.TryParse(Span[start..Position], out long length, out _))
            {
                return Fail($"the string length at offset {start} is not a number");
            }

            if (Position >= _data.Length || Span[Position] != (byte)':')
            {
                return Fail($"the string at offset {start} has no colon after its length");
            }

            Position++;

            if (length > _data.Length - Position)
            {
                return Fail($"the string at offset {start} claims {length} bytes but only {_data.Length - Position} remain");
            }

            ReadOnlyMemory<byte> bytes = _data.Slice(Position, (int)length);
            Position += (int)length;
            return new BString(bytes);
        }

        private BValue? ParseList(int depth)
        {
            int start = Position;
            Position++;

            List<BValue> items = [];
            while (true)
            {
                if (Position >= _data.Length)
                {
                    return Fail($"the list at offset {start} is not terminated");
                }

                if (Span[Position] == (byte)'e')
                {
                    Position++;
                    return new BList(items);
                }

                BValue? item = ParseValue(depth + 1);
                if (item == null)
                {
                    return null;
                }

                items.Add(item);
            }
        }

        private BValue? ParseDictionary(int depth)
        {
            int start = Position;
            Position++;

            List<KeyValuePair<ReadOnlyMemory<byte>, BValue>> entries = [];
            bool sorted = true;
            ReadOnlyMemory<byte> previousKey = default;

            while (true)
            {
                if (Position >= _data.Length)
                {
                    return Fail($"the dictionary at offset {start} is not terminated");
                }

                if (Span[Position] == (byte)'e')
                {
                    Position++;
                    return new BDictionary(entries, sorted);
                }

                int keyStart = Position;
                if (!IsDigit(Span[Position]))
                {
                    return Fail($"the dictionary key at offset {keyStart} is not a string");
                }

                if (ParseString() is not BString key)
                {
                    return null;
                }

                if (entries.Count > 0)
                {
                    int order = key.Span.SequenceCompareTo(previousKey.Span);
                    if (order == 0)
                    {
                        return Fail($"the dictionary at offset {start} repeats the key at offset {keyStart}");
                    }

                    if (order < 0)
                    {
                        sorted = false;
                    }
                }

                BValue? value = ParseValue(depth + 1);
                if (value == null)
                {
                    return null;
                }

                entries.Add(new KeyValuePair<ReadOnlyMemory<byte>, BValue>(key.Bytes, value));
                previousKey = key.Bytes;
            }
        }

        private BValue? Fail(string message)
        {
            Error ??= message;
            return null;
        }

        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';
    }
}
