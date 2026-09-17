using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Downloads a whole torrent, then checks the finished file against the
/// checksum its publisher published — which is the only measurement that
/// settles whether this client works, because it is made by somebody else and
/// nothing here can influence it.
///
///     dotnet run --project tests/Bitfield.Tests -- download &lt;directory&gt; [.torrent] [expected sha-256]
/// </summary>
internal static class LiveDownloadRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: download <directory> [path to a .torrent] [expected sha-256]");
            return 2;
        }

        string directory = args[1];
        // An empty argument stands for "the one built in", so that a checksum
        // can be given without naming a torrent file.
        Metainfo torrent = LoadTorrent(args.Length > 2 && args[2].Length > 0 ? args[2] : null);
        string? expected = args.Length > 3 && args[3].Length > 0 ? args[3].ToLowerInvariant() : null;

        PeerId peerId = PeerId.Generate();
        string resumePath = ResumeData.PathFor(Path.Combine(directory, ".bitfield"), torrent.InfoHash);

        Console.WriteLine($"torrent   {torrent.Name}");
        Console.WriteLine($"size      {torrent.TotalLength:N0} bytes in {torrent.PieceCount:N0} pieces of {torrent.PieceLength:N0}");
        Console.WriteLine($"files     {torrent.Files.Count}");
        Console.WriteLine($"into      {Path.GetFullPath(directory)}");
        Console.WriteLine();

        return RunAsync(torrent, directory, resumePath, peerId, expected).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(
        Metainfo torrent,
        string directory,
        string resumePath,
        PeerId peerId,
        string? expected)
    {
        await using TorrentStorage storage = new(torrent, directory);
        storage.Create();

        PieceBitfield have = await LoadProgressAsync(torrent, storage, resumePath).ConfigureAwait(false);

        if (have.IsComplete)
        {
            Console.WriteLine("already complete");
        }
        else
        {
            TorrentDownload download = new(torrent, storage, have, peerId, resumePath);
            await FollowAsync(download, torrent).ConfigureAwait(false);
        }

        return await CheckAsync(torrent, storage, expected).ConfigureAwait(false);
    }

    /// <summary>
    /// Works out what is already on disk: the resume file when it can be
    /// believed, and hashing everything when it cannot.
    /// </summary>
    private static async Task<PieceBitfield> LoadProgressAsync(
        Metainfo torrent,
        TorrentStorage storage,
        string resumePath)
    {
        ResumeData? resume = ResumeData.TryLoad(resumePath, torrent, storage);
        if (resume != null)
        {
            Console.WriteLine($"resume    {resume.Pieces} from the resume file");
            return resume.Pieces;
        }

        if (!File.Exists(resumePath) && storage.Files.All(f => !File.Exists(f.FullPath) || new FileInfo(f.FullPath).Length == 0))
        {
            return new PieceBitfield(torrent.PieceCount);
        }

        Stopwatch clock = Stopwatch.StartNew();
        Console.Write("verify    hashing what is on disk");

        PieceBitfield have = await storage.VerifyAsync().ConfigureAwait(false);

        clock.Stop();
        Console.WriteLine($"\rverify    {have}, hashed in {clock.Elapsed.TotalSeconds:N1} s          ");
        return have;
    }

    private static async Task FollowAsync(TorrentDownload download, Metainfo torrent)
    {
        Stopwatch clock = Stopwatch.StartNew();
        long lastBytes = 0;
        TimeSpan lastAt = TimeSpan.Zero;

        download.Note += note =>
        {
            Console.WriteLine($"\r          {note}".PadRight(90));
        };

        download.Progress += progress =>
        {
            TimeSpan now = clock.Elapsed;
            double seconds = (now - lastAt).TotalSeconds;
            double rate = seconds > 0 ? (progress.Downloaded - lastBytes) / seconds : 0;
            lastBytes = progress.Downloaded;
            lastAt = now;

            string bar = Bar(progress.Fraction);
            Console.Write(
                $"\r{bar} {progress.Fraction * 100,5:N1}%  "
                + $"{progress.PiecesHeld:N0}/{progress.PieceCount:N0} pieces  "
                + $"{rate / (1024 * 1024),6:N2} MB/s  "
                + $"{progress.ConnectedPeers,3} peers  "
                + $"{progress.FailedPieces} bad ");
        };

        using CancellationTokenSource cancel = new();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        try
        {
            await download.RunAsync(cancel.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine();
            Console.WriteLine("stopped, progress saved");
        }

        clock.Stop();

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"took      {clock.Elapsed.TotalSeconds:N1} s for {download.Downloaded:N0} bytes"
            + $" ({download.Downloaded / Math.Max(clock.Elapsed.TotalSeconds, 0.001) / (1024 * 1024):N2} MB/s average)");
    }

    /// <summary>
    /// The measurement this whole exercise exists for: hash the files that came
    /// out and compare with what the publisher says they should be.
    /// </summary>
    private static async Task<int> CheckAsync(Metainfo torrent, TorrentStorage storage, string? expected)
    {
        Console.WriteLine();

        PieceBitfield verified = await storage.VerifyAsync().ConfigureAwait(false);
        Console.WriteLine($"pieces    {verified} verified against the torrent's own hashes");

        if (!verified.IsComplete)
        {
            Console.Error.WriteLine("the download is not complete");
            return 1;
        }

        foreach (StoredFile file in storage.Files.Where(f => !f.IsPadding))
        {
            using FileStream stream = new(file.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
            string hex = Convert.ToHexStringLower(hash);

            Console.WriteLine($"sha-256   {hex}  {Path.GetFileName(file.FullPath)}");

            if (expected != null)
            {
                Console.WriteLine($"expected  {expected}");
                Console.WriteLine($"match     {(hex == expected ? "yes" : "NO")}");
                return hex == expected ? 0 : 1;
            }
        }

        return 0;
    }

    private static string Bar(double fraction)
    {
        const int width = 28;
        int filled = (int)Math.Round(fraction * width);
        return $"[{new string('#', filled)}{new string('.', width - filled)}]";
    }

    private static Metainfo LoadTorrent(string? path)
    {
        if (path != null)
        {
            return Metainfo.Load(path);
        }

        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("debian-13.7.0-amd64-netinst.iso.torrent")!;
        using MemoryStream buffer = new();
        stream.CopyTo(buffer);
        return Metainfo.Parse(buffer.ToArray());
    }
}
