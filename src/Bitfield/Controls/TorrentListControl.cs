using Bitfield.Core.Torrents;

namespace Bitfield.Controls;

/// <summary>One torrent, as the list shows it.</summary>
internal sealed record TorrentRow(
    InfoHash InfoHash,
    string Name,
    long Size,
    double Fraction,
    string Status,
    Color StatusColour,
    double Down,
    double Up,
    int Peers);

/// <summary>
/// Every torrent the client is running, one to a row.
///
/// The progress bar is the column that does the work: a number tells you a
/// torrent is at 61%, a bar tells you at a glance which of nine torrents is
/// nearly there and which has barely started.
/// </summary>
internal sealed class TorrentListControl : Control
{
    private const int RowHeight = 26;
    private const int HeaderHeight = 26;

    private TorrentRow[] _rows = [];
    private int _scroll;

    public TorrentListControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
    }

    /// <summary>The torrent the detail panel below is showing.</summary>
    public InfoHash? Selected { get; private set; }

    public event Action? SelectionChanged;

    /// <summary>Raised on a right-click, with the row under the cursor.</summary>
    public event Action<InfoHash, Point>? RowMenu;

    public void Set(IReadOnlyList<TorrentRow> rows)
    {
        _rows = [.. rows];

        // A torrent that has gone takes the selection with it, and the first
        // one that arrives takes it if there was none.
        if (Selected is { } selected && !_rows.Any(row => row.InfoHash == selected))
        {
            Selected = _rows.Length > 0 ? _rows[0].InfoHash : null;
            SelectionChanged?.Invoke();
        }
        else if (Selected == null && _rows.Length > 0)
        {
            Selected = _rows[0].InfoHash;
            SelectionChanged?.Invoke();
        }

        _scroll = Math.Min(_scroll, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        int index = RowAt(e.Y);
        if (index < 0)
        {
            return;
        }

        if (Selected != _rows[index].InfoHash)
        {
            Selected = _rows[index].InfoHash;
            SelectionChanged?.Invoke();
            Invalidate();
        }

        if (e.Button == MouseButtons.Right)
        {
            RowMenu?.Invoke(_rows[index].InfoHash, e.Location);
        }
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        _scroll = Math.Clamp(_scroll - (e.Delta / 120 * 2), 0, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    private int RowAt(int y)
    {
        if (y < HeaderHeight)
        {
            return -1;
        }

        int index = _scroll + ((y - HeaderHeight) / RowHeight);
        return index < _rows.Length ? index : -1;
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
            graphics.DrawString("no torrents — add one with the buttons above", Theme.UiFont, empty, 12, HeaderHeight + 10);
            return;
        }

        using SolidBrush primary = new(Theme.TextPrimary);
        using SolidBrush secondary = new(Theme.TextSecondary);
        using SolidBrush down = new(Theme.Accent);
        using SolidBrush up = new(Theme.Upload);
        using SolidBrush track = new(Theme.PieceMissing);

        int y = HeaderHeight;

        for (int i = _scroll; i < _rows.Length && y < Height; i++)
        {
            TorrentRow row = _rows[i];
            bool selected = Selected == row.InfoHash;

            using (SolidBrush background = new(selected ? Theme.Selection : i % 2 == 1 ? Theme.SurfaceRaised : Theme.Surface))
            {
                graphics.FillRectangle(background, 0, y, Width, RowHeight);
            }

            if (selected)
            {
                using SolidBrush marker = new(Theme.Accent);
                graphics.FillRectangle(marker, 0, y, 3, RowHeight);
            }

            Rectangle[] columns = Columns(y, RowHeight);

            graphics.DrawString(row.Name, Theme.UiFont, primary, columns[0], left);
            graphics.DrawString(Theme.Bytes(row.Size), Theme.UiFont, secondary, columns[1], right);

            DrawProgress(graphics, columns[2], row, track);

            using (SolidBrush status = new(row.StatusColour))
            {
                graphics.DrawString(row.Status, Theme.UiFont, status, columns[3], left);
            }

            graphics.DrawString(row.Down > 0 ? Theme.Rate(row.Down) : "", Theme.UiFont, down, columns[4], right);
            graphics.DrawString(row.Up > 0 ? Theme.Rate(row.Up) : "", Theme.UiFont, up, columns[5], right);
            graphics.DrawString($"{row.Peers}", Theme.UiFont, secondary, columns[6], right);

            y += RowHeight;
        }
    }

    private static void DrawProgress(Graphics graphics, Rectangle column, TorrentRow row, Brush track)
    {
        Rectangle bar = new(column.X, column.Y + (column.Height / 2) - 5, column.Width, 10);
        graphics.FillRectangle(track, bar);

        using SolidBrush fill = new(row.Fraction >= 1 ? Theme.Upload : Theme.Accent);
        graphics.FillRectangle(fill, bar.X, bar.Y, (int)(bar.Width * Math.Clamp(row.Fraction, 0, 1)), bar.Height);

        using SolidBrush text = new(Theme.TextPrimary);
        using StringFormat centred = new()
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        graphics.DrawString($"{row.Fraction * 100:N1}%", Theme.CaptionFont, text, column, centred);
    }

    /// <summary>
    /// The columns, worked out from the control's width so that the name takes
    /// whatever the fixed ones leave.
    /// </summary>
    private Rectangle[] Columns(int y, int height)
    {
        int size = 80;
        int progress = 120;
        int status = 100;
        int rate = 76;
        int peers = 50;

        int fixedPart = size + progress + status + (rate * 2) + peers + 32;
        int name = Math.Max(140, Width - fixedPart);

        int x = 12;
        Rectangle[] columns = new Rectangle[7];
        columns[0] = new Rectangle(x, y, name - 10, height);
        x += name;
        columns[1] = new Rectangle(x, y, size - 10, height);
        x += size;
        columns[2] = new Rectangle(x, y, progress - 12, height);
        x += progress;
        columns[3] = new Rectangle(x, y, status - 8, height);
        x += status;
        columns[4] = new Rectangle(x, y, rate - 8, height);
        x += rate;
        columns[5] = new Rectangle(x, y, rate - 8, height);
        x += rate;
        columns[6] = new Rectangle(x, y, peers - 10, height);

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
        graphics.DrawString(_rows.Length > 0 ? $"{_rows.Length} TORRENTS" : "TORRENTS", Theme.CaptionFont, text, columns[0], left);
        graphics.DrawString("SIZE", Theme.CaptionFont, text, columns[1], right);
        graphics.DrawString("PROGRESS", Theme.CaptionFont, text, columns[2], left);
        graphics.DrawString("STATUS", Theme.CaptionFont, text, columns[3], left);
        graphics.DrawString("DOWN", Theme.CaptionFont, text, columns[4], right);
        graphics.DrawString("UP", Theme.CaptionFont, text, columns[5], right);
        graphics.DrawString("PEERS", Theme.CaptionFont, text, columns[6], right);
    }
}
