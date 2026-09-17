using Bitfield.Controls;
using Bitfield.Core.Client;
using Bitfield.Core.Storage;
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
    private readonly NotifyIcon _tray = new();
    private bool _reallyClosing;

    private readonly TorrentListControl _list = new() { Dock = DockStyle.Fill };
    private readonly PieceMapControl _map = new() { Dock = DockStyle.Fill };
    private readonly FileListControl _files = new() { Dock = DockStyle.Fill, Visible = false };
    private Button? _piecesTab;
    private Button? _filesTab;
    private readonly SpeedGraphControl _graph = new() { Dock = DockStyle.Top, Height = 140 };
    private readonly PeerListControl _peers = new() { Dock = DockStyle.Fill };
    private readonly StatsBar _stats = new() { Dock = DockStyle.Top };
    private readonly ListBox _log = new();
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 500 };

    private readonly StatusBarControl _status = new();
    private ToolStripMenuItem? _torrentMenu;
    private ToolStripMenuItem? _pauseItem;
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

        _list.SetSort((TorrentColumn)_engine.Settings.SortColumn, _engine.Settings.SortDescending);
        _list.SortChanged += () => _engine.UpdateSettings(_engine.Settings with
        {
            SortColumn = (int)_list.SortColumn,
            SortDescending = _list.SortDescending,
        });

        _status.SetLimits(_engine.Settings.DownloadLimitKb, _engine.Settings.UploadLimitKb);

        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        Shown += async (_, _) =>
        {
            await _engine.StartAsync();
            OpenFromCommandLine();
        };

        BuildTray();

        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized && _engine.Settings.MinimiseToTray)
            {
                HideToTray();
            }
        };

        FormClosing += OnClosing;
    }

    /// <summary>
    /// The notification area, which is where a torrent client spends most of
    /// its life. The engine runs whether this window is on screen or not, and
    /// the icon is what says so.
    /// </summary>
    private void BuildTray()
    {
        _tray.Icon = Icon;
        _tray.Text = "Bitfield Torrent";
        _tray.Visible = true;

        ContextMenuStrip menu = new()
        {
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            ShowImageMargin = false,
        };

        menu.Items.Add("Show", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pause all", null, (_, _) => PauseAll(true));
        menu.Items.Add("Resume all", null, (_, _) => PauseAll(false));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            _reallyClosing = true;
            Close();
        });

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ShowFromTray();

        _engine.Completed += session =>
        {
            if (_engine.Settings.NotifyOnComplete)
            {
                BeginInvoke(() => _tray.ShowBalloonTip(5000, "Finished", session.Name, ToolTipIcon.Info));
            }
        };
    }

    private void ShowAbout()
    {
        using AboutForm about = new();
        about.ShowDialog(this);
    }

    private void PauseAll(bool paused)
    {
        foreach (TorrentSession session in _engine.Torrents)
        {
            _engine.SetPaused(session.InfoHash, paused);
        }
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void ShowFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Activate();
    }

    /// <summary>Opens what another launch of the client was asked to open.</summary>
    public void OpenFromAnotherLaunch(string argument)
    {
        BeginInvoke(() =>
        {
            ShowFromTray();

            try
            {
                if (MagnetLink.TryParse(argument, out MagnetLink? link, out _))
                {
                    ResolveAndAdd(link!, null);
                    return;
                }

                Add(Metainfo.Load(argument), null);
            }
            catch (Exception e)
            {
                Note($"could not open {argument}: {e.Message}");
            }
        });
    }

    private void OpenSettings()
    {
        using SettingsForm settings = new(_engine.Settings);

        if (settings.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        _engine.UpdateSettings(settings.Result);
        _status.SetLimits(settings.Result.DownloadLimitKb, settings.Result.UploadLimitKb);
        Note("settings saved");
    }

    /// <summary>
    /// Closing either ends the client or puts it in the notification area, and
    /// asks first when torrents are still running — a client that stops a
    /// download because somebody reached for the X is a client that loses work.
    /// </summary>
    private void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_reallyClosing && e.CloseReason == CloseReason.UserClosing)
        {
            if (_engine.Settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }

            int running = _engine.Torrents.Count(session => !session.Paused);

            if (running > 0)
            {
                string question = running == 1
                    ? "One torrent is still running. Close anyway?"
                    : $"{running} torrents are still running. Close anyway?";

                if (MessageBox.Show(this, question, "Bitfield Torrent",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

        Shutdown();
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
        centre.Controls.Add(_files);
        centre.Controls.Add(_map);
        centre.Controls.Add(BuildTabs());

        _files.PriorityChanged += (file, priority) => SetPriority(file, priority);

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
        Controls.Add(BuildLog());
        Controls.Add(BuildStatusBar());

        MenuStrip menu = BuildMenu();
        Controls.Add(menu);
        MainMenuStrip = menu;
    }

    /// <summary>
    /// Two views of the same torrent: the pieces as they arrive, and the files
    /// they add up to. Tabs rather than both at once, because the piece map
    /// wants the room.
    /// </summary>
    private Control BuildTabs()
    {
        Panel tabs = new() { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Background };

        _piecesTab = Tab("Pieces", 0, true);
        _filesTab = Tab("Files", 86, false);

        tabs.Controls.Add(_piecesTab);
        tabs.Controls.Add(_filesTab);

        return tabs;
    }

    private Button Tab(string text, int x, bool pieces)
    {
        Button tab = new()
        {
            Text = text,
            Location = new Point(x, 0),
            Size = new Size(82, 26),
            FlatStyle = FlatStyle.Flat,
            BackColor = pieces ? Theme.Surface : Theme.Background,
            ForeColor = pieces ? Theme.TextPrimary : Theme.TextMuted,
            Font = Theme.UiFont,
            Cursor = Cursors.Hand,
        };

        tab.FlatAppearance.BorderSize = 0;
        tab.Click += (_, _) => ShowTab(pieces);

        return tab;
    }

    private void ShowTab(bool pieces)
    {
        _map.Visible = pieces;
        _files.Visible = !pieces;

        if (_piecesTab != null)
        {
            _piecesTab.BackColor = pieces ? Theme.Surface : Theme.Background;
            _piecesTab.ForeColor = pieces ? Theme.TextPrimary : Theme.TextMuted;
        }

        if (_filesTab != null)
        {
            _filesTab.BackColor = pieces ? Theme.Background : Theme.Surface;
            _filesTab.ForeColor = pieces ? Theme.TextMuted : Theme.TextPrimary;
        }
    }

    private void SetPriority(int file, FilePriority priority)
    {
        if (_list.Selected is not { } selected || _engine.Find(selected) is not { } session)
        {
            return;
        }

        List<FilePriority> priorities = [.. session.Priorities.Files];
        priorities[file] = priority;

        _engine.SetPriorities(selected, priorities);
    }

    /// <summary>
    /// The menu. A row of buttons across the top is what a program looks like
    /// before it has decided what it is; the actions belong in one place with
    /// their shortcuts written beside them.
    /// </summary>
    private MenuStrip BuildMenu()
    {
        MenuStrip menu = new()
        {
            Dock = DockStyle.Top,
            BackColor = Theme.Background,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            Renderer = new DarkMenuRenderer(),
            Padding = new Padding(8, 2, 0, 2),
        };

        ToolStripMenuItem file = new("&File");
        file.DropDownItems.Add(Item("Add torrent…", Keys.Control | Keys.O, OpenTorrent));
        file.DropDownItems.Add(Item("Add magnet link…", Keys.Control | Keys.N, OpenMagnet));
        file.DropDownItems.Add(new ToolStripSeparator());
        file.DropDownItems.Add(Item("Exit", Keys.None, () =>
        {
            _reallyClosing = true;
            Close();
        }));

        _torrentMenu = new ToolStripMenuItem("&Torrent");
        _pauseItem = Item("Pause", Keys.None, TogglePauseSelected);
        _torrentMenu.DropDownItems.Add(_pauseItem);
        _torrentMenu.DropDownItems.Add(Item("Open containing folder", Keys.None, () =>
        {
            if (_list.Selected is { } selected)
            {
                OpenFolder(selected);
            }
        }));

        _torrentMenu.DropDownItems.Add(new ToolStripSeparator());
        _torrentMenu.DropDownItems.Add(Item("Select all", Keys.Control | Keys.A, _list.SelectAll));
        _torrentMenu.DropDownItems.Add(new ToolStripSeparator());
        _torrentMenu.DropDownItems.Add(Item("Remove…", Keys.Delete, () => RemoveSelected(deleteFiles: false)));
        _torrentMenu.DropDownItems.Add(Item("Remove and delete files…", Keys.Shift | Keys.Delete,
            () => RemoveSelected(deleteFiles: true)));

        ToolStripMenuItem view = new("&View");
        view.DropDownItems.Add(Item("Pieces", Keys.None, () => ShowTab(true)));
        view.DropDownItems.Add(Item("Files", Keys.None, () => ShowTab(false)));

        ToolStripMenuItem tools = new("T&ools");
        tools.DropDownItems.Add(Item("Settings…", Keys.None, OpenSettings));

        ToolStripMenuItem help = new("&Help");
        help.DropDownItems.Add(Item("About Bitfield Torrent", Keys.None, ShowAbout));

        menu.Items.AddRange([file, _torrentMenu, view, tools, help]);
        return menu;
    }

    private static ToolStripMenuItem Item(string text, Keys shortcut, Action onClick)
    {
        ToolStripMenuItem item = new(text, null, (_, _) => onClick());

        if (shortcut != Keys.None)
        {
            item.ShortcutKeys = shortcut;
        }

        return item;
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

        Panel host = new() { Dock = DockStyle.Bottom, Height = 84, Padding = new Padding(12, 8, 12, 8), BackColor = Theme.Background };
        host.Controls.Add(_log);
        return host;
    }

    private Control BuildStatusBar()
    {
        _status.Dock = DockStyle.Bottom;
        _status.LimitsChanged += (down, up) =>
            _engine.UpdateSettings(_engine.Settings with { DownloadLimitKb = down, UploadLimitKb = up });

        return _status;
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
        // The default folder is what makes adding several torrents bearable;
        // without one set, every one of them asks.
        directory ??= _engine.Settings.DefaultSavePath is { Length: > 0 } fallback && Directory.Exists(fallback)
            ? fallback
            : AskForFolder(torrent.Name);
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
            Renderer = new DarkMenuRenderer(),
        };

        if (_engine.Find(infoHash) is { } session)
        {
            menu.Items.Add(session.Paused ? "Resume" : "Pause", null,
                (_, _) => _engine.SetPaused(infoHash, !session.Paused));
            menu.Items.Add(new ToolStripSeparator());
        }

        menu.Items.Add("Open containing folder", null, (_, _) => OpenFolder(infoHash));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Remove", null, (_, _) => RemoveSelected(deleteFiles: false));
        menu.Items.Add("Remove and delete files…", null, (_, _) => RemoveSelected(deleteFiles: true));

        menu.Show(_list, point);
    }

    private void TogglePauseSelected()
    {
        if (_list.Selected is { } selected && _engine.Find(selected) is { } session)
        {
            _engine.SetPaused(selected, !session.Paused);
        }
    }

    /// <summary>
    /// Removes everything selected, asking once for the lot rather than once
    /// per torrent — somebody who selected nine of them meant nine.
    /// </summary>
    private void RemoveSelected(bool deleteFiles)
    {
        IReadOnlyList<InfoHash> selected = _list.SelectedAll;

        if (selected.Count == 0)
        {
            return;
        }

        if (selected.Count == 1)
        {
            Remove(selected[0], deleteFiles);
            return;
        }

        string question = deleteFiles
            ? $"Remove {selected.Count} torrents and delete everything they downloaded?\n\nThis cannot be undone."
            : $"Remove {selected.Count} torrents?\n\nThe files they downloaded are left where they are.";

        if (MessageBox.Show(this, question, deleteFiles ? "Remove and delete files" : "Remove torrents",
                MessageBoxButtons.OKCancel,
                deleteFiles ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.OK)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            foreach (InfoHash infoHash in selected)
            {
                await _engine.RemoveAsync(infoHash, deleteFiles).ConfigureAwait(false);
            }
        });
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
        _engine.CheckForCompletions();
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
                progress?.ConnectedPeers ?? 0,
                session.State.AddedOn));
        }

        _list.Set(rows);

        if (_pauseItem != null && _torrentMenu != null)
        {
            TorrentSession? selected = _list.Selected is { } infoHash ? _engine.Find(infoHash) : null;
            _pauseItem.Text = selected?.Paused == true ? "Resume" : "Pause";
            _torrentMenu.Enabled = selected != null;
        }

        _status.Set(
            rows.Sum(row => row.Down),
            rows.Sum(row => row.Up),
            _engine.Budget.InUse,
            _engine.Dht?.Table.Count ?? 0,
            rows.Count,
            _engine.Settings.UseUpnp);
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
            _files.Clear();
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
            ShowFiles(session);

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

        ShowFiles(session);

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

    private void ShowFiles(TorrentSession session)
    {
        List<FileRow> rows = [];

        for (int i = 0; i < session.Torrent.Files.Count; i++)
        {
            Core.Torrents.TorrentFile file = session.Torrent.Files[i];

            if (file.IsPadding)
            {
                // Padding is not something anybody chose to download, and
                // showing it would only invite somebody to set it aside.
                continue;
            }

            // Every path starts with the torrent's own folder, which is the
            // same for every row and eats the width that tells them apart.
            string path = file.Path.StartsWith(session.Torrent.Name + '/', StringComparison.Ordinal)
                ? file.Path[(session.Torrent.Name.Length + 1)..]
                : file.Path;

            rows.Add(new FileRow(
                i,
                path,
                file.Length,
                session.FileFraction(i),
                session.Priorities.Files[i],
                session.Priorities.SharesPieces(i)));
        }

        _files.Set(rows);
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
        _tray.Visible = false;
        _tray.Dispose();
        _engine.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
    }
}
