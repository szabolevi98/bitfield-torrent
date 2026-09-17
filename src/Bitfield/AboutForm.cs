using System.Diagnostics;
using System.Reflection;

namespace Bitfield;

/// <summary>
/// What this is, who wrote it, and where to find the source.
/// </summary>
internal sealed class AboutForm : Form
{
    private const string RepositoryUrl = "https://github.com/szabolevi98/bitfield-torrent";

    public AboutForm()
    {
        Text = "About Bitfield Torrent";
        ClientSize = new Size(460, 268);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        BackColor = Theme.Background;
        ForeColor = Theme.TextPrimary;
        Font = Theme.UiFont;
        MinimizeBox = false;
        MaximizeBox = false;

        Build();
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

    private void Build()
    {
        PictureBox icon = new()
        {
            Location = new Point(24, 24),
            Size = new Size(64, 64),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Theme.Background,
        };

        using (Stream? stream = typeof(AboutForm).Assembly.GetManifestResourceStream("Bitfield.app.ico"))
        {
            if (stream != null)
            {
                using Icon source = new(stream, new Size(64, 64));
                icon.Image = source.ToBitmap();
            }
        }

        Label title = new()
        {
            Text = "Bitfield Torrent",
            Font = new Font("Segoe UI Light", 17F),
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new Point(106, 24),
        };

        Label version = new()
        {
            Text = $"Version {Version()}",
            Font = Theme.CaptionFont,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(108, 58),
        };

        Label description = new()
        {
            Text = "A BitTorrent client written from the protocol up:\n"
                + "bencode, the peer wire protocol, piece selection,\n"
                + "the choking algorithm and a Kademlia DHT.",
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            Location = new Point(26, 108),
        };

        Panel separator = new()
        {
            Location = new Point(24, 176),
            Size = new Size(412, 1),
            BackColor = Theme.Border,
        };

        Label copyright = new()
        {
            Text = "Copyright © 2026 szabolevi98",
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Location = new Point(26, 192),
        };

        Label license = new()
        {
            Text = "MIT License",
            Font = Theme.CaptionFont,
            ForeColor = Theme.TextMuted,
            AutoSize = true,
            Location = new Point(28, 214),
        };

        LinkLabel repository = new()
        {
            Text = RepositoryUrl,
            Font = Theme.CaptionFont,
            AutoSize = true,
            Location = new Point(28, 234),
            LinkColor = Theme.Accent,
            ActiveLinkColor = Theme.AccentPressed,
            VisitedLinkColor = Theme.Accent,
            BackColor = Theme.Background,
        };

        repository.LinkClicked += (_, _) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = RepositoryUrl, UseShellExecute = true });
            }
            catch (Exception)
            {
                // A machine with nothing to open a link with is not worth a
                // dialog about it.
            }
        };

        Button close = new()
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            Location = new Point(332, 222),
            Size = new Size(104, 30),
            FlatStyle = FlatStyle.Flat,
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
        };

        close.FlatAppearance.BorderColor = Theme.Border;
        close.FlatAppearance.MouseOverBackColor = Theme.SurfaceHover;

        Controls.AddRange(icon, title, version, description, separator, copyright, license, repository, close);

        AcceptButton = close;
        CancelButton = close;
    }

    private static string Version()
    {
        string? version = typeof(AboutForm).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(AboutForm).Assembly.GetName().Version?.ToString();

        if (version == null)
        {
            return "unknown";
        }

        // The build metadata a release build appends says nothing to anybody.
        int plus = version.IndexOf('+');
        return plus < 0 ? version : version[..plus];
    }
}
