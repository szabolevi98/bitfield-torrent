using Bitfield.Controls;
using Bitfield.Core.Dht;
using Bitfield.Core.Download;
using Bitfield.Core.Peers;
using Bitfield.Core.Torrents;

namespace Bitfield;

/// <summary>
/// The window. A torrent at a time, with the piece map in the middle of it.
///
/// Everything the engine reports arrives on its own threads, so the window
/// polls a snapshot on a timer rather than being called into from them: one
/// place that reads the state and one place that draws it, which is far easier
/// to keep honest than a dozen invoked callbacks.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly PieceMapControl _map = new() { Dock = DockStyle.Fill };
    private readonly SpeedGraphControl _graph = new() { Dock = DockStyle.Top, Height = 150 };
    private readonly PeerListControl _peers = new() { Dock = DockStyle.Fill };
    private readonly StatsBar _stats = new() { Dock = DockStyle.Top };
    private readonly ListBox _log = new();
    private readonly Label _title = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    private readonly TextBox _downLimit = new();
    private readonly TextBox _upLimit = new();
    private readonly PeerId _peerId = PeerId.Generate();
    private readonly List<string> _notes = [];

    private TorrentSession? _session;
    private DhtNode? _dht;
    private PeerListener? _listener;
    private CancellationTokenSource? _background;
    private PortMapping.Mapping? _mapping;

    private long _lastDownloaded;
    private long _lastUploaded;
    private DateTime _lastSample = DateTime.UtcNow;

    private readonly string? _openOnStart;
    private readonly string? _directoryOnStart;

    public MainForm(string? open = null, string? directory = null)
    {
        _openOnStart = open;
        _directoryOnStart = directory;

        Text = "Bitfield Torrent";
        ClientSize = new Size(1180, 760);
        MinimumSize = new Size(900, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;

        BuildLayout();

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        Shown += (_, _) =>
        {
            StartBackground();
            OpenFromCommandLine();
        };
        FormClosing += (_, _) => Shutdown();
    }

    private void BuildLayout()
    {
        Panel side = new()
        {
            Dock = DockStyle.Right,
            Width = 462,
            Padding = new Padding(8, 0, 0, 0),
            BackColor = Theme.Background,
        };

        Panel peersHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(0, 8, 0, 0) };
        peersHost.Controls.Add(_peers);

        side.Controls.Add(peersHost);
        side.Controls.Add(_graph);

        Panel centre = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
        centre.Controls.Add(_map);

        Panel body = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0), BackColor = Theme.Background };
        body.Controls.Add(centre);
        body.Controls.Add(side);

        Panel statsHost = new()
        {
            Dock = DockStyle.Top,
            Height = 82,
            Padding = new Padding(12, 0, 12, 8),
            BackColor = Theme.Background,
        };
        statsHost.Controls.Add(_stats);

        Controls.Add(body);
        Controls.Add(statsHost);
        Controls.Add(BuildHeader());
        Controls.Add(BuildLog());
    }

    private Control BuildHeader()
    {
        Panel header = new() { Dock = DockStyle.Top, Height = 62, BackColor = Theme.Background, Padding = new Padding(12, 10, 12, 0) };

        _title.Text = "No torrent open";
        _title.ForeColor = Theme.TextSecondary;
        _title.Font = new Font("Segoe UI", 11F);
        _title.AutoSize = true;
        _title.Location = new Point(262, 14);

        header.Controls.Add(_title);
        header.Controls.Add(Button("Open torrent…", 12, OpenTorrent));
        header.Controls.Add(Button("Magnet link…", 132, OpenMagnet));
        header.Controls.Add(Limit(_downLimit, "down KB/s", 0));
        header.Controls.Add(Limit(_upLimit, "up KB/s", 150));

        return header;
    }

    private Control BuildLog()
    {
        _log.Dock = DockStyle.Bottom;
        _log.Height = 92;
        _log.BackColor = Theme.Surface;
        _log.ForeColor = Theme.TextSecondary;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = Theme.CaptionFont;
        _log.IntegralHeight = false;

        Panel host = new() { Dock = DockStyle.Bottom, Height = 104, Padding = new Padding(12, 8, 12, 12), BackColor = Theme.Background };
        host.Controls.Add(_log);
        return host;
    }

    /// <summary>
    /// A rate limit box. Zero means no limit, which is what both start at —
    /// throttling a client by default would be a surprise, and the number is
    /// there for the times a download is in the way of something else.
    /// </summary>
    private Control Limit(TextBox box, string caption, int offsetFromRight)
    {
        Panel panel = new()
        {
            Size = new Size(96, 42),
            BackColor = Theme.Background,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        panel.Location = new Point(ClientSize.Width - 210 + offsetFromRight, 6);

        Label label = new()
        {
            Text = caption,
            ForeColor = Theme.TextMuted,
            Font = Theme.CaptionFont,
            AutoSize = true,
            Location = new Point(2, 0),
        };

        box.Text = "0";
        box.Location = new Point(0, 15);
        box.Width = 88;
        box.BackColor = Theme.Surface;
        box.ForeColor = Theme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = Theme.UiFont;
        box.TextChanged += (_, _) => ApplyLimits();

        panel.Controls.Add(label);
        panel.Controls.Add(box);
        return panel;
    }

    private void ApplyLimits()
    {
        TorrentDownload? download = _session?.Download;
        if (download == null)
        {
            return;
        }

        download.DownloadLimit.BytesPerSecond = Kilobytes(_downLimit.Text);
        download.UploadLimit.BytesPerSecond = Kilobytes(_upLimit.Text);
    }

    private static long Kilobytes(string text) =>
        long.TryParse(text.Trim(), out long value) && value > 0 ? value * 1024 : 0;

    private Button Button(string text, int x, Action onClick)
    {
        Button button = new()
        {
            Text = text,
            Location = new Point(x, 8),
            Size = new Size(112, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            Cursor = Cursors.Hand,
        };

        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.SurfaceHover;
        button.FlatAppearance.MouseDownBackColor = Theme.Selection;
        button.Click += (_, _) => onClick();

        return button;
    }

    // ----------------------------------------------------------- the engine

    private void StartBackground()
    {
        _background = new CancellationTokenSource();

        // A DHT node and a listening port are what make this client a member of
        // a swarm rather than a visitor, so both come up with the window.
        _dht = new DhtNode(DhtNode.SavedId(TablePath()), port: 6881);
        _dht.Load(TablePath());
        _ = Task.Run(() => _dht.RunAsync(_background.Token), CancellationToken.None);

        _listener = new PeerListener(6881, infoHash => _session?.Download.InfoHash == infoHash ? _session.Download : null);
        _ = Task.Run(() => _listener.RunAsync(_background.Token), CancellationToken.None);

        Note($"listening on {_listener.Port}, DHT on {_dht.Port}");

        // Without a forwarded port this client can reach out but not be
        // reached, and in a well seeded swarm the peers with anything to gain
        // are the ones dialling out.
        _ = Task.Run(async () =>
        {
            PortMapping.Mapping? mapping = await PortMapping
                .AddAsync(6881, "Bitfield Torrent", _background.Token).ConfigureAwait(false);

            _mapping = mapping;
            Note(mapping != null
                ? $"the router {mapping}"
                : "no router would forward port 6881; peers can still be dialled out to");
        }, CancellationToken.None);

        _ = Task.Run(async () =>
        {
            int nodes = await _dht.BootstrapAsync(DhtNode.DefaultRouters, _background.Token).ConfigureAwait(false);
            Note($"the DHT has {nodes} nodes");
        }, CancellationToken.None);
    }

    private void OpenFromCommandLine()
    {
        if (_openOnStart == null)
        {
            return;
        }

        try
        {
            if (MagnetLink.TryParse(_openOnStart, out MagnetLink? link, out _))
            {
                ResolveAndStart(link!);
                return;
            }

            Start(Metainfo.Load(_openOnStart), _directoryOnStart);
        }
        catch (Exception e)
        {
            Note($"could not open {_openOnStart}: {e.Message}");
        }
    }

    private void OpenTorrent()
    {
        using OpenFileDialog dialog = new()
        {
            Filter = "Torrent files (*.torrent)|*.torrent|All files (*.*)|*.*",
            Title = "Open a torrent",
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        try
        {
            Start(Metainfo.Load(dialog.FileName));
        }
        catch (Exception e)
        {
            Note($"could not read that file: {e.Message}");
        }
    }

    private void OpenMagnet()
    {
        string? text = Prompt("Paste a magnet link");
        if (text == null)
        {
            return;
        }

        if (!MagnetLink.TryParse(text.Trim(), out MagnetLink? link, out string? error))
        {
            Note($"not a usable magnet link: {error}");
            return;
        }

        ResolveAndStart(link!);
    }

    private void ResolveAndStart(MagnetLink link)
    {
        Note($"asking the swarm about {link.InfoHash}");

        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<System.Net.IPEndPoint> fromDht = _dht == null
                    ? []
                    : await _dht.FindPeersAsync(link.InfoHash, _background!.Token).ConfigureAwait(false);

                Metainfo torrent = await MagnetResolver.ResolveAsync(
                    link, _peerId, 6881, fromDht, Note, _background!.Token).ConfigureAwait(false);

                BeginInvoke(() => Start(torrent));
            }
            catch (Exception e)
            {
                Note($"the description could not be fetched: {e.Message}");
            }
        }, CancellationToken.None);
    }

    private void Start(Metainfo torrent, string? into = null)
    {
        string directory;

        if (into != null)
        {
            directory = into;
        }
        else
        {
            using FolderBrowserDialog folder = new() { Description = $"Where should \"{torrent.Name}\" go?" };
            if (folder.ShowDialog(this) != DialogResult.OK)
            {
                return;
            }

            directory = folder.SelectedPath;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_session != null)
                {
                    await _session.DisposeAsync().ConfigureAwait(false);
                    _session = null;
                }

                TorrentSession session = await TorrentSession.OpenAsync(
                    torrent, directory, _peerId, _dht, Note, null, _background!.Token).ConfigureAwait(false);

                _session = session;
                BeginInvoke(ApplyLimits);

                BeginInvoke(() =>
                {
                    _title.Text = torrent.Name;
                    _title.ForeColor = Theme.TextPrimary;
                    _graph.Clear();
                });

                Note($"{torrent.Name} — {Theme.Bytes(torrent.TotalLength)} in {torrent.PieceCount:N0} pieces"
                    + (torrent.IsPrivate ? ", private, so the DHT stays out of it" : ""));
            }
            catch (Exception e)
            {
                Note($"could not open that torrent: {e.Message}");
            }
        }, CancellationToken.None);
    }

    // -------------------------------------------------------------- drawing

    /// <summary>
    /// Reads one snapshot of the engine and draws it. Everything the engine
    /// does happens on its own threads; this is the only place that looks.
    /// </summary>
    private void OnTick()
    {
        TorrentSession? session = _session;

        if (session == null)
        {
            _stats.Set(
                ("status", "idle", Theme.TextMuted),
                ("dht", $"{_dht?.Table.Count ?? 0:N0}", Theme.TextSecondary));
            return;
        }

        DownloadProgress progress = session.Download.Snapshot();

        _map.SetPieces(
            progress.PieceCount,
            session.Download.Have.Span,
            session.Download.PiecesInProgress());

        double seconds = Math.Max((DateTime.UtcNow - _lastSample).TotalSeconds, 0.001);
        long downloaded = session.Download.Downloaded;
        long uploaded = session.Download.Uploaded;

        double down = Math.Max(0, (downloaded - _lastDownloaded) / seconds);
        double up = Math.Max(0, (uploaded - _lastUploaded) / seconds);

        _lastDownloaded = downloaded;
        _lastUploaded = uploaded;
        _lastSample = DateTime.UtcNow;

        _graph.Add(down, up);

        long remaining = session.Torrent.TotalLength - downloaded;
        string eta = progress.PiecesHeld == progress.PieceCount
            ? "seeding"
            : down > 1 ? Theme.Duration(TimeSpan.FromSeconds(remaining / down)) : "—";

        _stats.Set(
            ("progress", $"{progress.Fraction * 100:N1}%", progress.Fraction >= 1 ? Theme.Upload : Theme.Accent),
            ("down", Theme.Rate(down), Theme.Accent),
            ("up", Theme.Rate(up), Theme.Upload),
            ("peers", $"{progress.ConnectedPeers:N0}", Theme.TextPrimary),
            ("remaining", eta, Theme.TextSecondary),
            ("bad pieces", $"{progress.FailedPieces:N0}", progress.FailedPieces > 0 ? Theme.Warning : Theme.TextMuted));

        _peers.Set(session.Download.Peers().Select(peer => new PeerRow(
            peer.RemoteEndPoint.ToString(),
            peer.ClientName,
            peer.BytesPerSecond,
            peer.UploadBytesPerSecond,
            peer.State.Available.SetCount,
            progress.PieceCount,
            peer.State.ChokedByPeer,
            peer.State.PeerInterested)));
    }

    private void Note(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Note(text));
            return;
        }

        _notes.Add($"{DateTime.Now:HH:mm:ss}  {text}");

        while (_notes.Count > 200)
        {
            _notes.RemoveAt(0);
        }

        _log.BeginUpdate();
        _log.Items.Clear();
        _log.Items.AddRange([.. _notes.AsEnumerable().Reverse()]);
        _log.EndUpdate();
    }

    private string? Prompt(string caption)
    {
        using Form dialog = new()
        {
            Text = caption,
            ClientSize = new Size(540, 108),
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            BackColor = Theme.Background,
            MinimizeBox = false,
            MaximizeBox = false,
        };

        TextBox input = new()
        {
            Location = new Point(14, 18),
            Width = 512,
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.UiFont,
        };

        Button ok = new() { Text = "Open", DialogResult = DialogResult.OK, Location = new Point(414, 58), Size = new Size(112, 30), FlatStyle = FlatStyle.Flat, BackColor = Theme.SurfaceRaised, ForeColor = Theme.TextPrimary };
        ok.FlatAppearance.BorderColor = Theme.Border;

        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.AcceptButton = ok;

        return dialog.ShowDialog(this) == DialogResult.OK && input.Text.Length > 0 ? input.Text : null;
    }

    private static string TablePath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bitfield",
        "dht.table");

    private void Shutdown()
    {
        _tick.Stop();

        try
        {
            _dht?.Save(TablePath());
        }
        catch (IOException)
        {
            // Not worth holding up a window that is closing.
        }

        if (_mapping is { } mapping)
        {
            // Router mapping tables are small, and a client that leaves one
            // behind per run eventually fills one.
            try
            {
                PortMapping.RemoveAsync(mapping).Wait(TimeSpan.FromSeconds(3));
            }
            catch (Exception)
            {
                // A router that will not take it back is not worth waiting on.
            }
        }

        _background?.Cancel();
        _listener?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        _session?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(5));
        _dht?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
    }
}
