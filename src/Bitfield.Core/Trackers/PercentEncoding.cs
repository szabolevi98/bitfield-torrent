using System.Text;

namespace Bitfield.Core.Trackers;

/// <summary>
/// Percent-encoding for raw bytes, as a tracker query string needs it.
///
/// The framework's own encoders all take text. Handing them twenty bytes of
/// SHA-1 means deciding what those bytes are text in, and whatever is decided,
/// the bytes that are not valid in that encoding come out replaced rather than
/// escaped — which is a wrong infohash, and a tracker that has never heard of
/// the torrent.
/// </summary>
internal static class PercentEncoding
{
    public static void AppendEncoded(StringBuilder output, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            if (IsUnreserved(b))
            {
                output.Append((char)b);
            }
            else
            {
                output.Append('%').Append(HexDigit(b >> 4)).Append(HexDigit(b & 0xF));
            }
        }
    }

    public static string Encode(ReadOnlySpan<byte> bytes)
    {
        StringBuilder output = new(bytes.Length * 3);
        AppendEncoded(output, bytes);
        return output.ToString();
    }

    /// <summary>
    /// The unreserved set from RFC 3986. Everything else is escaped, which is
    /// more than strictly required and is what every other client does.
    /// </summary>
    private static bool IsUnreserved(byte b) =>
        b is >= (byte)'A' and <= (byte)'Z'
            or >= (byte)'a' and <= (byte)'z'
            or >= (byte)'0' and <= (byte)'9'
            or (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~';

    private static char HexDigit(int value) => (char)(value < 10 ? '0' + value : 'A' + (value - 10));
}
