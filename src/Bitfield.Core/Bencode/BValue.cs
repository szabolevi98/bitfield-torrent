using System.Text;

namespace Bitfield.Core.Bencode;

/// <summary>
/// One bencoded value. A value that was parsed remembers the byte range it
/// occupied in the buffer it came from, which is what lets the info dictionary
/// be hashed exactly as it arrived rather than as this library would have
/// written it back out.
/// </summary>
public abstract class BValue
{
    /// <summary>Offset of the first byte, or -1 for a value built in memory.</summary>
    public int SourceStart { get; internal set; } = -1;

    /// <summary>Number of bytes the value occupied, when it was parsed.</summary>
    public int SourceLength { get; internal set; }

    public bool HasSource => SourceStart >= 0;
}

public sealed class BInteger : BValue
{
    public BInteger(long value) => Value = value;

    public long Value { get; }

    public override string ToString() => Value.ToString();
}

/// <summary>
/// A byte string. Bencode has no notion of text encoding, and torrents in the
/// wild carry file names that are not valid UTF-8, so the bytes are what is
/// kept and text is only ever a view onto them.
/// </summary>
public sealed class BString : BValue
{
    public BString(ReadOnlyMemory<byte> bytes) => Bytes = bytes;

    public BString(string text) => Bytes = Encoding.UTF8.GetBytes(text);

    public ReadOnlyMemory<byte> Bytes { get; }

    public ReadOnlySpan<byte> Span => Bytes.Span;

    public int Length => Bytes.Length;

    /// <summary>The bytes decoded as UTF-8, with invalid sequences replaced.</summary>
    public string Text => Encoding.UTF8.GetString(Bytes.Span);

    public override string ToString() => Text;
}

public sealed class BList : BValue
{
    public BList(IReadOnlyList<BValue> items) => Items = items;

    public IReadOnlyList<BValue> Items { get; }

    public int Count => Items.Count;

    public BValue this[int index] => Items[index];
}

/// <summary>
/// A dictionary, kept in the order its keys were encountered rather than
/// re-sorted. Bencode requires sorted keys and nearly every file obeys, but
/// re-ordering a file that does not would change its infohash, so the order is
/// preserved and <see cref="KeysAreSorted"/> reports what was found.
/// </summary>
public sealed class BDictionary : BValue
{
    private readonly Dictionary<string, BValue> _index;

    public BDictionary(IReadOnlyList<KeyValuePair<ReadOnlyMemory<byte>, BValue>> entries, bool keysAreSorted = true)
    {
        Entries = entries;
        KeysAreSorted = keysAreSorted;
        _index = new Dictionary<string, BValue>(entries.Count, StringComparer.Ordinal);
        foreach ((ReadOnlyMemory<byte> key, BValue value) in entries)
        {
            _index[KeyToString(key.Span)] = value;
        }
    }

    public IReadOnlyList<KeyValuePair<ReadOnlyMemory<byte>, BValue>> Entries { get; }

    public bool KeysAreSorted { get; }

    public int Count => Entries.Count;

    public bool ContainsKey(string key) => _index.ContainsKey(key);

    public BValue? Get(string key) => _index.GetValueOrDefault(key);

    public long? GetInteger(string key) => Get(key) is BInteger i ? i.Value : null;

    public BString? GetByteString(string key) => Get(key) as BString;

    public string? GetString(string key) => (Get(key) as BString)?.Text;

    public BList? GetList(string key) => Get(key) as BList;

    public BDictionary? GetDictionary(string key) => Get(key) as BDictionary;

    /// <summary>
    /// Keys are compared as raw bytes, so a key that is not valid UTF-8 still
    /// maps to exactly one string. Latin-1 is a lossless byte-to-char mapping.
    /// </summary>
    internal static string KeyToString(ReadOnlySpan<byte> key) => Encoding.Latin1.GetString(key);
}
