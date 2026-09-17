using System.Diagnostics;
using Bitfield.Core.Download;

namespace Bitfield.Tests;

/// <summary>
/// Asks the router to forward a port and then takes it back, so the exchange
/// can be checked without leaving anything behind.
///
///     dotnet run --project tests/Bitfield.Tests -- upnp [port]
/// </summary>
internal static class LiveUpnpRunner
{
    public static int Run(string[] args)
    {
        int port = args.Length > 1 && int.TryParse(args[1], out int parsed) ? parsed : 6881;

        Stopwatch clock = Stopwatch.StartNew();
        PortMapping.Mapping? mapping = PortMapping
            .AddAsync(port, "Bitfield Torrent (test)")
            .GetAwaiter().GetResult();

        clock.Stop();

        if (mapping == null)
        {
            Console.WriteLine($"no router forwarded port {port} after {clock.ElapsedMilliseconds:N0} ms");
            return 1;
        }

        Console.WriteLine($"mapped    {mapping} in {clock.ElapsedMilliseconds:N0} ms");
        Console.WriteLine($"control   {mapping.Control}");
        Console.WriteLine($"service   {mapping.ServiceType}");

        PortMapping.RemoveAsync(mapping).GetAwaiter().GetResult();
        Console.WriteLine("removed   the mapping again, nothing left behind");

        return 0;
    }
}
