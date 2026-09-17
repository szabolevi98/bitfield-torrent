using System.Text;

namespace Bitfield.Core.Storage;

/// <summary>
/// Turns the paths inside a torrent into paths Windows will accept.
///
/// The parser deliberately leaves alone the characters that are legal
/// elsewhere and not here — a torrent made on Linux may well contain
/// <c>episode 1: pilot.mkv</c>, and refusing to download it would be worse than
/// writing it under a slightly different name. Doing the substitution here, at
/// the last moment before a file is created, keeps the torrent's own idea of
/// its contents intact everywhere else.
/// </summary>
public static class LocalPath
{
    private const char Replacement = '_';

    /// <summary>
    /// Names Windows refuses whatever extension follows them, inherited from
    /// the device names of DOS and never removed.
    /// </summary>
    private static readonly string[] ReservedNames =
    [
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    ];

    /// <summary>
    /// Maps one path from a torrent — forward slashes, already checked for
    /// components that would escape the download directory — onto a relative
    /// path safe to create.
    /// </summary>
    public static string FromTorrentPath(string path)
    {
        string[] components = path.Split('/');
        for (int i = 0; i < components.Length; i++)
        {
            components[i] = Component(components[i]);
        }

        return Path.Combine(components);
    }

    private static string Component(string component)
    {
        StringBuilder mapped = new(component.Length);

        foreach (char c in component)
        {
            mapped.Append(c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' || c < ' '
                ? Replacement
                : c);
        }

        // A name ending in a dot or a space can be created through the API and
        // then not opened again by ordinary means.
        while (mapped.Length > 0 && (mapped[^1] == '.' || mapped[^1] == ' '))
        {
            mapped[^1] = Replacement;
        }

        string result = mapped.ToString();

        int extension = result.IndexOf('.');
        string stem = extension < 0 ? result : result[..extension];

        foreach (string reserved in ReservedNames)
        {
            if (string.Equals(stem, reserved, StringComparison.OrdinalIgnoreCase))
            {
                return string.Concat(stem, "_", extension < 0 ? "" : result[extension..]);
            }
        }

        return result.Length == 0 ? Replacement.ToString() : result;
    }
}
