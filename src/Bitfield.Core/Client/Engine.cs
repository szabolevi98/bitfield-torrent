using System.Collections.Concurrent;
using Bitfield.Core.Dht;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Storage;
using Bitfield.Core.Torrents;

namespace Bitfield.Core.Client;

/// <summary>
/// The client itself: the torrents, the DHT node, the listening port and the
/// limits they all share.
///
/// It is deliberately not the window. A torrent client spends most of its life
/// with nobody looking at it, and anything the window owns is something that
/// has to stay open for the downloads to keep running — so the window becomes a
/// view onto this rather than the thing itself.
/// </summary>
public sealed class Engine : IAsyncDisposable
{
    private readonly ConcurrentDictionary<InfoHash, TorrentSession> _torrents = new();
    private readonly CancellationTokenSource _stop = new();

    private DhtNode? _dht;
    private PeerListener? _listener;
    private PortMapping.Mapping? _mapping;

    public Engine(TorrentStore? store = null)
    {
        Store = store ?? new TorrentStore();
        PeerId = PeerId.Generate();
    }

    public TorrentStore Store { get; }

    public PeerId PeerId { get; }

    public int Port { get; init; } = 6881;

    public RateLimiter DownloadLimit { get; } = new();

    public RateLimiter UploadLimit { get; } = new();

    public PeerBudget Budget { get; } = new(200);

    public DhtNode? Dht => _dht;

    public IReadOnlyList<TorrentSession> Torrents => [.. _torrents.Values.OrderBy(t => t.State.AddedOn)];

    public event Action<string>? Note;

    /// <summary>Raised when a torrent is added or removed, not on every byte.</summary>
    public event Action? Changed;

    /// <summary>
    /// Brings up everything that is shared, then puts back the torrents that
    /// were running when the client last closed.
    /// </summary>
    public async Task StartAsync()
    {
        _dht = new DhtNode(DhtNode.SavedId(Store.DhtTablePath), Port) { PeerPort = Port };
        _dht.Load(Store.DhtTablePath);
        _ = Task.Run(() => _dht.RunAsync(_stop.Token), CancellationToken.None);

        _listener = new PeerListener(Port, infoHash =>
            _torrents.TryGetValue(infoHash, out TorrentSession? session) ? session.Download : null);

        _ = Task.Run(() => _listener.RunAsync(_stop.Token), CancellationToken.None);

        Note?.Invoke($"listening on {_listener.Port}, DHT on {_dht.Port}");

        _ = Task.Run(async () =>
        {
            _mapping = await PortMapping.AddAsync(Port, "Bitfield Torrent", _stop.Token).ConfigureAwait(false);
            Note?.Invoke(_mapping != null
                ? $"the router {_mapping}"
                : $"no router would forward port {Port}; peers can still be dialled out to");
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            int nodes = await _dht.BootstrapAsync(DhtNode.DefaultRouters, _stop.Token).ConfigureAwait(false);
            Note?.Invoke($"the DHT has {nodes} nodes");
        }, CancellationToken.None);

        foreach (StoredTorrent stored in Store.Load(text => Note?.Invoke(text)))
        {
            Start(stored.Torrent, stored.State);
        }

        if (!_torrents.IsEmpty)
        {
            Note?.Invoke($"put back {_torrents.Count} torrents from last time");
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    /// <summary>
    /// Adds a torrent. Returns null when it is already running, because adding
    /// the same infohash twice would have two downloads writing the same files.
    /// </summary>
    public TorrentSession? Add(Metainfo torrent, string directory)
    {
        if (_torrents.ContainsKey(torrent.InfoHash))
        {
            Note?.Invoke($"{torrent.Name} is already here");
            return null;
        }

        TorrentState state = new() { SavePath = directory };
        Store.Save(torrent, state);

        TorrentSession session = Start(torrent, state);
        Note?.Invoke($"added {torrent.Name} — {torrent.TotalLength:N0} bytes into {directory}");

        return session;
    }

    private TorrentSession Start(Metainfo torrent, TorrentState state)
    {
        EngineServices services = new()
        {
            PeerId = PeerId,
            Port = Port,
            Dht = _dht,
            DownloadLimit = DownloadLimit,
            UploadLimit = UploadLimit,
            Budget = Budget,
        };

        TorrentSession session = TorrentSession.Open(torrent, state, Store, services, text => Note?.Invoke(text));
        _torrents[torrent.InfoHash] = session;

        Changed?.Invoke();
        return session;
    }

    /// <summary>
    /// Stops a torrent and forgets it. With <paramref name="deleteFiles"/> its
    /// content goes too — only the files the torrent itself lists, and only the
    /// directories that are empty once those are gone, because the save folder
    /// may well hold things this client never put there.
    /// </summary>
    public async Task RemoveAsync(InfoHash infoHash, bool deleteFiles)
    {
        if (!_torrents.TryRemove(infoHash, out TorrentSession? session))
        {
            return;
        }

        string name = session.Name;
        IReadOnlyList<StoredFile> files = [.. session.Storage.Files];

        await session.DisposeAsync().ConfigureAwait(false);
        Store.Remove(infoHash);

        if (deleteFiles)
        {
            DeleteContent(files, session.State.SavePath, name);
        }

        Note?.Invoke(deleteFiles ? $"removed {name} and its files" : $"removed {name}, files left alone");
        Changed?.Invoke();
    }

    private void DeleteContent(IReadOnlyList<StoredFile> files, string savePath, string name)
    {
        HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);

        foreach (StoredFile file in files)
        {
            try
            {
                if (File.Exists(file.FullPath))
                {
                    File.Delete(file.FullPath);
                }

                if (Path.GetDirectoryName(file.FullPath) is { Length: > 0 } directory)
                {
                    directories.Add(directory);
                }
            }
            catch (IOException e)
            {
                Note?.Invoke($"{Path.GetFileName(file.FullPath)} could not be deleted: {e.Message}");
            }
        }

        // Deepest first, so that a directory emptied by the one below it is
        // itself considered. Nothing outside the save folder is touched, and
        // only empty directories go.
        string root = Path.GetFullPath(savePath);

        foreach (string directory in directories.OrderByDescending(d => d.Length))
        {
            string current = directory;

            while (current.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                && !current.Equals(root, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    if (Directory.Exists(current) && !Directory.EnumerateFileSystemEntries(current).Any())
                    {
                        Directory.Delete(current);
                    }
                    else
                    {
                        break;
                    }
                }
                catch (IOException)
                {
                    break;
                }

                current = Path.GetDirectoryName(current) ?? root;
            }
        }
    }

    /// <summary>Stops a torrent, or starts it again, and remembers which.</summary>
    public void SetPaused(InfoHash infoHash, bool paused)
    {
        if (Find(infoHash) is not { } session)
        {
            return;
        }

        if (paused)
        {
            session.Pause();
        }
        else
        {
            session.Resume();
        }

        Changed?.Invoke();
    }

    /// <summary>Changes what a torrent wants, file by file.</summary>
    public void SetPriorities(InfoHash infoHash, IReadOnlyList<FilePriority> priorities)
    {
        Find(infoHash)?.SetPriorities(priorities);
        Changed?.Invoke();
    }

    public TorrentSession? Find(InfoHash infoHash) =>
        _torrents.TryGetValue(infoHash, out TorrentSession? session) ? session : null;

    public async ValueTask DisposeAsync()
    {
        try
        {
            _dht?.Save(Store.DhtTablePath);
        }
        catch (IOException)
        {
            // Not worth holding up a client that is closing.
        }

        if (_mapping is { } mapping)
        {
            try
            {
                await PortMapping.RemoveAsync(mapping).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // A router that will not take it back is not worth waiting on.
            }
        }

        await _stop.CancelAsync().ConfigureAwait(false);

        foreach (TorrentSession session in _torrents.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _torrents.Clear();

        if (_listener != null)
        {
            await _listener.DisposeAsync().ConfigureAwait(false);
        }

        if (_dht != null)
        {
            await _dht.DisposeAsync().ConfigureAwait(false);
        }

        _stop.Dispose();
    }
}
