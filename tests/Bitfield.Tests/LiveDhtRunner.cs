using System.Diagnostics;
using System.Net;
using Bitfield.Core.Dht;
using Bitfield.Core.Torrents;

namespace Bitfield.Tests;

/// <summary>
/// Joins the real DHT and finds a torrent's peers through it, with no tracker
/// involved at any point.
///
///     dotnet run --project tests/Bitfield.Tests -- dht [infohash] [table file]
/// </summary>
internal static class LiveDhtRunner
{
    private const string DebianInfoHash = "7acf8fb590b2060dd9c3146ef770169d593433b0";

    public static int Run(string[] args)
    {
        string hash = args.Length > 1 && args[1].Length > 0 ? args[1] : DebianInfoHash;
        string table = args.Length > 2 && args[2].Length > 0
            ? args[2]
            : Path.Combine(Path.GetTempPath(), "bitfield-dht.table");

        if (!InfoHash.TryParse(hash, out InfoHash infoHash))
        {
            Console.Error.WriteLine($"\"{hash}\" is not an infohash");
            return 2;
        }

        return RunAsync(infoHash, table).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(InfoHash infoHash, string tablePath)
    {
        // The id comes back with the table, because the table is only useful
        // under the id it was built for.
        await using DhtNode node = new(DhtNode.SavedId(tablePath), port: 0) { PeerPort = 6881 };

        using CancellationTokenSource stop = new(TimeSpan.FromMinutes(3));
        Task running = Task.Run(() => node.RunAsync(stop.Token), CancellationToken.None);

        Console.WriteLine($"node id   {node.Id}");
        Console.WriteLine($"port      {node.Port}");
        Console.WriteLine($"infohash  {infoHash}");

        int restored = node.Load(tablePath);
        Console.WriteLine(restored > 0
            ? $"table     {restored} nodes restored from {tablePath}"
            : "table     empty, starting from the bootstrap routers");
        Console.WriteLine();

        Stopwatch clock = Stopwatch.StartNew();

        int known = await node.BootstrapAsync(DhtNode.DefaultRouters, stop.Token).ConfigureAwait(false);
        long bootstrapped = clock.ElapsedMilliseconds;

        Console.WriteLine($"joined    {known} nodes known after {bootstrapped:N0} ms"
            + $", {node.Table.BucketCount} buckets");

        if (known == 0)
        {
            Console.Error.WriteLine("no node answered; the DHT could not be joined");
            return 1;
        }

        clock.Restart();
        IReadOnlyList<IPEndPoint> peers = await node.FindPeersAsync(infoHash, stop.Token).ConfigureAwait(false);
        long lookup = clock.ElapsedMilliseconds;

        Console.WriteLine($"lookup    {peers.Count} peers in {lookup:N0} ms, table now {node.Table.Count} nodes");

        foreach (IPEndPoint peer in peers.Take(10))
        {
            Console.WriteLine($"          {peer}");
        }

        if (peers.Count > 10)
        {
            Console.WriteLine($"          ... and {peers.Count - 10} more");
        }

        node.Save(tablePath);
        Console.WriteLine();
        Console.WriteLine($"saved     {node.Table.Count} nodes to {tablePath}");

        await stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await running.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Ending by cancellation.
        }

        return peers.Count > 0 ? 0 : 1;
    }
}
