# Fixtures

Real torrent files, kept here rather than downloaded during a test run, because
the checks that use them state exact values and those values have to stay
reachable after the releases they point at are retired.

| File | Source | Why it is here |
|---|---|---|
| `debian-13.7.0-amd64-netinst.iso.torrent` | [cdimage.debian.org](https://cdimage.debian.org/debian-cd/current/amd64/bt-cd/) | Single file, a few thousand pieces, several tracker tiers |
| `ubuntu-24.04.4-desktop-amd64.iso.torrent` | [releases.ubuntu.com](https://releases.ubuntu.com/24.04/) | Single file, 25,390 pieces — half a megabyte of piece hashes |
| `popeye-meets-sindbad_archive.torrent` | [Internet Archive](https://archive.org/details/PopeyeTheSailorMeetsSindbadTheSailor) | Multiple files, web seeds, and a path list to join |

All three are freely distributable: two Linux installation images and one public
domain film.

The expected values in `TorrentFileTests.cs` — infohashes, piece counts, lengths
— were produced by a separate bencode reader written for the purpose, not by the
code under test. Deriving them from this parser would make the checks agree with
whatever it happened to do.

`tools/fetch-test-torrents.ps1` re-downloads current versions of the first two.
Replacing a file means the expected values change with it.
