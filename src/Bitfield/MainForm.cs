using Bitfield.Controls;
using Bitfield.Core.Client;
using Bitfield.Core.Download;
using Bitfield.Core.Torrents;

namespace Bitfield;

/// <summary>
/// The window: every torrent in a list, and the selected one in detail below.
///
/// It owns nothing that matters. The engine runs the torrents whether this is
/// open or not; the window reads one snapshot of it on a timer and draws that,
/// which is far easier to keep honest than a dozen callbacks arriving from a
/// dozen threads.
/// </summary>
internal sealed class MainForm : Form
{
    private readonly Engine _engine = new();

    private readonly TorrentListControl _list = new() { Dock = DockStyle.Fill };
    private readonly PieceMapControl _map = new() { Dock = DockStyle.Fill };
    private readonly SpeedGraphControl _graph = new() { Dock = DockStyle.Top, Height = 140 };
    private readonly PeerListControl _peers = new() { Dock = DockStyle.Fill };
    private readonly StatsBar _stats = new() { Dock = DockStyle.Top };
    private readonly ListBox _log = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    private Button? _pauseButton;
    private readonly TextBox _downLimit = new();
    private readonly TextBox _upLimit = new();
    private readonly List<string> _notes = [];

    private readonly Dictionary<InfoHash, (long Down, long Up, DateTime At)> _lastSample = [];

    /// <summary>This tick's rates, worked out once in the list and read again by the detail panel.</summary>
    private readonly Dictionary<InfoHash, (double Down, double Up)> _rates = [];

    private readonly string? _openOnStart;
    private readonly string? _directoryOnStart;

    public MainForm(string? open = null, string? directory = null)
    {
        _openOnStart = open;
        _directoryOnStart = directory;

        Text = "Bitfield Torrent";
        Icon = LoadIcon();
        ClientSize = new Size(1240, 860);
        MinimumSize = new Size(960, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;

        BuildLayout();

        _engine.Note += Note;
        _engine.Changed += () => BeginInvoke(RefreshList);

        _list.SelectionChanged += () => BeginInvoke(OnSelectionChanged);
        _list.RowMenu += (infoHash, point) => ShowRowMenu(infoHash, point);

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        Shown += async (_, _) =>
        {
            await _engine.StartAsync();
            OpenFromCommandLine();
        };

        FormClosing += (_, _) => Shutdown();
    }

    private static Icon? LoadIcon()
    {
        using Stream? stream = typeof(MainForm).Assembly.GetManifestResourceStream("Bitfield.app.ico");
        return stream == null ? null : new Icon(stream);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.NativeMethods.UseDarkTitleBar(Handle);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Native.NativeMethods.UseDarkTitleBar(Handle);
    }

    // ------------------------------------------------------------- the layout

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

        Panel detail = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 8, 12, 0), BackColor = Theme.Background };
        detail.Controls.Add(centre);
        detail.Controls.Add(side);

        Panel statsHost = new()
        {
            Dock = DockStyle.Top,
            Height = 82,
            Padding = new Padding(12, 0, 12, 8),
            BackColor = Theme.Background,
        };
        statsHost.Controls.Add(_stats);

        Panel listHost = new()
        {
            Dock = DockStyle.Top,
            Height = 220,
            Padding = new Padding(12, 0, 12, 10),
            BackColor = Theme.Background,
        };
        listHost.Controls.Add(_list);

        Splitter splitter = new()
        {
            Dock = DockStyle.Top,
            Height = 6,
            BackColor = Theme.Background,
            MinExtra = 220,
            MinSize = 90,
        };

        Controls.Add(detail);
        Controls.Add(statsHost);
        Controls.Add(splitter);
        Controls.Add(listHost);
        Controls.Add(BuildHeader());
        Controls.Add(BuildLog());
    }

    private Control BuildHeader()
    {
        Panel header = new() { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Background, Padding = new Padding(12, 8, 12, 0) };

        // The limits go in a panel docked to the right rather than placed at a
        // computed offset: a position worked out from the form's width is wrong
        // the moment anything around it has padding, which is how they ended up
        // off the edge of the window the first time.
        FlowLayoutPanel limits = new()
        {
            Dock = DockStyle.Right,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Theme.Background,
            WrapContents = false,
            Padding = new Padding(0, 2, 0, 0),
        };

        limits.Controls.Add(Limit(_downLimit, "down KB/s"));
        limits.Controls.Add(Limit(_upLimit, "up KB/s"));

        FlowLayoutPanel buttons = new()
        {
            Dock = DockStyle.Left,
            FlowDirection = FlowDirection.LeftToRight,
            AutoSize = true,
            BackColor = Theme.Background,
            WrapContents = false,
        };

        buttons.Controls.Add(Button("Add torrent…", OpenTorrent));
        buttons.Controls.Add(Button("Add magnet…", OpenMagnet));
        buttons.Controls.Add(_pauseButton = Button("Pause", TogglePauseSelected));
        buttons.Controls.Add(Button("Remove…", () => RemoveSelected(deleteFiles: false)));

        header.Controls.Add(buttons);
        header.Controls.Add(limits);

        return header;
    }

    private Control BuildLog()
    {
        _log.Dock = DockStyle.Bottom;
        _log.Height = 76;
        _log.BackColor = Theme.Surface;
        _log.ForeColor = Theme.TextSecondary;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = Theme.CaptionFont;
        _log.IntegralHeight = false;

        Panel host = new() { Dock = DockStyle.Bottom, Height = 88, Padding = new Padding(12, 8, 12, 12), BackColor = Theme.Background };
        host.Controls.Add(_log);
        return host;
    }

    private Button Button(string text, Action onClick)
    {
        Button button = new()
        {
            Text = text,
            Margin = new Padding(0, 4, 8, 0),
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

    /// <summary>
    /// A rate limit box, applying to every torrent at once. Zero means no
    /// limit, which is what both start at — throttling a client by default
    /// would be a surprise.
    /// </summary>
    private Control Limit(TextBox box, string caption)
    {
        Panel panel = new()
        {
            Size = new Size(96, 40),
            BackColor = Theme.Background,
            Margin = new Padding(8, 0, 0, 0),
        };

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
        box.TextChanged += (_, _) =>
        {
            _engine.DownloadLimit.BytesPerSecond = Kilobytes(_downLimit.Text);
            _engine.UploadLimit.BytesPerSecond = Kilobytes(_upLimit.Text);
        };

        panel.Controls.Add(label);
        panel.Controls.Add(box);
        return panel;
    }

    private static long Kilobytes(string text) =>
        long.TryParse(text.Trim(), out long value) && value > 0 ? value * 1024 : 0;

    // ---------------------------------------------------------- adding torrents

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
                ResolveAndAdd(link!, _directoryOnStart);
                return;
            }

            Add(Metainfo.Load(_openOnStart), _directoryOnStart);
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
            Title = "Add a torrent",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        // Several at once is the point of a list, so the folder is asked for
        // once and they all go there.
        string? directory = AskForFolder(dialog.FileNames.Length == 1
            ? Path.GetFileName(dialog.FileName)
            : $"{dialog.FileNames.Length} torrents");

        if (directory == null)
        {
            return;
        }

        foreach (string path in dialog.FileNames)
        {
            try
            {
                Add(Metainfo.Load(path), directory);
            }
            catch (Exception e)
            {
                Note($"could not read {Path.GetFileName(path)}: {e.Message}");
            }
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

        ResolveAndAdd(link!, null);
    }

    private void ResolveAndAdd(MagnetLink link, string? directory)
    {
        Note($"asking the swarm about {link.InfoHash}");

        _ = Task.Run(async () =>
        {
            try
            {
                IReadOnlyList<System.Net.IPEndPoint> fromDht = _engine.Dht == null
                    ? []
                    : await _engine.Dht.FindPeersAsync(link.InfoHash, CancellationToken.None).ConfigureAwait(false);

                Metainfo torrent = await MagnetResolver
                    .ResolveAsync(link, _engine.PeerId, _engine.Port, fromDht, Note)
                    .ConfigureAwait(false);

                BeginInvoke(() => Add(torrent, directory));
            }
            catch (Exception e)
            {
                Note($"the description could not be fetched: {e.Message}");
            }
        });
    }

    private void Add(Metainfo torrent, string? directory)
    {
        directory ??= AskForFolder(torrent.Name);
        if (directory == null)
        {
            return;
        }

        _engine.Add(torrent, directory);
    }

    private string? AskForFolder(string what)
    {
        using FolderBrowserDialog folder = new() { Description = $"Where should \"{what}\" go?" };
        return folder.ShowDialog(this) == DialogResult.OK ? folder.SelectedPath : null;
    }

    // -------------------------------------------------------- removing torrents

    private void ShowRowMenu(InfoHash infoHash, Point point)
    {
        ContextMenuStrip menu = new()
        {
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            ShowImageMargin = false,
        };

        if (_engine.Find(infoHash) is { } session)
        {
            menu.Items.Add(session.Paused ? "Resume" : "Pause", null,
                (_, _) => _engine.SetPaused(infoHash, !session.Paused));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Open containing folder", null, (_, _) => OpenFolder(infoHash));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove", null, (_, _) => Remove(infoHash, deleteFiles: false));
        menu.Items.Add("Remove and delete files…", null, (_, _) => Remove(infoHash, deleteFiles: true));

        menu.Show(_list, point);
    }

    private void TogglePauseSelected()
    {
        if (_list.Selected is { } selected && _engine.Find(selected) is { } session)
        {
            _engine.SetPaused(selected, !session.Paused);
        }
    }

    private void RemoveSelected(bool deleteFiles)
    {
        if (_list.Selected is { } selected)
        {
            Remove(selected, deleteFiles);
        }
    }

    /// <summary>
    /// Removing a torrent is one confirmation; removing its files is another,
    /// because one of those is undoable by adding the torrent again and the
    /// other is somebody's download gone.
    /// </summary>
    private void Remove(InfoHash infoHash, bool deleteFiles)
    {
        TorrentSession? session = _engine.Find(infoHash);
        if (session == null)
        {
            return;
        }

        string question = deleteFiles
            ? $"Remove \"{session.Name}\" and delete everything it downloaded?\n\nThis cannot be undone."
            : $"Remove \"{session.Name}\"?\n\nThe files it downloaded are left where they are.";

        if (MessageBox.Show(this, question, deleteFiles ? "Remove and delete files" : "Remove torrent",
                MessageBoxButtons.OKCancel,
                deleteFiles ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _ = Task.Run(() => _engine.RemoveAsync(infoHash, deleteFiles));
    }

    private void OpenFolder(InfoHash infoHash)
    {
        if (_engine.Find(infoHash) is not { } session)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = session.State.SavePath,
                UseShellExecute = true,
            });
        }
        catch (Exception e)
        {
            Note($"could not open the folder: {e.Message}");
        }
    }

    // -------------------------------------------------------------- the drawing

    private void OnTick()
    {
        RefreshList();
        RefreshDetail();
    }

    private void RefreshList()
    {
        List<TorrentRow> rows = [];

        foreach (TorrentSession session in _engine.Torrents)
        {
            DownloadProgress? progress = session.Download?.Snapshot();
            (double down, double up) = Sample(session);
            _rates[session.InfoHash] = (down, up);

            rows.Add(new TorrentRow(
                session.InfoHash,
                session.Name,
                session.Torrent.TotalLength,
                session.Fraction,
                StatusText(session),
                StatusColour(session),
                down,
                up,
                progress?.ConnectedPeers ?? 0));
        }

        _list.Set(rows);

        if (_pauseButton != null)
        {
            TorrentSession? selected = _list.Selected is { } infoHash ? _engine.Find(infoHash) : null;
            _pauseButton.Text = selected?.Paused == true ? "Resume" : "Pause";
            _pauseButton.Enabled = selected != null;
        }
    }

    private static string StatusText(TorrentSession session) => session.Status switch
    {
        TorrentStatus.Checking => $"checking {session.CheckedFraction * 100:N0}%",
        TorrentStatus.Seeding => "seeding",
        TorrentStatus.Paused => "paused",
        TorrentStatus.Stalled => "stalled",
        TorrentStatus.Error => "error",
        _ => "downloading",
    };

    private static Color StatusColour(TorrentSession session) => session.Status switch
    {
        TorrentStatus.Checking => Theme.Warning,
        TorrentStatus.Seeding => Theme.Upload,
        TorrentStatus.Paused => Theme.TextMuted,
        TorrentStatus.Stalled => Theme.TextMuted,
        TorrentStatus.Error => Color.FromArgb(0xE0, 0x6C, 0x6C),
        _ => Theme.Accent,
    };

    /// <summary>
    /// Bytes a second since the last look. Kept per torrent, because a rate
    /// worked out from a total divided by how long the client has been running
    /// is an average, not a speed.
    /// </summary>
    private (double Down, double Up) Sample(TorrentSession session)
    {
        long downloaded = session.Download?.Downloaded ?? 0;
        long uploaded = session.Uploaded;
        DateTime now = DateTime.UtcNow;

        if (!_lastSample.TryGetValue(session.InfoHash, out (long Down, long Up, DateTime At) last))
        {
            _lastSample[session.InfoHash] = (downloaded, uploaded, now);
            return (0, 0);
        }

        double seconds = Math.Max((now - last.At).TotalSeconds, 0.001);
        _lastSample[session.InfoHash] = (downloaded, uploaded, now);

        return (Math.Max(0, (downloaded - last.Down) / seconds), Math.Max(0, (uploaded - last.Up) / seconds));
    }

    private void OnSelectionChanged()
    {
        _graph.Clear();
        _map.Clear();
        RefreshDetail();
    }

    private void RefreshDetail()
    {
        TorrentSession? session = _list.Selected is { } selected ? _engine.Find(selected) : null;

        if (session == null)
        {
            _map.Clear();
            _peers.Set([]);
            _stats.Set(
                ("torrents", $"{_engine.Torrents.Count}", Theme.TextSecondary),
                ("dht", $"{_engine.Dht?.Table.Count ?? 0:N0}", Theme.TextSecondary),
                ("peers", $"{_engine.Budget.InUse}/{_engine.Budget.Total}", Theme.TextSecondary));
            return;
        }

        if (session.Download is not { } download)
        {
            // Paused or still checking: the piece map still has something to
            // say, because what is held does not go anywhere while it waits.
            if (session.Have is { } held)
            {
                _map.SetPieces(session.PieceCount, held.Span, []);
            }
            else
            {
                _map.Clear();
            }

            _peers.Set([]);

            _stats.Set(
                session.Paused
                    ? ("status", "paused", Theme.TextMuted)
                    : ("status", "checking", Theme.Warning),
                ("progress", $"{session.Fraction * 100:N1}%", Theme.Accent),
                ("downloaded", Theme.Bytes(session.Downloaded), Theme.TextSecondary),
                ("uploaded", Theme.Bytes(session.Uploaded), Theme.Upload));

            return;
        }

        DownloadProgress progress = download.Snapshot();

        // The list worked the rate out a moment ago; sampling again here would
        // divide a few bytes by a few microseconds and produce nonsense.
        (double down, double up) = _rates.GetValueOrDefault(session.InfoHash);

        _map.SetPieces(progress.PieceCount, download.Have.Span, download.PiecesInProgress());
        _graph.Add(down, up);

        long remaining = session.Torrent.TotalLength - download.Downloaded;
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

        _peers.Set(download.Peers().Select(peer => new PeerRow(
            peer.RemoteEndPoint.ToString(),
            peer.ClientName,
            peer.BytesPerSecond,
            peer.UploadBytesPerSecond,
            peer.State.Available.SetCount,
            progress.PieceCount,
            peer.State.ChokedByPeer,
            peer.State.PeerInterested)));
    }

    // ------------------------------------------------------------------ the rest

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
            Icon = Icon,
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

        Button ok = new()
        {
            Text = "Add",
            DialogResult = DialogResult.OK,
            Location = new Point(414, 58),
            Size = new Size(112, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
        };

        ok.FlatAppearance.BorderColor = Theme.Border;

        dialog.Controls.Add(input);
        dialog.Controls.Add(ok);
        dialog.AcceptButton = ok;

        dialog.HandleCreated += (_, _) => Native.NativeMethods.UseDarkTitleBar(dialog.Handle);
        dialog.Shown += (_, _) => Native.NativeMethods.UseDarkTitleBar(dialog.Handle);

        return dialog.ShowDialog(this) == DialogResult.OK && input.Text.Length > 0 ? input.Text : null;
    }

    private void Shutdown()
    {
        _tick.Stop();
        _engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
    }
}
