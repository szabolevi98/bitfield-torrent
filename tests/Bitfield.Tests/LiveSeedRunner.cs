using System.Diagnostics;
using System.Net;
using System.Reflection;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Seeds a finished torrent to the public swarm and reports what real clients
/// took. The swarm test proves this client can serve a copy to itself; this is
/// whether anybody else accepts what it sends.
///
/// The listener is off unless asked for, because binding a port that accepts
/// connections from the internet is not something to do to somebody's machine
/// as a side effect of a measurement — and it is not needed for the point being
/// made. Peers this client dialled can ask it for blocks over the same
/// connection, so uploading works through outgoing connections alone; what an
/// open port adds is the peers that could not have dialled in.
///
///     dotnet run --project tests/Bitfield.Tests -- seed &lt;directory&gt; [seconds] [.torrent] [--listen]
/// </summary>
internal static class LiveSeedRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: seed <directory> [seconds] [path to a .torrent] [--listen]");
            return 2;
        }

        string directory = args[1];
        int seconds = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : 60;
        string? torrentPath = args.Length > 3 && args[3].Length > 0 && !args[3].StartsWith("--") ? args[3] : null;
        bool listen = args.Contains("--listen");

        return RunAsync(directory, seconds, torrentPath, listen).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(string directory, int seconds, string? torrentPath, bool listen)
    {
        Metainfo torrent = LoadTorrent(torrentPath);

        await using TorrentStorage storage = new(torrent, directory);

        Console.WriteLine($"torrent   {torrent.Name}");
        Console.Write("verify    hashing what is on disk");

        PieceBitfield have = await storage.VerifyAsync().ConfigureAwait(false);
        Console.WriteLine($"\rverify    {have}                              ");

        if (!have.IsComplete)
        {
            Console.Error.WriteLine("nothing to seed: the download is not complete");
            return 1;
        }

        PeerId peerId = PeerId.Generate();
        TorrentDownload seed = new(torrent, storage, have, peerId)
        {
            KeepSeeding = true,
            MaxPeers = 50,
        };

        PeerListener? listener = null;
        Task listening = Task.CompletedTask;

        using CancellationTokenSource stop = new(TimeSpan.FromSeconds(seconds));

        PortMapping.Mapping? mapping = null;

        if (listen)
        {
            listener = new PeerListener(6881, infoHash => infoHash == seed.InfoHash ? seed : null);
            listening = listener.RunAsync(stop.Token);
            Console.WriteLine($"listening on {listener.Port}");

            mapping = await PortMapping.AddAsync(listener.Port, "Bitfield Torrent (seeding)", stop.Token)
                .ConfigureAwait(false);

            Console.WriteLine(mapping != null
                ? $"router    {mapping}"
                : "router    would not forward the port; only outgoing connections then");
        }
        else
        {
            Console.WriteLine("not listening: peers this client dialled can still ask it for blocks");
        }

        seed.Note += note => Console.WriteLine($"\r          {note}".PadRight(90));

        Stopwatch clock = Stopwatch.StartNew();
        long lastUploaded = 0;
        double lastSeconds = 0;

        seed.Progress += progress =>
        {
            long uploaded = seed.Uploaded;
            double now = clock.Elapsed.TotalSeconds;
            double rate = now > lastSeconds ? (uploaded - lastUploaded) / (now - lastSeconds) : 0;
            lastUploaded = uploaded;
            lastSeconds = now;

            Console.Write(
                $"\rseeding   {now,5:N0} s  "
                + $"{progress.ConnectedPeers,3} peers  "
                + $"{progress.SeedPeers,3} of them seeds  "
                + $"{progress.InterestedPeers,3} interested  "
                + $"{progress.UnchokedPeers,3} unchoked  "
                + $"uploaded {uploaded / (1024.0 * 1024),8:N2} MB  "
                + $"{rate / (1024 * 1024),6:N2} MB/s ");
        };

        try
        {
            await seed.RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The time asked for is up.
        }

        if (listener != null)
        {
            await listener.DisposeAsync().ConfigureAwait(false);
            await Quietly(listening).ConfigureAwait(false);
        }

        if (mapping != null)
        {
            await PortMapping.RemoveAsync(mapping).ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine("router    the port mapping was taken back");
        }

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"seeded    {seconds} s");
        Console.WriteLine($"uploaded  {seed.Uploaded:N0} bytes"
            + $" ({seed.Uploaded / (1024.0 * 1024):N2} MB, {seed.Uploaded / (1024.0 * 1024) / seconds:N2} MB/s average)");

        return seed.Uploaded > 0 ? 0 : 1;
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ending by cancellation is not a failure.
        }
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
