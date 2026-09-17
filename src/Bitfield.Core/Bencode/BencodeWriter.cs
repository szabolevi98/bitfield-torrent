using System.Buffers;
using System.Buffers.Text;

namespace Bitfield.Core.Bencode;

/// <summary>
/// Writes bencode back out. Dictionary entries are written in the order the
/// value holds them, which for a parsed value is the order they appeared in,
/// so a parsed file re-encodes to the same bytes it came from. That is the
/// property the infohash depends on, and the one the checks assert.
/// </summary>
public static class BencodeWriter
{
    public static byte[] Encode(BValue value)
    {
        ArrayBufferWriter<byte> writer = new();
        Encode(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    public static void Encode(BValue value, IBufferWriter<byte> output)
    {
        switch (value)
        {
            case BInteger integer:
                WriteByte(output, (byte)'i');
                WriteNumber(output, integer.Value);
                WriteByte(output, (byte)'e');
                break;

            case BString text:
                WriteNumber(output, text.Length);
                WriteByte(output, (byte)':');
                output.Write(text.Span);
                break;

            case BList list:
                WriteByte(output, (byte)'l');
                foreach (BValue item in list.Items)
                {
                    Encode(item, output);
                }

                WriteByte(output, (byte)'e');
                break;

            case BDictionary dictionary:
                WriteByte(output, (byte)'d');
                foreach ((ReadOnlyMemory<byte> key, BValue item) in dictionary.Entries)
                {
                    WriteNumber(output, key.Length);
                    WriteByte(output, (byte)':');
                    output.Write(key.Span);
                    Encode(item, output);
                }

                WriteByte(output, (byte)'e');
                break;

            default:
                throw new BencodeException($"cannot encode {value.GetType().Name}");
        }
    }

    private static void WriteByte(IBufferWriter<byte> output, byte value)
    {
        Span<byte> span = output.GetSpan(1);
        span[0] = value;
        output.Advance(1);
    }

    private static void WriteNumber(IBufferWriter<byte> output, long value)
    {
        Span<byte> span = output.GetSpan(20);
        Utf8Formatter.TryFormat(value, span, out int written);
        output.Advance(written);
    }
}
