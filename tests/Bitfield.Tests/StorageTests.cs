using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Writing a torrent to disk. The arithmetic that matters is the one nobody
/// sees: pieces are cut from the torrent's byte stream without regard for file
/// boundaries, so a piece routinely has to be written across two or three files
/// at once, and getting the split wrong produces files that are exactly the
/// right size and entirely wrong.
/// </summary>
internal static class StorageTests
{
    public static void Run(Action<string, bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "bitfield-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            Names(check);
            Layout(check, root);
            WritingAcrossFiles(check, root);
            Sparse(check, root);
            Verifying(check, root);
            Resuming(check, root);
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

    private static void Names(Action<string, bool, string> check)
    {
        // Characters the parser deliberately kept, because plenty of real
        // torrents carry them and refusing those torrents would be worse.
        check("names: a colon becomes an underscore",
            LocalPath.FromTorrentPath("episode 1: pilot.mkv") == "episode 1_ pilot.mkv",
            LocalPath.FromTorrentPath("episode 1: pilot.mkv"));
        check("names: the other illegal characters too",
            LocalPath.FromTorrentPath("a<b>c\"d|e?f*g") == "a_b_c_d_e_f_g",
            LocalPath.FromTorrentPath("a<b>c\"d|e?f*g"));

        // A name ending in a dot or a space can be created and then not opened
        // again by ordinary means.
        check("names: a trailing dot is replaced",
            LocalPath.FromTorrentPath("chapter.") == "chapter_", LocalPath.FromTorrentPath("chapter."));
        check("names: a trailing space is replaced",
            LocalPath.FromTorrentPath("chapter ") == "chapter_", LocalPath.FromTorrentPath("chapter "));

        // The DOS device names, which Windows still refuses whatever follows.
        check("names: a reserved device name is given a suffix",
            LocalPath.FromTorrentPath("CON.txt") == "CON_.txt", LocalPath.FromTorrentPath("CON.txt"));
        check("names: reserved even with no extension",
            LocalPath.FromTorrentPath("nul") == "nul_", LocalPath.FromTorrentPath("nul"));
        check("names: a name that merely starts with one is left alone",
            LocalPath.FromTorrentPath("console.log") == "console.log", LocalPath.FromTorrentPath("console.log"));

        check("names: directories are kept as directories",
            LocalPath.FromTorrentPath("album/disc one/track.flac")
                == Path.Combine("album", "disc one", "track.flac"), "");
    }

    private static void Layout(Action<string, bool, string> check, string root)
    {
        TestTorrents.Built built = TestTorrents.Build("album",
            [("cover.jpg", 1_000, false), ("disc one/track.flac", 30_000, false)]);

        string directory = Path.Combine(root, "layout");
        TorrentStorage storage = new(built.Torrent, directory);

        check("layout: the torrent's name leads the path",
            storage.Files[0].FullPath == Path.Combine(directory, "album", "cover.jpg"),
            storage.Files[0].FullPath);
        check("layout: directories inside the torrent are kept",
            storage.Files[1].FullPath == Path.Combine(directory, "album", "disc one", "track.flac"),
            storage.Files[1].FullPath);

        // Two paths that differ only in characters this platform cannot keep
        // would otherwise land on the same file and overwrite each other.
        TestTorrents.Built clashing = TestTorrents.Build("x",
            [("a:b.txt", 100, false), ("a?b.txt", 100, false)]);
        TorrentStorage colliding = new(clashing.Torrent, directory);

        check("layout: names that collide after mapping are separated",
            colliding.Files[0].FullPath != colliding.Files[1].FullPath,
            colliding.Files[0].FullPath);
        check("layout: and the second gets a numbered name",
            Path.GetFileName(colliding.Files[1].FullPath) == "a_b_2.txt",
            Path.GetFileName(colliding.Files[1].FullPath));
    }

    private static void WritingAcrossFiles(Action<string, bool, string> check, string root)
    {
        // Files far smaller than a piece, so that every piece covers several of
        // them and two pieces meet inside one file.
        TestTorrents.Built built = TestTorrents.Build("split",
            [
                ("one.bin", 5_000, false),
                ("two.bin", 20_000, false),
                ("three.bin", 100, false),
                ("four.bin", 12_000, false),
            ],
            pieceLength: 16 * 1024);

        Metainfo torrent = built.Torrent;
        string directory = Path.Combine(root, "split");

        check("writing: four files in 16 KiB pieces means pieces cross them",
            torrent.PieceCount == 3 && torrent.TotalLength == 37_100,
            $"{torrent.PieceCount} pieces, {torrent.TotalLength} bytes");

        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(torrent, directory);
            storage.Create();

            check("writing: every file was created at its full length",
                storage.Files.All(f => new FileInfo(f.FullPath).Length == f.Length), "");

            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                int begin = piece * torrent.PieceLength;
                await storage.WritePieceAsync(piece, built.Content.AsMemory(begin, torrent.PieceLengthAt(piece)));
            }

            // Each file has to hold exactly its own stretch of the stream.
            bool allMatch = true;
            foreach (StoredFile file in storage.Files)
            {
                byte[] written = ReadShared(file.FullPath);
                allMatch &= written.AsSpan().SequenceEqual(built.Content.AsSpan((int)file.Offset, (int)file.Length));
            }

            check("writing: every file holds its own stretch of the stream", allMatch, "");

            byte[] readBack = new byte[torrent.TotalLength];
            await storage.ReadAsync(0, readBack);
            check("writing: the whole stream reads back as it went in",
                readBack.AsSpan().SequenceEqual(built.Content), "");

            // The last piece is short, and writing a full-length one there
            // would run past the end of the torrent.
            bool refused = false;
            try
            {
                await storage.WritePieceAsync(2, new byte[torrent.PieceLength]);
            }
            catch (ArgumentException)
            {
                refused = true;
            }

            check("writing: a piece of the wrong length is refused", refused, "");
        });

        // Padding files exist to align the pieces around them and are never
        // created, but the bytes they stand for still belong to the stream.
        TestTorrents.Built padded = TestTorrents.Build("padded",
            [("real.bin", 20_000, false), (".pad/12768", 12_768, true)]);

        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(padded.Torrent, Path.Combine(root, "padded"));
            storage.Create();

            check("writing: a padding file is not created",
                !File.Exists(storage.Files[1].FullPath), storage.Files[1].FullPath);

            for (int piece = 0; piece < padded.Torrent.PieceCount; piece++)
            {
                int begin = piece * padded.Torrent.PieceLength;
                await storage.WritePieceAsync(
                    piece, padded.Content.AsMemory(begin, padded.Torrent.PieceLengthAt(piece)));
            }

            check("writing: the real file beside padding is still correct",
                ReadShared(storage.Files[0].FullPath).AsSpan()
                    .SequenceEqual(padded.Content.AsSpan(0, 20_000)), "");
        });
    }

    private static void Sparse(Action<string, bool, string> check, string root)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        TestTorrents.Built built = TestTorrents.BuildSingle("big.bin", 8 * 1024 * 1024, pieceLength: 256 * 1024);
        string directory = Path.Combine(root, "sparse");

        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(built.Torrent, directory);
            storage.Create();

            FileInfo file = new(storage.Files[0].FullPath);

            check("sparse: the file is created at its full length",
                file.Length == 8 * 1024 * 1024, $"{file.Length}");

            // Marked sparse, so an abandoned download occupies what has
            // actually arrived rather than what it claimed.
            check("sparse: and marked sparse, so it does not yet occupy the disk",
                file.Attributes.HasFlag(FileAttributes.SparseFile), $"{file.Attributes}");
        });
    }

    private static void Verifying(Action<string, bool, string> check, string root)
    {
        TestTorrents.Built built = TestTorrents.Build("verify",
            [("a.bin", 20_000, false), ("b.bin", 30_000, false)]);

        Metainfo torrent = built.Torrent;
        string directory = Path.Combine(root, "verify");

        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(torrent, directory);
            storage.Create();

            PieceBitfield empty = await storage.VerifyAsync();
            check("verifying: an untouched torrent has nothing", empty.IsEmpty, $"{empty}");

            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                int begin = piece * torrent.PieceLength;
                await storage.WritePieceAsync(piece, built.Content.AsMemory(begin, torrent.PieceLengthAt(piece)));
            }

            PieceBitfield complete = await storage.VerifyAsync();
            check("verifying: a written torrent is complete", complete.IsComplete, $"{complete}");
        });

        // One byte changed in the middle of the second file, which is one piece
        // wrong and the rest untouched.
        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(torrent, directory);
            string path = storage.Files[1].FullPath;
            byte[] bytes = ReadShared(path);
            bytes[5_000] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            PieceBitfield after = await storage.VerifyAsync();
            int damaged = (int)((20_000 + 5_000) / torrent.PieceLength);

            check("verifying: a changed byte costs exactly its own piece",
                after.SetCount == torrent.PieceCount - 1 && !after[damaged],
                $"{after}, piece {damaged}");
        });
    }

    private static void Resuming(Action<string, bool, string> check, string root)
    {
        TestTorrents.Built built = TestTorrents.Build("resume",
            [("a.bin", 20_000, false), ("b.bin", 30_000, false)]);

        Metainfo torrent = built.Torrent;
        string directory = Path.Combine(root, "resume");
        string resumePath = ResumeData.PathFor(Path.Combine(root, "state"), torrent.InfoHash);

        check("resuming: the resume file is named after the infohash",
            Path.GetFileName(resumePath) == $"{torrent.InfoHash}.resume", Path.GetFileName(resumePath));

        RunAsync(async () =>
        {
            await using TorrentStorage storage = new(torrent, directory);
            storage.Create();

            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                int begin = piece * torrent.PieceLength;
                await storage.WritePieceAsync(piece, built.Content.AsMemory(begin, torrent.PieceLengthAt(piece)));
            }

            PieceBitfield have = await storage.VerifyAsync();
            ResumeData.Capture(torrent, storage, have, downloaded: torrent.TotalLength, uploaded: 1_234)
                .Save(resumePath);

            check("resuming: the resume file was written", File.Exists(resumePath), resumePath);

            ResumeData? loaded = ResumeData.TryLoad(resumePath, torrent, storage);
            check("resuming: it reads back", loaded != null, "");
            check("resuming: with the pieces it recorded",
                loaded?.Pieces.IsComplete == true, $"{loaded?.Pieces}");
            check("resuming: and the counters",
                loaded is { Downloaded: 50_000, Uploaded: 1_234 },
                $"{loaded?.Downloaded}/{loaded?.Uploaded}");

            // A resume file is a shortcut, and every reason to doubt it has to
            // send the client back to hashing rather than to trusting it.
            TestTorrents.Built other = TestTorrents.Build("resume",
                [("a.bin", 20_000, false), ("b.bin", 30_000, false)], seed: 2);
            await using TorrentStorage otherStorage = new(other.Torrent, directory);

            check("resuming: a resume file for a different torrent is ignored",
                ResumeData.TryLoad(resumePath, other.Torrent, otherStorage) == null, "");

            File.SetLastWriteTimeUtc(storage.Files[0].FullPath, DateTime.UtcNow.AddMinutes(1));
            check("resuming: a file touched since is ignored",
                ResumeData.TryLoad(resumePath, torrent, storage) == null, "");

            check("resuming: a missing resume file is simply absent",
                ResumeData.TryLoad(resumePath + ".nothing", torrent, storage) == null, "");

            File.WriteAllBytes(resumePath, "not bencode"u8.ToArray());
            check("resuming: a corrupted resume file is ignored rather than fatal",
                ResumeData.TryLoad(resumePath, torrent, storage) == null, "");
        });
    }

    /// <summary>
    /// Reads a file the storage may still hold open. The default overload asks
    /// for a share mode that forbids other writers, which the storage's own
    /// read-write handle already is.
    /// </summary>
    private static byte[] ReadShared(string path)
    {
        using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] bytes = new byte[file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static void RunAsync(Func<Task> work) => work().GetAwaiter().GetResult();
}
