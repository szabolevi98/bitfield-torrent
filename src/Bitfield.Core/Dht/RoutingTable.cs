namespace Bitfield.Core.Dht;

/// <summary>
/// The nodes this client knows, arranged the way Kademlia wants them: many in
/// its own neighbourhood and progressively fewer further away.
///
/// The table is a list of buckets, one per leading bit of the distance from
/// this client's own id. A node sharing no leading bits — half the network —
/// goes in the first bucket, one sharing exactly one bit in the second, and so
/// on, so a table of a few hundred entries covers a network of millions with
/// enough detail near the client to answer questions about it precisely.
///
/// Each bucket holds eight nodes and prefers the ones that have been there
/// longest: a node that has answered for an hour is likelier to answer again
/// than one that turned up a minute ago, and the rule also means a flood of new
/// nodes cannot push out a table that works.
/// </summary>
public sealed class RoutingTable
{
    /// <summary>Nodes per bucket. Eight is what the specification says.</summary>
    public const int BucketSize = 8;

    private readonly Lock _gate = new();
    private readonly List<Bucket> _buckets;

    public RoutingTable(NodeId ownId)
    {
        OwnId = ownId;
        _buckets = [new Bucket()];
    }

    public NodeId OwnId { get; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Sum(bucket => bucket.Nodes.Count);
            }
        }
    }

    public int BucketCount
    {
        get
        {
            lock (_gate)
            {
                return _buckets.Count;
            }
        }
    }

    /// <summary>
    /// Remembers a node that has just been heard from. Returns false when its
    /// bucket is full of nodes that are doing fine — there is nothing to be
    /// gained by displacing them, and the specification says as much.
    /// </summary>
    public bool Add(DhtContact contact)
    {
        if (contact.Id == OwnId)
        {
            return false;
        }

        lock (_gate)
        {
            int index = IndexOf(contact.Id);
            Bucket bucket = _buckets[index];

            int existing = bucket.Nodes.FindIndex(node => node.Contact.Id == contact.Id);
            if (existing >= 0)
            {
                // Seen again: it moves to the back as the most recently heard
                // from, and its failures are forgiven.
                Entry entry = bucket.Nodes[existing];
                bucket.Nodes.RemoveAt(existing);
                bucket.Nodes.Add(entry with { Contact = contact, LastSeen = DateTime.UtcNow, Failures = 0 });
                return true;
            }

            if (bucket.Nodes.Count < BucketSize)
            {
                bucket.Nodes.Add(new Entry(contact, DateTime.UtcNow, 0));
                return true;
            }

            // A bucket that has gone bad makes room; one full of working nodes
            // does not.
            int worst = bucket.Nodes.FindIndex(node => node.Failures >= 3);
            if (worst >= 0)
            {
                bucket.Nodes.RemoveAt(worst);
                bucket.Nodes.Add(new Entry(contact, DateTime.UtcNow, 0));
                return true;
            }

            // Only the bucket this client's own id falls in may be split, which
            // is what keeps the table detailed nearby and coarse far away.
            if (index == _buckets.Count - 1 && _buckets.Count < NodeId.Size * 8)
            {
                Split();
                return Add(contact);
            }

            return false;
        }
    }

    /// <summary>Records that a node did not answer.</summary>
    public void Failed(NodeId id)
    {
        lock (_gate)
        {
            Bucket bucket = _buckets[IndexOf(id)];
            int index = bucket.Nodes.FindIndex(node => node.Contact.Id == id);

            if (index >= 0)
            {
                Entry entry = bucket.Nodes[index];
                bucket.Nodes[index] = entry with { Failures = entry.Failures + 1 };

                // Three strikes. A node that has stopped answering is worse
                // than an empty slot, because a lookup spends a timeout on it.
                if (bucket.Nodes[index].Failures >= 3)
                {
                    bucket.Nodes.RemoveAt(index);
                }
            }
        }
    }

    /// <summary>The nodes nearest a target, which is what a lookup starts from.</summary>
    public IReadOnlyList<DhtContact> Closest(NodeId target, int count = BucketSize)
    {
        lock (_gate)
        {
            return
            [
                .. _buckets
                    .SelectMany(bucket => bucket.Nodes)
                    .Select(entry => entry.Contact)
                    .Order(new ContactDistanceComparer(target))
                    .Take(count),
            ];
        }
    }

    public IReadOnlyList<DhtContact> All()
    {
        lock (_gate)
        {
            return [.. _buckets.SelectMany(bucket => bucket.Nodes).Select(entry => entry.Contact)];
        }
    }

    /// <summary>
    /// Splits the last bucket in two, moving out the nodes that no longer
    /// belong. Only ever the last, because that is the one containing this
    /// client's own id.
    /// </summary>
    private void Split()
    {
        Bucket last = _buckets[^1];
        Bucket added = new();
        _buckets.Add(added);

        int depth = _buckets.Count - 2;

        Entry[] nodes = [.. last.Nodes];
        last.Nodes.Clear();

        foreach (Entry entry in nodes)
        {
            Bucket destination = OwnId.CommonPrefixLength(entry.Contact.Id) > depth ? added : last;
            if (destination.Nodes.Count < BucketSize)
            {
                destination.Nodes.Add(entry);
            }
        }
    }

    private int IndexOf(NodeId id) => Math.Min(OwnId.CommonPrefixLength(id), _buckets.Count - 1);

    private sealed record Entry(DhtContact Contact, DateTime LastSeen, int Failures);

    private sealed class Bucket
    {
        /// <summary>Oldest first, which is the order the eviction rule needs.</summary>
        public List<Entry> Nodes { get; } = [];
    }

    private sealed class ContactDistanceComparer(NodeId target) : IComparer<DhtContact>
    {
        public int Compare(DhtContact? left, DhtContact? right) =>
            NodeId.CompareDistance(target, left!.Id, right!.Id);
    }
}
