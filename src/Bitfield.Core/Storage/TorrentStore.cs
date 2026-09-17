using System.Text;
using Bitfield.Core.Bencode;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Storage;

/// <summary>What is remembered about a torrent besides the torrent itself.</summary>
public sealed record TorrentState
{
    public required string SavePath { get; init; }

    public DateTimeOffset AddedOn { get; init; } = DateTimeOffset.Now;

    /// <summary>
    /// Stopped by the user. Kept because a paused torrent that starts itself
    /// again when the client restarts is the client overruling the user.
    /// </summary>
    public bool Paused { get; init; }

    /// <summary>
    /// One byte per file, or empty when every file is wanted normally. Stored
    /// as bytes rather than a list of integers because a torrent may hold tens
    /// of thousands of files and the state file is rewritten every time one of
    /// them changes.
    /// </summary>
    public IReadOnlyList<Download.FilePriority> Priorities { get; init; } = [];
}

/// <summary>A torrent read back from the store on startup.</summary>
public sealed record StoredTorrent(Metainfo Torrent, TorrentState State);

/// <summary>
/// The list of torrents this client is running, kept where the client lives
/// rather than where the downloads go.
///
/// A client that forgets its torrents when it closes is a downloader, not a
/// client: torrents have to come back on the next run, seeding included,
/// without being opened again by hand. Three files per torrent — the metainfo,
/// the resume data and where it is going — because one index file for
/// everything is one file to corrupt and lose the lot.
/// </summary>
public sealed class TorrentStore
{
    public TorrentStore(string? root = null)
    {
        Root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bitfield");

        Torrents = Path.Combine(Root, "torrents");
        Directory.CreateDirectory(Torrents);
    }

    public string Root { get; }

    public string Torrents { get; }

    public string DhtTablePath => Path.Combine(Root, "dht.table");

    public string TorrentPath(InfoHash infoHash) => Path.Combine(Torrents, $"{infoHash}.torrent");

    public string ResumePath(InfoHash infoHash) => Path.Combine(Torrents, $"{infoHash}.resume");

    public string StatePath(InfoHash infoHash) => Path.Combine(Torrents, $"{infoHash}.state");

    /// <summary>
    /// Writes the torrent and what is known about it. The metainfo is stored as
    /// the file it came from, so a torrent added from a magnet link is as
    /// re-openable as one added from disk.
    /// </summary>
    public void Save(Metainfo torrent, TorrentState state)
    {
        File.WriteAllBytes(TorrentPath(torrent.InfoHash), TorrentFileBytes(torrent));

        BDictionary saved = Dictionary(
            ("added", new BInteger(state.AddedOn.ToUnixTimeSeconds())),
            ("paused", new BInteger(state.Paused ? 1 : 0)),
            ("priorities", new BString(state.Priorities.Select(priority => (byte)priority).ToArray())),
            ("save path", new BString(state.SavePath)),
            ("version", new BInteger(1)));

        WriteAtomically(StatePath(torrent.InfoHash), BencodeWriter.Encode(saved));
    }

    public void SaveState(InfoHash infoHash, TorrentState state)
    {
        BDictionary saved = Dictionary(
            ("added", new BInteger(state.AddedOn.ToUnixTimeSeconds())),
            ("paused", new BInteger(state.Paused ? 1 : 0)),
            ("priorities", new BString(state.Priorities.Select(priority => (byte)priority).ToArray())),
            ("save path", new BString(state.SavePath)),
            ("version", new BInteger(1)));

        WriteAtomically(StatePath(infoHash), BencodeWriter.Encode(saved));
    }

    /// <summary>
    /// Everything the store holds. A torrent whose files will not read is
    /// skipped with a note rather than stopping the client from starting — one
    /// damaged entry should not cost somebody the other nine.
    /// </summary>
    public IReadOnlyList<StoredTorrent> Load(Action<string>? note = null)
    {
        List<StoredTorrent> loaded = [];

        foreach (string path in Directory.EnumerateFiles(Torrents, "*.torrent"))
        {
            try
            {
                Metainfo torrent = Metainfo.Load(path);
                string statePath = StatePath(torrent.InfoHash);

                if (!File.Exists(statePath))
                {
                    note?.Invoke($"{Path.GetFileName(path)} has no saved state, skipping it");
                    continue;
                }

                if (BencodeParser.Parse(File.ReadAllBytes(statePath)) is not BDictionary state
                    || state.GetString("save path") is not { Length: > 0 } savePath)
                {
                    note?.Invoke($"{Path.GetFileName(statePath)} could not be read, skipping it");
                    continue;
                }

                loaded.Add(new StoredTorrent(torrent, new TorrentState
                {
                    SavePath = savePath,
                    AddedOn = DateTimeOffset.FromUnixTimeSeconds(state.GetInteger("added") ?? 0),
                    Paused = state.GetInteger("paused") == 1,
                    Priorities = ReadPriorities(state, torrent.Files.Count),
                }));
            }
            catch (Exception e) when (e is MetainfoException or BencodeException or IOException)
            {
                note?.Invoke($"{Path.GetFileName(path)} could not be loaded: {e.Message}");
            }
        }

        return [.. loaded.OrderBy(entry => entry.State.AddedOn)];
    }

    /// <summary>
    /// The saved priorities, or none at all when they do not match the torrent
    /// — a file list that has changed length means the two do not belong
    /// together, and treating every file as normal is the safe answer.
    /// </summary>
    private static IReadOnlyList<Download.FilePriority> ReadPriorities(BDictionary state, int fileCount)
    {
        if (state.GetByteString("priorities") is not { } stored || stored.Length != fileCount)
        {
            return [];
        }

        Download.FilePriority[] priorities = new Download.FilePriority[fileCount];

        for (int i = 0; i < fileCount; i++)
        {
            byte value = stored.Span[i];
            priorities[i] = value <= (byte)Download.FilePriority.High
                ? (Download.FilePriority)value
                : Download.FilePriority.Normal;
        }

        return priorities;
    }

    public void Remove(InfoHash infoHash)
    {
        foreach (string path in new[] { TorrentPath(infoHash), ResumePath(infoHash), StatePath(infoHash) })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A file that will not go is left; it will be overwritten if the
                // torrent is added again.
            }
        }
    }

    /// <summary>
    /// Wraps a torrent back into the file it would have been. A torrent that
    /// arrived as a magnet link never had one, and its info dictionary's own
    /// bytes are what the infohash is over, so they go in untouched.
    /// </summary>
    private static byte[] TorrentFileBytes(Metainfo torrent)
    {
        using MemoryStream file = new();
        file.WriteByte((byte)'d');

        List<string> trackers = [.. torrent.AnnounceTiers.SelectMany(tier => tier)];
        if (trackers.Count > 0)
        {
            file.Write("13:announce-listl"u8);

            foreach (string tracker in trackers)
            {
                byte[] url = Encoding.UTF8.GetBytes(tracker);
                file.Write(Encoding.ASCII.GetBytes($"l{url.Length}:"));
                file.Write(url);
                file.WriteByte((byte)'e');
            }

            file.WriteByte((byte)'e');
        }

        file.Write("4:info"u8);
        file.Write(torrent.RawInfo.Span);
        file.WriteByte((byte)'e');

        return file.ToArray();
    }

    /// <summary>
    /// Written beside the real file and moved into place, so that being
    /// interrupted leaves the previous state rather than half of a new one.
    /// </summary>
    private static void WriteAtomically(string path, byte[] bytes)
    {
        string temporary = path + ".new";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, path, overwrite: true);
    }

    private static BDictionary Dictionary(params (string Key, BValue Value)[] entries) =>
        new([.. entries
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry => new KeyValuePair<ReadOnlyMemory<byte>, BValue>(
                Encoding.ASCII.GetBytes(entry.Key), entry.Value))]);
}
