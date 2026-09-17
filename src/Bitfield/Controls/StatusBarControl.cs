namespace Bitfield.Controls;

/// <summary>
/// The strip along the bottom: what the client as a whole is doing, and the
/// limits it is doing it under.
///
/// The limits live here rather than in the settings alone because they are the
/// one setting somebody reaches for mid-download — a torrent is in the way of a
/// call, and the number has to be a click away rather than three.
/// </summary>
internal sealed class StatusBarControl : Control
{
    private readonly TextBox _downLimit = new();
    private readonly TextBox _upLimit = new();

    private double _down;
    private double _up;
    private int _peers;
    private int _dhtNodes;
    private int _torrents;
    private bool _listening;

    public StatusBarControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
        Height = 30;

        Controls.Add(Limit(_downLimit));
        Controls.Add(Limit(_upLimit));
    }

    /// <summary>Raised when a limit is typed into, in kilobytes a second.</summary>
    public event Action<int, int>? LimitsChanged;

    public void SetLimits(int downKb, int upKb)
    {
        _downLimit.Text = downKb.ToString();
        _upLimit.Text = upKb.ToString();
    }

    public void Set(double down, double up, int peers, int dhtNodes, int torrents, bool listening)
    {
        _down = down;
        _up = up;
        _peers = peers;
        _dhtNodes = dhtNodes;
        _torrents = torrents;
        _listening = listening;

        Invalidate();
    }

    private TextBox Limit(TextBox box)
    {
        box.Width = 46;
        box.BackColor = Theme.SurfaceRaised;
        box.ForeColor = Theme.TextPrimary;
        box.BorderStyle = BorderStyle.FixedSingle;
        box.Font = Theme.CaptionFont;
        box.Text = "0";
        box.TextAlign = HorizontalAlignment.Right;

        box.TextChanged += (_, _) => LimitsChanged?.Invoke(Number(_downLimit.Text), Number(_upLimit.Text));

        return box;
    }

    private static int Number(string text) =>
        int.TryParse(text.Trim(), out int value) && value > 0 ? value : 0;

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);

        // Laid out from the right edge, so the boxes stay put as the window
        // changes width.
        int y = (Height - _downLimit.Height) / 2;

        _upLimit.Location = new Point(Width - 60, y);
        _downLimit.Location = new Point(Width - 186, y);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using Pen top = new(Theme.Border);
        graphics.DrawLine(top, 0, 0, Width, 0);

        using SolidBrush muted = new(Theme.TextMuted);
        using SolidBrush secondary = new(Theme.TextSecondary);
        using SolidBrush down = new(Theme.Accent);
        using SolidBrush up = new(Theme.Upload);

        float y = (Height - Theme.UiFont.Height) / 2f;
        float x = 12;

        x += Draw(graphics, "↓", muted, x, y);
        x += Draw(graphics, Theme.Rate(_down), down, x, y) + 14;
        x += Draw(graphics, "↑", muted, x, y);
        x += Draw(graphics, Theme.Rate(_up), up, x, y) + 22;

        x += Draw(graphics, $"{_torrents} torrents", secondary, x, y) + 16;
        x += Draw(graphics, $"{_peers} peers", secondary, x, y) + 16;

        x += Draw(graphics, _dhtNodes > 0 ? $"DHT {_dhtNodes}" : "DHT off", _dhtNodes > 0 ? secondary : muted, x, y) + 16;

        Draw(graphics, _listening ? "port open" : "not listening", _listening ? secondary : muted, x, y);

        // The captions for the two boxes on the right.
        using SolidBrush caption = new(Theme.TextMuted);
        graphics.DrawString("limit KB/s  ↓", Theme.CaptionFont, caption, Width - 262, y + 1);
        graphics.DrawString("↑", Theme.CaptionFont, caption, Width - 74, y + 1);
    }

    private static float Draw(Graphics graphics, string text, Brush brush, float x, float y)
    {
        graphics.DrawString(text, Theme.UiFont, brush, x, y);
        return graphics.MeasureString(text, Theme.UiFont).Width - 4;
    }
}
