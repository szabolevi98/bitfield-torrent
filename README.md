# Bitfield Torrent

A BitTorrent client written in C#, built from the protocol up: bencode, the peer
wire protocol, piece selection and the choking algorithm, with a Kademlia DHT
underneath for finding peers when there is no tracker to ask.

The name is the protocol's own: `bitfield` is the message a peer sends to say
which pieces it already has. It is also what the window is built around — the
piece map in the middle of the screen is a bitfield drawn out, filling in as the
download proceeds.

## Status

Milestone 2 of 9. Torrent files are read and identified correctly, and real
trackers answer with real peers. Nothing is transferred yet — no peer has been
connected to. The window opens and is empty.

**196 offline checks pass**, covering the bencode reader and writer, the
metainfo model, the announce request down to its exact bytes, every shape a
tracker reply arrives in, and three real torrent files whose infohashes, piece
counts and lengths are matched against values derived with a separate
implementation.

Announcing for real, on 2026-09-17:

| Tracker | Answered in | Reported | Peers |
|---|---:|---|---:|
| `bttracker.debian.org` (HTTP) | 190 ms | swarm sizes not reported | 50 |
| `torrent.ubuntu.com` (HTTPS) | 348 ms | 1,608 seeders, 31 leechers | 1 |

```
dotnet run --project tests/Bitfield.Tests -- announce [path to a .torrent]
```

Nothing below is claimed as working until it has a measurement beside it.

## Scope

- **Metainfo and bencode** — `.torrent` parsing with the raw `info` bytes kept
  intact, because the infohash is the SHA-1 of those bytes exactly as they
  arrived, not of a re-encoding of them
- **Trackers** — HTTP announce, then UDP (BEP 15) with its connection handshake
- **Peer wire protocol** — handshake, `bitfield`/`have`, `choke`/`interested`,
  block requests in 16 KiB pieces, pipelined per peer
- **Piece selection** — random first while there is nothing to trade, rarest
  first in the middle, endgame duplication for the last few blocks
- **Choking** — tit-for-tat with four unchoke slots and a rotating optimistic
  unchoke, which is what makes the swarm work at all
- **Storage** — pieces mapped across file boundaries, sparse preallocation,
  hash verification before anything is written, resume after a restart
- **Magnet links** — the extension protocol (BEP 10) and `ut_metadata` (BEP 9),
  so a download can start from an infohash alone
- **DHT** — Kademlia routing table, `get_peers` and `announce_peer`, bootstrapped
  once and persisted afterwards

### Private torrents

Torrents carrying `private: 1` in the info dictionary are tracker-only by
design. The DHT, peer exchange and local peer discovery are all implemented, and
all three stay switched off for those torrents — that flag is not advisory.

## How it will be measured

The point of a client is that the bytes are right, so the checks are arranged to
say so rather than to look reassuring:

- **The finished file's SHA-256 matches the publisher's own checksum.** A Linux
  ISO downloaded end to end, verified against the distributor's `SHA256SUMS`.
- **Bencode round-trips byte for byte** across a corpus of real `.torrent` files,
  with the computed infohashes matching their published values.
- **A local swarm** — several instances of this client on one machine sharing a
  generated file — exercises seeding, choking and rarest-first without touching
  the public network, repeatably and fast enough to run on every build.
- **Interop in both directions** with an established client: downloading from it
  and seeding to it, which is what proves this speaks the protocol rather than a
  private dialect.
- **Throughput, peer counts and time to first DHT peer**, recorded rather than
  estimated.

## Milestones

| # | Milestone | Done when |
|---|---|---|
| **1** | **Bencode, metainfo, infohash** | **Done — computed infohashes match real `.torrent` files** |
| **2** | **HTTP tracker announce** | **Done — a live peer list comes back** |
| 3 | One peer, one piece | A single piece downloads and its SHA-1 verifies |
| 4 | Full download, multi-file, resume | An ISO's published SHA-256 matches |
| 5 | Piece picker, pipelining, many peers | Sustained throughput on a real swarm |
| 6 | Seeding and choking | The local swarm test passes |
| 7 | UDP trackers, magnet, `ut_metadata` | A magnet link downloads from scratch |
| 8 | DHT | Peers found with no tracker involved |
| 9 | The window: piece map, peers, graphs, rate limits, UPnP | — |

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

### Why the announce URL is built by hand

The infohash and peer id go into the tracker's query string as twenty raw bytes
each, not as text. Handing them to a general-purpose URL encoder means deciding
what encoding those bytes are text in, and whichever is chosen, the bytes that
are not valid in it come back replaced rather than escaped. The result is a
different infohash, and every tracker answers that it has never heard of the
torrent. So the bytes are percent-encoded one at a time, and a check compares
the finished URL against one built by a different encoder.

### Why the info dictionary is kept as bytes

A torrent's identity is the SHA-1 of its info dictionary, taken over the bytes
as they appear in the file. Parsing that dictionary and hashing a re-encoding of
it looks equivalent and is not: a file may carry keys this client has never
heard of, or carry them in an order it would not have chosen, and either
difference produces a hash that no peer and no tracker recognises. So the parser
records the byte range of every value it reads, and the infohash is taken
straight from the file.

The checks hold it to that from both sides: every real torrent file re-encodes
to the same bytes it came from, and a synthetic torrent with unsorted and
unknown keys in its info dictionary keeps its own identity rather than a tidied
one.


## License

MIT. Demonstrations use freely distributable torrents — Linux distributions and
Internet Archive material.
