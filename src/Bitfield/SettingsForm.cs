using Microsoft.Win32;
using Bitfield.Core.Storage;

namespace Bitfield;

/// <summary>
/// The settings, as a dialog.
///
/// Nothing here is clever: the point is that the things worth changing are
/// changeable, and that the ones needing a restart say so on the line rather
/// than failing quietly when somebody changes them.
/// </summary>
internal sealed class SettingsForm : Form
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Bitfield Torrent";

    private readonly TextBox _savePath = new();
    private readonly TextBox _port = new();
    private readonly TextBox _downLimit = new();
    private readonly TextBox _upLimit = new();
    private readonly TextBox _maxPeers = new();
    private readonly CheckBox _upnp = new();
    private readonly CheckBox _dht = new();
    private readonly CheckBox _startWithWindows = new();
    private readonly CheckBox _minimiseToTray = new();
    private readonly CheckBox _closeToTray = new();
    private readonly CheckBox _notify = new();

    public SettingsForm(Settings settings)
    {
        Result = settings;

        Text = "Settings";
        ClientSize = new Size(560, 486);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        MinimizeBox = false;
        MaximizeBox = false;

        Build(settings);
    }

    public Settings Result { get; private set; }

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

    private void Build(Settings settings)
    {
        int y = 18;

        Controls.Add(Section("Downloads", ref y));

        _savePath.Text = settings.DefaultSavePath;
        Controls.Add(Field("Default folder", _savePath, ref y, 340));
        Controls.Add(Browse(y - 30));
        Controls.Add(Hint("Empty means ask every time a torrent is added.", ref y));

        Controls.Add(Section("Network", ref y));

        _port.Text = settings.Port.ToString();
        Controls.Add(Field("Port", _port, ref y, 90));

        _upnp.Text = "Ask the router to forward it (UPnP)";
        Controls.Add(Check(_upnp, settings.UseUpnp, ref y));

        _dht.Text = "Use the DHT to find peers without a tracker";
        Controls.Add(Check(_dht, settings.UseDht, ref y));

        Controls.Add(Hint("The port and the DHT take effect the next time the client starts.", ref y));

        Controls.Add(Section("Limits", ref y));

        _downLimit.Text = settings.DownloadLimitKb.ToString();
        Controls.Add(Field("Download KB/s", _downLimit, ref y, 90));

        _upLimit.Text = settings.UploadLimitKb.ToString();
        Controls.Add(Field("Upload KB/s", _upLimit, ref y, 90));

        _maxPeers.Text = settings.MaxPeers.ToString();
        Controls.Add(Field("Peers in total", _maxPeers, ref y, 90));

        Controls.Add(Hint("Zero means no limit. The limits are shared by every torrent.", ref y));

        Controls.Add(Section("Window", ref y));

        _minimiseToTray.Text = "Minimise to the notification area";
        Controls.Add(Check(_minimiseToTray, settings.MinimiseToTray, ref y));

        _closeToTray.Text = "Closing the window keeps the client running";
        Controls.Add(Check(_closeToTray, settings.CloseToTray, ref y));

        _notify.Text = "Say when a torrent finishes";
        Controls.Add(Check(_notify, settings.NotifyOnComplete, ref y));

        _startWithWindows.Text = "Start with Windows";
        Controls.Add(Check(_startWithWindows, settings.StartWithWindows, ref y));

        Button save = Button("Save", 320, ClientSize.Height - 44);
        save.Click += (_, _) => Apply();

        Button cancel = Button("Cancel", 436, ClientSize.Height - 44);
        cancel.DialogResult = DialogResult.Cancel;

        Controls.Add(save);
        Controls.Add(cancel);
        CancelButton = cancel;
    }

    private void Apply()
    {
        Settings updated = Result with
        {
            DefaultSavePath = _savePath.Text.Trim(),
            Port = Number(_port.Text, Result.Port, 1, 65535),
            UseUpnp = _upnp.Checked,
            UseDht = _dht.Checked,
            DownloadLimitKb = Number(_downLimit.Text, 0, 0, int.MaxValue),
            UploadLimitKb = Number(_upLimit.Text, 0, 0, int.MaxValue),
            MaxPeers = Number(_maxPeers.Text, Result.MaxPeers, 1, 2000),
            MinimiseToTray = _minimiseToTray.Checked,
            CloseToTray = _closeToTray.Checked,
            NotifyOnComplete = _notify.Checked,
            StartWithWindows = _startWithWindows.Checked,
        };

        if (updated.StartWithWindows != Result.StartWithWindows)
        {
            SetStartWithWindows(updated.StartWithWindows);
        }

        Result = updated;
        DialogResult = DialogResult.OK;
        Close();
    }

    /// <summary>
    /// Adds or removes the entry Windows starts the client from. Only ever when
    /// the checkbox changes — a client that writes itself into the registry on
    /// every launch is a client nobody can turn off.
    /// </summary>
    private static void SetStartWithWindows(bool start)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null)
            {
                return;
            }

            if (start)
            {
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException)
        {
            MessageBox.Show(
                $"Windows would not let the startup entry be changed: {e.Message}",
                "Settings", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static int Number(string text, int fallback, int low, int high) =>
        int.TryParse(text.Trim(), out int value) && value >= low && value <= high ? value : fallback;

    // ------------------------------------------------------------- the pieces

    private Control Section(string title, ref int y)
    {
        Label label = new()
        {
            Text = title.ToUpperInvariant(),
            ForeColor = Theme.TextMuted,
            Font = Theme.CaptionFont,
            AutoSize = true,
            Location = new Point(18, y),
        };

        y += 24;
        return label;
    }

    private Control Field(string caption, TextBox box, ref int y, int width)
    {
        Panel row = new() { Location = new Point(18, y), Size = new Size(500, 28), BackColor = Theme.Background };

        Label label = new()
        {
            Text = caption,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(2, 5),
        };

        box.Location = new Point(140, 2);
        box.Width = width;
        box.BackColor = Theme.Surface;
        box.ForeColor = Theme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = Theme.UiFont;

        row.Controls.Add(label);
        row.Controls.Add(box);

        y += 32;
        return row;
    }

    private Control Browse(int y)
    {
        Button browse = Button("Browse…", 490, y + 1);
        browse.Size = new Size(50, 24);
        browse.Text = "…";

        browse.Click += (_, _) =>
        {
            using FolderBrowserDialog dialog = new() { Description = "Where should downloads go by default?" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                _savePath.Text = dialog.SelectedPath;
            }
        };

        return browse;
    }

    private Control Check(CheckBox box, bool value, ref int y)
    {
        box.Checked = value;
        box.Location = new Point(20, y);
        box.AutoSize = true;
        box.ForeColor = Theme.TextPrimary;
        box.BackColor = Theme.Background;
        box.FlatStyle = FlatStyle.Flat;
        box.Font = Theme.UiFont;

        y += 26;
        return box;
    }

    private Control Hint(string text, ref int y)
    {
        Label label = new()
        {
            Text = text,
            ForeColor = Theme.TextMuted,
            Font = Theme.CaptionFont,
            AutoSize = true,
            Location = new Point(20, y),
        };

        y += 26;
        return label;
    }

    private Button Button(string text, int x, int y)
    {
        Button button = new()
        {
            Text = text,
            Location = new Point(x, y),
            Size = new Size(104, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            Cursor = Cursors.Hand,
        };

        button.FlatAppearance.BorderColor = Theme.Border;
        button.FlatAppearance.MouseOverBackColor = Theme.SurfaceHover;

        return button;
    }
}
