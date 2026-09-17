using System.Diagnostics;
using System.Reflection;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;
using Bitfield.Core.Trackers;

namespace Bitfield.Tests;

/// <summary>
/// Announces to a real tracker and prints what comes back. This is the one
/// thing about milestone 2 that cannot be settled offline: whether a live
/// tracker accepts the request as built and answers with peers.
///
/// Kept out of the default run because it needs a network and a tracker that
/// happens to be up, neither of which should be able to fail a build.
///
///     dotnet run --project tests/Bitfield.Tests -- announce [path to a .torrent]
/// </summary>
internal static class LiveTrackerRunner
{
    public static int Run(string[] args)
    {
        if (args[0] != "announce")
        {
            Console.Error.WriteLine($"unknown runner \"{args[0]}\"; the only one is \"announce\"");
            return 2;
        }

        Metainfo torrent;
        if (args.Length > 1)
        {
            torrent = Metainfo.Load(args[1]);
        }
        else
        {
            using Stream stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("debian-13.7.0-amd64-netinst.iso.torrent")!;
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            torrent = Metainfo.Parse(buffer.ToArray());
        }

        PeerId peerId = PeerId.Generate();

        Console.WriteLine($"torrent   {torrent.Name}");
        Console.WriteLine($"infohash  {torrent.InfoHash}");
        Console.WriteLine($"size      {torrent.TotalLength:N0} bytes in {torrent.PieceCount:N0} pieces");
        Console.WriteLine($"peer id   {peerId} ({peerId.ClientName()})");
        Console.WriteLine($"private   {torrent.IsPrivate}");
        Console.WriteLine();

        foreach (IReadOnlyList<string> tier in torrent.AnnounceTiers)
        {
            Console.WriteLine($"trackers  {string.Join(", ", tier)}");
        }

        Console.WriteLine();

        TrackerTiers tiers = new(torrent.AnnounceTiers);
        using HttpTrackerClient client = new();

        AnnounceRequest request = new()
        {
            InfoHash = torrent.InfoHash,
            PeerId = peerId,
            Port = 6881,
            Left = torrent.TotalLength,
            Event = TrackerEvent.Started,
            NumWant = 50,
        };

        try
        {
            Stopwatch clock = Stopwatch.StartNew();
            (Uri tracker, AnnounceResponse response) = tiers
                .AnnounceAsync(request, client.AnnounceAsync)
                .GetAwaiter().GetResult();
            clock.Stop();

            Console.WriteLine($"answered  {tracker} in {clock.ElapsedMilliseconds} ms");
            Console.WriteLine("swarm     " + (response.Seeders is { } seeders && response.Leechers is { } leechers
                ? $"{seeders:N0} seeders, {leechers:N0} leechers"
                : "not reported by this tracker"));
            Console.WriteLine($"interval  {response.Interval.TotalSeconds:N0} s"
                + (response.MinInterval is { } min ? $" (at least {min.TotalSeconds:N0} s)" : ""));

            if (response.Warning is { } warning)
            {
                Console.WriteLine($"warning   {warning}");
            }

            Console.WriteLine($"peers     {response.Peers.Count}");
            foreach (System.Net.IPEndPoint peer in response.Peers.Take(20))
            {
                Console.WriteLine($"          {peer}");
            }

            if (response.Peers.Count > 20)
            {
                Console.WriteLine($"          ... and {response.Peers.Count - 20} more");
            }

            // Having said hello, say goodbye: a tracker that is not told counts
            // this client as part of the swarm until the announce interval runs
            // out, and hands its address to peers that will find nothing there.
            try
            {
                client.AnnounceAsync(tracker, request with { Event = TrackerEvent.Stopped })
                    .GetAwaiter().GetResult();
                Console.WriteLine();
                Console.WriteLine("stopped   announced, this client is out of the swarm again");
            }
            catch (TrackerException e)
            {
                Console.WriteLine($"stopped   could not be announced: {e.Message}");
            }

            return response.Peers.Count > 0 ? 0 : 1;
        }
        catch (TrackerException e)
        {
            Console.Error.WriteLine($"failed    {e.Message}");
            return 1;
        }
    }
}
