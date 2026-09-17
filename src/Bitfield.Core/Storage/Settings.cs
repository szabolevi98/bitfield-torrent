using System.Text;
using Bitfield.Core.Bencode;

namespace Bitfield.Core.Storage;

/// <summary>
/// What the client has been told to do, kept beside the torrent list.
///
/// The defaults are the ones a client should have before anybody has opened
/// the settings: no rate limits, the DHT on, the port forwarded, and the window
/// behaving like a window rather than disappearing into the notification area.
/// A client that throttles or hides itself by default is a client doing
/// something its user did not ask for.
/// </summary>
public sealed record Settings
{
    /// <summary>Where downloads go unless told otherwise. Empty means ask every time.</summary>
    public string DefaultSavePath { get; init; } = "";

    public int Port { get; init; } = 6881;

    public bool UseUpnp { get; init; } = true;

    public bool UseDht { get; init; } = true;

    /// <summary>Kilobytes a second across every torrent, or zero for no limit.</summary>
    public int DownloadLimitKb { get; init; }

    public int UploadLimitKb { get; init; }

    /// <summary>Peer connections across every torrent at once.</summary>
    public int MaxPeers { get; init; } = 200;

    public bool StartWithWindows { get; init; }

    public bool MinimiseToTray { get; init; }

    public bool CloseToTray { get; init; }

    public bool NotifyOnComplete { get; init; } = true;

    /// <summary>
    /// Which column the torrent list is ordered by, and which way. Remembered
    /// because a list that forgets how it was sorted every time the client
    /// starts is a list somebody has to sort again every time.
    /// </summary>
    public int SortColumn { get; init; }

    public bool SortDescending { get; init; }

    public static string PathFor(string root) => Path.Combine(root, "settings");

    public static Settings Load(string path)
    {
        try
        {
            if (!File.Exists(path) || BencodeParser.Parse(File.ReadAllBytes(path)) is not BDictionary saved)
            {
                return new Settings();
            }

            Settings defaults = new();

            return new Settings
            {
                DefaultSavePath = saved.GetString("save path") ?? defaults.DefaultSavePath,
                Port = Clamp(saved.GetInteger("port"), 1, 65535, defaults.Port),
                UseUpnp = Flag(saved, "upnp", defaults.UseUpnp),
                UseDht = Flag(saved, "dht", defaults.UseDht),
                DownloadLimitKb = Clamp(saved.GetInteger("down limit"), 0, int.MaxValue, defaults.DownloadLimitKb),
                UploadLimitKb = Clamp(saved.GetInteger("up limit"), 0, int.MaxValue, defaults.UploadLimitKb),
                MaxPeers = Clamp(saved.GetInteger("max peers"), 1, 2000, defaults.MaxPeers),
                StartWithWindows = Flag(saved, "start with windows", defaults.StartWithWindows),
                MinimiseToTray = Flag(saved, "minimise to tray", defaults.MinimiseToTray),
                CloseToTray = Flag(saved, "close to tray", defaults.CloseToTray),
                NotifyOnComplete = Flag(saved, "notify on complete", defaults.NotifyOnComplete),
                SortColumn = Clamp(saved.GetInteger("sort column"), 0, 7, defaults.SortColumn),
                SortDescending = Flag(saved, "sort descending", defaults.SortDescending),
            };
        }
        catch (Exception e) when (e is BencodeException or IOException)
        {
            // A settings file that cannot be read is replaced by the defaults
            // rather than stopping the client from starting.
            return new Settings();
        }
    }

    public void Save(string path)
    {
        BDictionary saved = Dictionary(
            ("close to tray", new BInteger(CloseToTray ? 1 : 0)),
            ("dht", new BInteger(UseDht ? 1 : 0)),
            ("down limit", new BInteger(DownloadLimitKb)),
            ("max peers", new BInteger(MaxPeers)),
            ("minimise to tray", new BInteger(MinimiseToTray ? 1 : 0)),
            ("notify on complete", new BInteger(NotifyOnComplete ? 1 : 0)),
            ("port", new BInteger(Port)),
            ("save path", new BString(DefaultSavePath)),
            ("sort column", new BInteger(SortColumn)),
            ("sort descending", new BInteger(SortDescending ? 1 : 0)),
            ("start with windows", new BInteger(StartWithWindows ? 1 : 0)),
            ("up limit", new BInteger(UploadLimitKb)),
            ("upnp", new BInteger(UseUpnp ? 1 : 0)));

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);

        string temporary = path + ".new";
        File.WriteAllBytes(temporary, BencodeWriter.Encode(saved));
        File.Move(temporary, path, overwrite: true);
    }

    private static bool Flag(BDictionary saved, string key, bool fallback) =>
        saved.GetInteger(key) is { } value ? value == 1 : fallback;

    private static int Clamp(long? value, int low, int high, int fallback) =>
        value is { } number && number >= low && number <= high ? (int)number : fallback;

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(
                Encoding.ASCII.GetBytes(entry.Key), entry.Value))]);
}
