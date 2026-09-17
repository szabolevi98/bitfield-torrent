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
    int Peers,
    DateTimeOffset AddedOn);

/// <summary>Which column the list is ordered by.</summary>
internal enum TorrentColumn
{
    /// <summary>The order they were added, which is what a list starts as.</summary>
    Added,

    Name,
    Size,
    Progress,
    Status,
    Down,
    Up,
    Peers,
}

/// <summary>
/// Every torrent the client is running, one to a row.
///
/// The progress bar is the column that does the work: a number tells you a
/// torrent is at 61%, a bar tells you at a glance which of nine torrents is
/// nearly there and which has barely started.
///
/// Sorting lives here rather than in the window because the rows are rebuilt
/// twice a second — an order applied by whoever supplies them would be undone
/// on the next tick. Selection is kept by infohash for the same reason: sorting
/// by speed reshuffles the list constantly, and a selection held by row number
/// would wander off every time it did.
/// </summary>
internal sealed class TorrentListControl : Control
{
    private const int RowHeight = 26;
    private const int HeaderHeight = 26;

    private TorrentRow[] _rows = [];
    private readonly HashSet<InfoHash> _selected = [];
    private InfoHash? _anchor;
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

    /// <summary>The torrent the detail panel below is showing: the last one clicked.</summary>
    public InfoHash? Selected { get; private set; }

    /// <summary>Everything selected, which is what the menu acts on.</summary>
    public IReadOnlyList<InfoHash> SelectedAll => [.. _rows.Where(row => _selected.Contains(row.InfoHash)).Select(row => row.InfoHash)];

    public TorrentColumn SortColumn { get; private set; } = TorrentColumn.Added;

    public bool SortDescending { get; private set; }

    public event Action? SelectionChanged;

    /// <summary>Raised on a right-click, with the row under the cursor.</summary>
    public event Action<InfoHash, Point>? RowMenu;

    /// <summary>Raised when the user sorts, so the choice can be remembered.</summary>
    public event Action? SortChanged;

    public void SetSort(TorrentColumn column, bool descending)
    {
        SortColumn = column;
        SortDescending = descending;
        Invalidate();
    }

    public void Set(IReadOnlyList<TorrentRow> rows)
    {
        _rows = Sorted(rows);

        // Torrents that have gone take their selection with them, and the first
        // one to arrive takes it when there was none.
        _selected.RemoveWhere(hash => !_rows.Any(row => row.InfoHash == hash));

        if (Selected is { } selected && !_rows.Any(row => row.InfoHash == selected))
        {
            Selected = _rows.Length > 0 ? _rows[0].InfoHash : null;

            if (Selected is { } replacement)
            {
                _selected.Add(replacement);
            }

            SelectionChanged?.Invoke();
        }
        else if (Selected == null && _rows.Length > 0)
        {
            Selected = _rows[0].InfoHash;
            _selected.Add(_rows[0].InfoHash);
            SelectionChanged?.Invoke();
        }

        _scroll = Math.Min(_scroll, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    private TorrentRow[] Sorted(IReadOnlyList<TorrentRow> rows)
    {
        IOrderedEnumerable<TorrentRow> ordered = SortColumn switch
        {
            TorrentColumn.Name => rows.OrderBy(row => row.Name, StringComparer.CurrentCultureIgnoreCase),
            TorrentColumn.Size => rows.OrderBy(row => row.Size),
            TorrentColumn.Progress => rows.OrderBy(row => row.Fraction),
            TorrentColumn.Status => rows.OrderBy(row => row.Status, StringComparer.Ordinal),
            TorrentColumn.Down => rows.OrderBy(row => row.Down),
            TorrentColumn.Up => rows.OrderBy(row => row.Up),
            TorrentColumn.Peers => rows.OrderBy(row => row.Peers),
            _ => rows.OrderBy(row => row.AddedOn),
        };

        // A stable tiebreak, or rows with equal speeds swap places every time
        // the list is rebuilt and the whole thing shimmers.
        ordered = ordered.ThenBy(row => row.AddedOn);

        return SortDescending
            ? [.. ordered.Reverse()]
            : [.. ordered];
    }

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (e.Y < HeaderHeight)
        {
            if (e.Button == MouseButtons.Left)
            {
                SortBy(ColumnAt(e.X));
            }

            return;
        }

        int index = RowAt(e.Y);
        if (index < 0)
        {
            return;
        }

        InfoHash hash = _rows[index].InfoHash;

        if (e.Button == MouseButtons.Right && _selected.Contains(hash))
        {
            // Right-clicking inside a selection acts on the whole selection
            // rather than throwing it away, which is what every list does.
            Selected = hash;
            RowMenu?.Invoke(hash, e.Location);
            return;
        }

        Select(index, ModifierKeys);

        if (e.Button == MouseButtons.Right)
        {
            RowMenu?.Invoke(hash, e.Location);
        }
    }

    /// <summary>
    /// Plain click replaces the selection, control adds to it, shift takes
    /// everything between the anchor and here.
    /// </summary>
    private void Select(int index, Keys modifiers)
    {
        InfoHash hash = _rows[index].InfoHash;

        if (modifiers.HasFlag(Keys.Shift) && _anchor is { } anchor)
        {
            int from = Array.FindIndex(_rows, row => row.InfoHash == anchor);

            if (from >= 0)
            {
                _selected.Clear();

                for (int i = Math.Min(from, index); i <= Math.Max(from, index); i++)
                {
                    _selected.Add(_rows[i].InfoHash);
                }
            }
        }
        else if (modifiers.HasFlag(Keys.Control))
        {
            if (!_selected.Add(hash))
            {
                _selected.Remove(hash);
            }

            _anchor = hash;
        }
        else
        {
            _selected.Clear();
            _selected.Add(hash);
            _anchor = hash;
        }

        Selected = hash;
        SelectionChanged?.Invoke();
        Invalidate();
    }

    public void SelectAll()
    {
        _selected.Clear();

        foreach (TorrentRow row in _rows)
        {
            _selected.Add(row.InfoHash);
        }

        SelectionChanged?.Invoke();
        Invalidate();
    }

    private void SortBy(TorrentColumn column)
    {
        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;

            // Names read best ascending; everything else is a number somebody
            // clicked on to see the biggest of.
            SortDescending = column is not (TorrentColumn.Name or TorrentColumn.Added);
        }

        _rows = Sorted(_rows);
        SortChanged?.Invoke();
        Invalidate();
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

    private TorrentColumn ColumnAt(int x)
    {
        Rectangle[] columns = Columns(0, HeaderHeight);
        TorrentColumn[] order =
        [
            TorrentColumn.Name, TorrentColumn.Size, TorrentColumn.Progress,
            TorrentColumn.Status, TorrentColumn.Down, TorrentColumn.Up, TorrentColumn.Peers,
        ];

        for (int i = columns.Length - 1; i >= 0; i--)
        {
            if (x >= columns[i].X - 6)
            {
                return order[i];
            }
        }

        return TorrentColumn.Name;
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
            graphics.DrawString("no torrents — add one from the File menu", Theme.UiFont, empty, 12, HeaderHeight + 10);
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
            bool selected = _selected.Contains(row.InfoHash);

            using (SolidBrush background = new(selected ? Theme.Selection : i % 2 == 1 ? Theme.SurfaceRaised : Theme.Surface))
            {
                graphics.FillRectangle(background, 0, y, Width, RowHeight);
            }

            if (Selected == row.InfoHash)
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

        (string Caption, TorrentColumn Column, StringFormat Format)[] headers =
        [
            (_rows.Length > 0 ? $"{_rows.Length} TORRENTS" : "TORRENTS", TorrentColumn.Name, left),
            ("SIZE", TorrentColumn.Size, right),
            ("PROGRESS", TorrentColumn.Progress, left),
            ("STATUS", TorrentColumn.Status, left),
            ("DOWN", TorrentColumn.Down, right),
            ("UP", TorrentColumn.Up, right),
            ("PEERS", TorrentColumn.Peers, right),
        ];

        for (int i = 0; i < headers.Length; i++)
        {
            (string caption, TorrentColumn column, StringFormat format) = headers[i];
            bool sorted = SortColumn == column;

            using SolidBrush text = new(sorted ? Theme.TextSecondary : Theme.TextMuted);
            graphics.DrawString(caption, Theme.CaptionFont, text, columns[i], format);

            if (sorted)
            {
                DrawArrow(graphics, columns[i], format == right);
            }
        }
    }

    /// <summary>
    /// The little mark saying which way the list is ordered. Drawn beside the
    /// caption rather than replacing it, so the column still says what it is.
    /// </summary>
    private void DrawArrow(Graphics graphics, Rectangle column, bool rightAligned)
    {
        int size = 4;
        int x = rightAligned ? column.X - 2 : column.X + Math.Min(column.Width - 10, TextWidth(column)) + 6;
        int y = column.Y + (column.Height / 2);

        Point[] arrow = SortDescending
            ? [new Point(x - size, y - 2), new Point(x + size, y - 2), new Point(x, y + 3)]
            : [new Point(x - size, y + 2), new Point(x + size, y + 2), new Point(x, y - 3)];

        using SolidBrush brush = new(Theme.Accent);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        graphics.FillPolygon(brush, arrow);
        graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.Default;
    }

    private int TextWidth(Rectangle column) => Math.Min(column.Width, 76);
}
