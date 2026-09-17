using System.Diagnostics;
using System.Text;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Resolves a real magnet link: announce to whatever trackers it lists, then
/// ask the peers they name for the torrent's own description and check it
/// against the hash the link carried.
///
/// Given a directory as well, it goes on to download what the description
/// turned out to describe — a magnet link from nothing but a hash to a finished
/// file.
///
///     dotnet run --project tests/Bitfield.Tests -- magnet "magnet:?..." [directory] [expected sha-256]
/// </summary>
internal static class LiveMagnetRunner
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: magnet \"magnet:?xt=urn:btih:...\" [directory] [expected sha-256]");
            return 2;
        }

        if (!MagnetLink.TryParse(args[1], out MagnetLink? link, out string? error))
        {
            Console.Error.WriteLine($"not a usable magnet link: {error}");
            return 2;
        }

        Console.WriteLine($"infohash  {link!.InfoHash}");
        Console.WriteLine($"name      {link.DisplayName ?? "(not given)"}");

        foreach (string tracker in link.Trackers)
        {
            Console.WriteLine($"tracker   {tracker}");
        }

        Console.WriteLine();

        Stopwatch clock = Stopwatch.StartNew();
        Metainfo torrent;

        try
        {
            torrent = MagnetResolver.ResolveAsync(
                link,
                PeerId.Generate(),
                port: 6881,
                note: note => Console.WriteLine($"          {note}"))
                .GetAwaiter().GetResult();
        }
        catch (MetainfoException e)
        {
            Console.Error.WriteLine($"failed    {e.Message}");
            return 1;
        }

        clock.Stop();

        Console.WriteLine();
        Console.WriteLine($"resolved  in {clock.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"name      {torrent.Name}");
        Console.WriteLine($"size      {torrent.TotalLength:N0} bytes in {torrent.PieceCount:N0} pieces of {torrent.PieceLength:N0}");
        Console.WriteLine($"files     {torrent.Files.Count}");
        Console.WriteLine($"private   {torrent.IsPrivate}");

        // The check that makes the whole exchange safe: the description a
        // stranger handed over has to be the one the link asked for.
        Console.WriteLine($"infohash  {torrent.InfoHash}");
        Console.WriteLine($"matches   {torrent.InfoHash == link.InfoHash}");

        if (torrent.InfoHash != link.InfoHash)
        {
            return 1;
        }

        if (args.Length < 3)
        {
            return 0;
        }

        // The description is written out as the torrent file it would have
        // been, and the download proceeds as it would for any other.
        string directory = args[2];
        Directory.CreateDirectory(directory);

        string saved = Path.Combine(directory, $"{torrent.InfoHash}.torrent");
        File.WriteAllBytes(saved, TorrentFileBytes(torrent));

        Console.WriteLine($"saved     {saved}");
        Console.WriteLine();

        return LiveDownloadRunner.Run(["download", directory, saved, args.Length > 3 ? args[3] : ""]);
    }

    /// <summary>
    /// Wraps an info dictionary back into the file a torrent would have been,
    /// its own bytes untouched so that the infohash stays what it was.
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
}
