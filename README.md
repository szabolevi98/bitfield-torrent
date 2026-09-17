# Bitfield Torrent

A BitTorrent client written in C#, built from the protocol up: bencode, the peer
wire protocol, piece selection and the choking algorithm, with a Kademlia DHT
underneath for finding peers when there is no tracker to ask.

The name is the protocol's own: `bitfield` is the message a peer sends to say
which pieces it already has. It is also what the window is built around — the
piece map in the middle of the screen is a bitfield drawn out, filling in as the
download proceeds.

![Bitfield Torrent downloading a Debian ISO](docs/screenshot.png)

Three torrents — one downloading the Debian netinst ISO at 15.6 MB/s across 31
peers, one paused, one stalled. The speckle in the piece map is what rarest
first looks like: pieces arriving from all over the torrent rather than in
order.

## What it does

- **Torrents from files or magnet links.** A magnet link is an infohash and some
  hints; the description is fetched from the swarm and has to hash to the
  infohash before a byte of it is believed.
- **Trackers over HTTP and UDP**, in tiers, with `started`, `stopped` and
  `completed` announced when they are actually true.
- **A DHT** — Kademlia routing table, `get_peers` and `announce_peer` — so a
  torrent with no tracker still finds peers. Torrents carrying `private: 1` keep
  it off, along with peer exchange and local discovery. That flag is not
  advisory.
- **Dozens of peers at once**, pieces picked rarest first with endgame
  duplication, requests pipelined per peer and adapted to what each one keeps up
  with.
- **Uploading**, with tit-for-tat choking and a rotating optimistic unchoke,
  behind a listening port the router is asked to forward over UPnP.
- **Several torrents**, listed with progress and status, sortable by any column,
  added and removed with or without their content, and back where they were when
  the client starts again — seeding included.
- **Pause and resume without hashing a byte**, and **file priorities** including
  *do not download*: a torrent is finished when every piece it still wants is
  held, not when every piece is.
- **Storage that respects file boundaries** — pieces written across the files
  they straddle, sparse preallocation, and nothing written until its SHA-1
  matches.
- **A window that is not the client.** It lives in the notification area, so
  closing it stops nothing; a second launch hands its torrent to the copy
  already running rather than starting a rival for the same port and the same
  files.

## Measured, not claimed

Nothing here is claimed as working without a measurement beside it.

**454 offline checks pass** on every build: bencode both ways, the metainfo
model, the announce request down to its exact bytes, every shape a tracker reply
arrives in, the handshake and every wire message, the piece picker, the DHT's
distance arithmetic, routing table and messages, and pieces written across file
boundaries. Three real torrent files have their infohashes, piece counts and
lengths matched against values derived with a separate implementation.

Two of those checks are whole networks on loopback. Eight DHT nodes, where one
announces a torrent and another that has never heard of it looks the torrent up
and is told where to find it. And a swarm of this client talking to itself: a
seed and two leechers, and then a third leecher that finishes **with the seed
removed from the swarm entirely**, served only by the two peers that had just
downloaded it themselves. That needs the uploading side, the choking algorithm
and incoming connections all to be right at once. It takes about a second.

Against the live swarm, on 2026-09-17:

| What | Result |
|---|---|
| **The Debian 13.7 netinst ISO, start to finish** | **792,723,456 bytes in 49.1 s — 15.39 MB/s, 28 peers, no piece failed its hash** |
| **Its SHA-256 against Debian's published `SHA256SUMS`** | **`a7ef94ac…e355` — matches** |
| **The same ISO from a magnet link — the hash and nothing else** | **description fetched from a stranger in 726 ms, then all 792,723,456 bytes, SHA-256 matching again** |
| **The same ISO's peers from the real DHT, no tracker at all** | **joined in 41 s, then 100 peers in 12.3 s** |
| **Seeding it with the port forwarded** | **4,276,224 bytes taken by a real peer in four minutes** |
| Resuming after 12 pieces were damaged | hashing found exactly those 12 in 2.6 s and re-fetched 12 pieces to the byte, checksum matching |
| The same ISO over HTTP from Debian's mirror, one stream | 18.32 MB/s — this client reaches about 85% of it |

The live runs are not part of the build, because they depend on somebody else's
swarm being up:

```
dotnet run --project tests/Bitfield.Tests -- announce [path to a .torrent]
dotnet run --project tests/Bitfield.Tests -- download <directory> [.torrent] [expected sha-256]
```

## Building

Needs the .NET 9 SDK.

```
dotnet build Bitfield.sln
dotnet run --project tests/Bitfield.Tests
```

## Layout

```
src/Bitfield.Core    protocol, piece selection, storage, DHT
src/Bitfield         the Windows Forms application
tests/Bitfield.Tests offline checks
```

## Notes

### What rarest first did and did not do

Rarest first and adaptive pipelining went in expecting the download to get
faster. It did not: 49.1 s against the 48.0 s the same ISO took when pieces were
picked in order with a fixed sixteen requests outstanding. The same file over
HTTP from Debian's own mirror comes down at 18.32 MB/s, and this client reaches
about 85% of that — so what limits it is the line, not the order the pieces are
asked for.

The change is doing its job; it is simply not the thing in the way. Adaptive
pipelining is visible in the run: the fastest peer was being kept waiting on 73
blocks at once, against the flat sixteen it would have had before. And rarest
first earns its place for a different reason than speed — it stops a torrent
needing, at 99%, a piece only one departed peer ever had. Neither shows up in
the time to fetch a well-seeded Debian ISO.

### The missing port, and what happened when it was opened

Seeding the finished ISO to the public swarm for ten minutes uploaded nothing at
all, and counting what the peers were said why: every one that got as far as
sending its bitfield held the whole torrent already — nine of nine. A heavily
seeded torrent has few leechers, those leechers dial out to seeds rather than
waiting to be dialled, and this client was not listening anywhere they could
reach.

With the UPnP mapping in, that could be tested rather than argued. Same ISO,
same four-minute window, port forwarded: one peer that was not a seed arrived,
was unchoked, and took **4,276,224 bytes**.

### The routing table is worth nothing without its id

The DHT's buckets are cut by distance from this node's own id, so a node that
comes back under a fresh id has a table sorted for somebody else. Measuring it
made that concrete: of 81 saved nodes, 16 survived being restored. Saving the id
alongside them takes the figure to all of them, and the bootstrap routers —
which carry the first question of every client on the network — can then be left
alone entirely. The other half of the same point is that other nodes' tables go
on pointing at the id this one had last time.

### What a tracker is told, and when

A download that ended used to announce `completed` whatever had actually
happened — including a torrent cancelled at forty per cent. On a tracker that
keeps ratios that is a client claiming to have finished something it did not.
It now announces `stopped` whenever it leaves a swarm, and `completed` exactly
once: at the moment the piece that finishes the torrent verifies, and only if it
did not start finished.

### Why the info dictionary is kept as bytes

A torrent's identity is the SHA-1 of its info dictionary, taken over the bytes
as they appear in the file. Parsing that dictionary and hashing a re-encoding of
it looks equivalent and is not: a file may carry keys this client has never
heard of, or carry them in an order it would not have chosen, and either
difference produces a hash that no peer and no tracker recognises. So the parser
records the byte range of every value it reads, and the infohash is taken
straight from the file. The checks hold it to that from both sides — every real
torrent file re-encodes to the bytes it came from, and a synthetic torrent with
unsorted and unknown keys keeps its own identity rather than a tidied one.

### Why the finished file is checked against somebody else's number

Every piece is checked against a hash from the torrent file, so a download that
completes is self-consistent by construction — which means it proves nothing on
its own. The measurement that counts is the SHA-256 of the finished ISO against
the one Debian publishes: made by someone else, about the same bytes, and
nothing in this repository can influence it.

## License

MIT. Demonstrations use freely distributable torrents — Linux distributions and
Internet Archive material.
