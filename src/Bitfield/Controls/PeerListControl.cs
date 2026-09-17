namespace Bitfield.Controls;

/// <summary>One row of the peer list.</summary>
internal sealed record PeerRow(
    string Address,
    string Client,
    double Down,
    double Up,
    int PiecesHeld,
    int PieceCount,
    bool Choked,
    bool Interested);

/// <summary>
/// Who this client is talking to, and what each of them is worth.
///
/// Drawn rather than left to a list view, because a themed window with one
/// window-grey control in the corner looks like a mistake, and the useful part
/// of a peer row — the share of the torrent it holds — reads far better as a
/// bar than as a number.
/// </summary>
internal sealed class PeerListControl : Control
{
    private const int RowHeight = 22;
    private const int HeaderHeight = 24;

    private PeerRow[] _rows = [];
    private int _scroll;

    public PeerListControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
    }

    public void Set(IEnumerable<PeerRow> rows)
    {
        _rows = [.. rows.OrderByDescending(row => row.Down).ThenByDescending(row => row.Up)];
        _scroll = Math.Min(_scroll, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        int lines = -e.Delta / 120 * 3;
        _scroll = Math.Clamp(_scroll + lines, 0, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using StringFormat left = new(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
        };

        using StringFormat right = new(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisCharacter,
            LineAlignment = StringAlignment.Center,
            Alignment = StringAlignment.Far,
        };

        DrawHeader(graphics, left, right);

        if (_rows.Length == 0)
        {
            using SolidBrush empty = new(Theme.TextMuted);
            graphics.DrawString("no peers connected", Theme.UiFont, empty, 10, HeaderHeight + 8);
            return;
        }

        using SolidBrush primary = new(Theme.TextPrimary);
        using SolidBrush secondary = new(Theme.TextSecondary);
        using SolidBrush muted = new(Theme.TextMuted);
        using SolidBrush down = new(Theme.Accent);
        using SolidBrush up = new(Theme.Upload);
        using SolidBrush bar = new(Theme.PieceBusy);
        using SolidBrush barFill = new(Theme.Accent);

        int y = HeaderHeight;

        for (int i = _scroll; i < _rows.Length && y < Height; i++)
        {
            PeerRow row = _rows[i];

            if (i % 2 == 1)
            {
                using SolidBrush stripe = new(Theme.SurfaceRaised);
                graphics.FillRectangle(stripe, 0, y, Width, RowHeight);
            }

            Rectangle[] columns = Columns(y, RowHeight);

            graphics.DrawString(row.Address, Theme.UiFont, row.Choked ? muted : primary, columns[0], left);
            graphics.DrawString(row.Client, Theme.UiFont, secondary, columns[1], left);

            // The share of the torrent this peer holds, which is what decides
            // whether it is worth anything here.
            Rectangle held = new(columns[2].X, y + 7, columns[2].Width, 8);
            graphics.FillRectangle(bar, held);
            if (row.PieceCount > 0)
            {
                graphics.FillRectangle(barFill, held.X, held.Y,
                    (int)(held.Width * (row.PiecesHeld / (double)row.PieceCount)), held.Height);
            }

            graphics.DrawString(row.Down > 0 ? Theme.Rate(row.Down) : "", Theme.UiFont, down, columns[3], right);
            graphics.DrawString(row.Up > 0 ? Theme.Rate(row.Up) : "", Theme.UiFont, up, columns[4], right);

            y += RowHeight;
        }

    }

    /// <summary>
    /// Where each column starts and how wide it is. Worked out from the
    /// control's own width rather than fixed, and every string is drawn into
    /// its column's rectangle with an ellipsis — a client name running into the
    /// rate beside it is the sort of thing that makes a window look broken.
    /// </summary>
    private Rectangle[] Columns(int y, int height)
    {
        int rates = 62;
        int bar = 60;
        int fixedPart = bar + (rates * 2) + 24;
        int flexible = Math.Max(120, Width - fixedPart);

        int address = (int)(flexible * 0.56);
        int client = flexible - address;

        int x = 10;
        Rectangle[] columns = new Rectangle[5];
        columns[0] = new Rectangle(x, y, address - 6, height);
        x += address;
        columns[1] = new Rectangle(x, y, client - 6, height);
        x += client;
        columns[2] = new Rectangle(x, y, bar - 6, height);
        x += bar;
        columns[3] = new Rectangle(x, y, rates - 6, height);
        x += rates;
        columns[4] = new Rectangle(x, y, rates - 6, height);

        return columns;
    }

    private void DrawHeader(Graphics graphics, StringFormat left, StringFormat right)
    {
        using SolidBrush background = new(Theme.Background);
        graphics.FillRectangle(background, 0, 0, Width, HeaderHeight);

        using Pen line = new(Theme.Border);
        graphics.DrawLine(line, 0, HeaderHeight - 1, Width, HeaderHeight - 1);

        Rectangle[] columns = Columns(0, HeaderHeight - 1);

        using SolidBrush text = new(Theme.TextMuted);
        graphics.DrawString(_rows.Length > 0 ? $"{_rows.Length:N0} PEERS" : "PEERS", Theme.CaptionFont, text, columns[0], left);
        graphics.DrawString("CLIENT", Theme.CaptionFont, text, columns[1], left);
        graphics.DrawString("HAS", Theme.CaptionFont, text, columns[2], left);
        graphics.DrawString("DOWN", Theme.CaptionFont, text, columns[3], right);
        graphics.DrawString("UP", Theme.CaptionFont, text, columns[4], right);
    }
}
