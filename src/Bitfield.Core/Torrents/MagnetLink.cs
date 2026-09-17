using System.Net;

namespace Bitfield.Core.Torrents;

/// <summary>
/// A magnet link: a torrent named by its infohash instead of described by a
/// file.
///
/// Everything else in it is a hint. The name is only what to call the download
/// until the real one arrives, the trackers are somewhere to start looking, and
/// the peers are somewhere to ask. What makes it work is that the infohash is
/// the torrent's identity, so the description can be fetched from the swarm and
/// checked against the link that asked for it.
/// </summary>
public sealed record MagnetLink
{
    public required InfoHash InfoHash { get; init; }

    /// <summary>The suggested name, which nothing has verified.</summary>
    public string? DisplayName { get; init; }

    public IReadOnlyList<string> Trackers { get; init; } = [];

    /// <summary>Peers named in the link itself, as <c>x.pe</c> (BEP 9).</summary>
    public IReadOnlyList<IPEndPoint> Peers { get; init; } = [];

    public static MagnetLink Parse(string uri)
    {
        if (!TryParse(uri, out MagnetLink? link, out string? error))
        {
            throw new FormatException(error);
        }

        return link!;
    }

    public static bool TryParse(string uri, out MagnetLink? link) => TryParse(uri, out link, out _);

    public static bool TryParse(string uri, out MagnetLink? link, out string? error)
    {
        link = null;

        if (!uri.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
        {
            error = "not a magnet link";
            return false;
        }

        InfoHash? infoHash = null;
        string? name = null;
        List<string> trackers = [];
        List<IPEndPoint> peers = [];

        foreach (string pair in uri["magnet:?".Length..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = pair.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            string key = pair[..equals];
            string value = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

            switch (key)
            {
                // A link may name the same torrent several ways — a version 2
                // hash alongside the version 1 one, most often. The first
                // version 1 hash is the one this client can act on.
                case "xt" when infoHash == null && TryReadInfoHash(value, out InfoHash parsed):
                    infoHash = parsed;
                    break;

                case "dn":
                    name ??= value;
                    break;

                case "tr" when value.Length > 0:
                    trackers.Add(value);
                    break;

                case "x.pe" when IPEndPoint.TryParse(value, out IPEndPoint? peer) && peer.Port > 0:
                    peers.Add(peer);
                    break;
            }
        }

        if (infoHash == null)
        {
            error = "the link carries no version 1 infohash";
            return false;
        }

        link = new MagnetLink
        {
            InfoHash = infoHash.Value,
            DisplayName = name,
            Trackers = trackers,
            Peers = peers,
        };

        error = null;
        return true;
    }

    /// <summary>
    /// Reads <c>urn:btih:</c> in either spelling: forty hexadecimal characters,
    /// or the thirty-two character base32 form that older clients wrote.
    /// </summary>
    private static bool TryReadInfoHash(string value, out InfoHash infoHash)
    {
        infoHash = default;

        const string prefix = "urn:btih:";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string hash = value[prefix.Length..];

        if (hash.Length == InfoHash.Size * 2)
        {
            return InfoHash.TryParse(hash, out infoHash);
        }

        if (hash.Length == 32 && TryDecodeBase32(hash, out byte[]? bytes))
        {
            infoHash = new InfoHash(bytes);
            return true;
        }

        return false;
    }

    private static bool TryDecodeBase32(string text, out byte[] bytes)
    {
        bytes = new byte[InfoHash.Size];

        int buffer = 0;
        int bits = 0;
        int written = 0;

        foreach (char c in text)
        {
            int value = c switch
            {
                >= 'A' and <= 'Z' => c - 'A',
                >= 'a' and <= 'z' => c - 'a',
                >= '2' and <= '7' => c - '2' + 26,
                _ => -1,
            };

            if (value < 0)
            {
                return false;
            }

            buffer = (buffer << 5) | value;
            bits += 5;

            if (bits >= 8)
            {
                bits -= 8;
                if (written == bytes.Length)
                {
                    return false;
                }

                bytes[written++] = (byte)(buffer >> bits);
            }
        }

        return written == bytes.Length;
    }

    public override string ToString() =>
        $"magnet:?xt=urn:btih:{InfoHash}" + (DisplayName is { Length: > 0 } name ? $"&dn={Uri.EscapeDataString(name)}" : "");
}
