using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// The list of torrents the client keeps between runs.
///
/// A client that forgets its torrents when it closes is a downloader; these
/// checks are about it coming back with the same ones, including the torrents
/// that arrived as a magnet link and never had a file to be re-opened from.
/// </summary>
internal static class StoreTests
{
    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "bitfield-store", Guid.NewGuid().ToString("N"));

        try
        {
            TorrentStore store = new(root);

            check("store: it makes its own folder", Directory.Exists(store.Torrents), store.Torrents);

            TestTorrents.Built first = TestTorrents.BuildSingle("first.bin", 40_000, 16 * 1024, seed: 1);
            TestTorrents.Built second = TestTorrents.Build("second",
                [("a.bin", 20_000, false), ("b.bin", 30_000, false)], seed: 2);

            store.Save(first.Torrent, new TorrentState { SavePath = @"D:\downloads\one" });
            store.Save(second.Torrent, new TorrentState { SavePath = @"D:\downloads\two" });

            IReadOnlyList<StoredTorrent> loaded = store.Load();

            check("store: both come back", loaded.Count == 2, $"{loaded.Count}");

            StoredTorrent? back = loaded.FirstOrDefault(t => t.Torrent.InfoHash == first.Torrent.InfoHash);
            check("store: with their infohashes intact", back != null, "");
            check("store: and where they were going",
                back?.State.SavePath == @"D:\downloads\one", back?.State.SavePath ?? "none");
            check("store: and their names",
                back?.Torrent.Name == "first.bin", back?.Torrent.Name ?? "none");
            check("store: and everything the torrent described",
                back?.Torrent.PieceCount == first.Torrent.PieceCount
                && back?.Torrent.TotalLength == first.Torrent.TotalLength, "");

            // The info dictionary has to survive the round trip byte for byte,
            // because the infohash is over those bytes — this is the same
            // property a magnet link's description rests on.
            check("store: the info dictionary is stored unchanged",
                back != null && back.Torrent.RawInfo.Span.SequenceEqual(first.Torrent.RawInfo.Span), "");

            check("store: a multi-file torrent keeps its file list",
                loaded.First(t => t.Torrent.InfoHash == second.Torrent.InfoHash).Torrent.Files.Count == 2, "");

            // Order matters: the list should come back as it was built up.
            check("store: they come back oldest first",
                loaded[0].State.AddedOn <= loaded[1].State.AddedOn, "");

            // Anything unreadable is skipped rather than stopping the client
            // from starting at all.
            File.WriteAllBytes(store.StatePath(first.Torrent.InfoHash), "not bencode"u8.ToArray());

            List<string> complaints = [];
            IReadOnlyList<StoredTorrent> afterDamage = store.Load(complaints.Add);

            check("store: a damaged entry is skipped, not fatal", afterDamage.Count == 1, $"{afterDamage.Count}");
            check("store: and it says which one", complaints.Count == 1, string.Join("; ", complaints));

            store.Remove(second.Torrent.InfoHash);
            check("store: removing takes all three files away",
                !File.Exists(store.TorrentPath(second.Torrent.InfoHash))
                && !File.Exists(store.StatePath(second.Torrent.InfoHash))
                && !File.Exists(store.ResumePath(second.Torrent.InfoHash)), "");

            check("store: and it is gone from the list", store.Load().Count == 0, $"{store.Load().Count}");
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing over.
            }
        }
    }
}
