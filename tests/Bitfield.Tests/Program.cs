// Offline checks. Everything that can be decided without a network — bencode
// round-trips, infohash values, the piece picker, the choking algorithm against
// a simulated transport — is answered here, and the exit code says whether it
// held. Tests that need real peers live in the swarm runner instead.

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

// Checks are added milestone by milestone; the first set covers bencode.
_ = (Action<string, bool, string>)Check;

Console.WriteLine();
Console.WriteLine($"{total - failures}/{total} checks passed.");
return failures == 0 ? 0 : 1;
