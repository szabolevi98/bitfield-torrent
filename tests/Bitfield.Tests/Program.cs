// Offline checks. Everything that can be decided without a network — bencode
// round-trips, infohash values, and later the piece picker and the choking
// algorithm against a simulated transport — is answered here, and the exit code
// says whether it held. Checks that need real peers live in the swarm runner.

using Bitfield.Tests;

// The runners need a network, so they are asked for by name rather than run as
// part of the build's own checks.
if (args.Length > 0)
{
    return args[0] switch
    {
        "piece" => LivePieceRunner.Run(args),
        "download" => LiveDownloadRunner.Run(args),
        "seed" => LiveSeedRunner.Run(args),
        "magnet" => LiveMagnetRunner.Run(args),
        "dht" => LiveDhtRunner.Run(args),
        _ => LiveTrackerRunner.Run(args),
    };
}

int failures = 0;
int total = 0;

void Check(string name, bool condition, string detail = "")
{
    total++;
    if (condition)
    {
        Console.WriteLine($"PASS  {name}");
    }
    else
    {
        failures++;
        Console.WriteLine($"FAIL  {name}  {detail}");
    }
}

BencodeTests.Run((name, pass, detail) => Check(name, pass, detail));
MetainfoTests.Run((name, pass, detail) => Check(name, pass, detail));
TorrentFileTests.Run((name, pass, detail) => Check(name, pass, detail));
TrackerTests.Run((name, pass, detail) => Check(name, pass, detail));
PeerProtocolTests.Run((name, pass, detail) => Check(name, pass, detail));
StorageTests.Run((name, pass, detail) => Check(name, pass, detail));
PickerTests.Run((name, pass, detail) => Check(name, pass, detail));
MagnetTests.Run((name, pass, detail) => Check(name, pass, detail));
DhtTests.Run((name, pass, detail) => Check(name, pass, detail));
SwarmTests.Run((name, pass, detail) => Check(name, pass, detail));

Console.WriteLine();
Console.WriteLine($"{total - failures}/{total} checks passed.");
return failures == 0 ? 0 : 1;
