using Bitfield.Core.Download;

namespace Bitfield.Controls;

/// <summary>One file of a torrent, as the file list shows it.</summary>
internal sealed record FileRow(int Index, string Path, long Size, double Fraction, FilePriority Priority, bool Shared);

/// <summary>
/// The files inside a torrent, and how much each of them is wanted.
///
/// Setting a file aside is the one place where the client does something with
/// lasting consequences from a single click, so the row says plainly when a
/// skipped file will still be partly written: it shares a piece with a file
/// that is wanted, and that piece cannot be had in halves.
/// </summary>
internal sealed class FileListControl : Control
{
    private const int RowHeight = 24;
    private const int HeaderHeight = 24;

    private FileRow[] _rows = [];
    private int _scroll;
    private int _hovered = -1;

    public FileListControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
    }

    /// <summary>Raised when the user picks a new priority for a file.</summary>
    public event Action<int, FilePriority>? PriorityChanged;

    public void Set(IReadOnlyList<FileRow> rows)
    {
        _rows = [.. rows];
        _scroll = Math.Min(_scroll, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    public void Clear()
    {
        _rows = [];
        _scroll = 0;
        Invalidate();
    }

    private int VisibleRows => Math.Max(1, (Height - HeaderHeight) / RowHeight);

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        _scroll = Math.Clamp(_scroll - (e.Delta / 120 * 3), 0, Math.Max(0, _rows.Length - VisibleRows));
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int row = RowAt(e.Y);
        if (row != _hovered)
        {
            _hovered = row;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        int index = RowAt(e.Y);
        if (index < 0)
        {
            return;
        }

        FileRow row = _rows[index];

        ContextMenuStrip menu = new()
        {
            BackColor = Theme.SurfaceRaised,
            ForeColor = Theme.TextPrimary,
            Font = Theme.UiFont,
            ShowImageMargin = false,
        };

        foreach (FilePriority priority in new[]
                 { FilePriority.High, FilePriority.Normal, FilePriority.Low, FilePriority.Skip })
        {
            ToolStripMenuItem item = new(Describe(priority), null, (_, _) => PriorityChanged?.Invoke(row.Index, priority))
            {
                Checked = row.Priority == priority,
            };

            menu.Items.Add(item);
        }

        menu.Show(this, e.Location);
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

    internal static string Describe(FilePriority priority) => priority switch
    {
        FilePriority.Skip => "do not download",
        FilePriority.Low => "low",
        FilePriority.High => "high",
        _ => "normal",
    };

    private static Color Colour(FilePriority priority) => priority switch
    {
        FilePriority.Skip => Theme.TextMuted,
        FilePriority.Low => Theme.TextSecondary,
        FilePriority.High => Theme.Warning,
        _ => Theme.TextPrimary,
    };

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using StringFormat left = new(StringFormatFlags.NoWrap)
        {
            Trimming = StringTrimming.EllipsisPath,
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
            graphics.DrawString("no torrent selected", Theme.UiFont, empty, 12, HeaderHeight + 8);
            return;
        }

        using SolidBrush secondary = new(Theme.TextSecondary);
        using SolidBrush track = new(Theme.PieceMissing);

        int y = HeaderHeight;

        for (int i = _scroll; i < _rows.Length && y < Height; i++)
        {
            FileRow row = _rows[i];

            if (i == _hovered)
            {
                using SolidBrush hover = new(Theme.SurfaceHover);
                graphics.FillRectangle(hover, 0, y, Width, RowHeight);
            }
            else if (i % 2 == 1)
            {
                using SolidBrush stripe = new(Theme.SurfaceRaised);
                graphics.FillRectangle(stripe, 0, y, Width, RowHeight);
            }

            Rectangle[] columns = Columns(y, RowHeight);

            using (SolidBrush name = new(row.Priority == FilePriority.Skip ? Theme.TextMuted : Theme.TextPrimary))
            {
                graphics.DrawString(row.Path, Theme.UiFont, name, columns[0], left);
            }

            graphics.DrawString(Theme.Bytes(row.Size), Theme.UiFont, secondary, columns[1], right);

            Rectangle bar = new(columns[2].X, y + (RowHeight / 2) - 4, columns[2].Width, 8);
            graphics.FillRectangle(track, bar);

            using (SolidBrush fill = new(row.Priority == FilePriority.Skip ? Theme.TextMuted : Theme.Accent))
            {
                graphics.FillRectangle(fill, bar.X, bar.Y,
                    (int)(bar.Width * Math.Clamp(row.Fraction, 0, 1)), bar.Height);
            }

            using (SolidBrush priority = new(Colour(row.Priority)))
            {
                string text = row.Priority == FilePriority.Skip && row.Shared
                    ? "do not download*"
                    : Describe(row.Priority);

                graphics.DrawString(text, Theme.UiFont, priority, columns[3], left);
            }

            y += RowHeight;
        }

        // The asterisk above needs explaining, and a footnote is cheaper than
        // finding out later that a file you set aside is not empty.
        if (_rows.Any(row => row is { Priority: FilePriority.Skip, Shared: true }))
        {
            using SolidBrush note = new(Theme.TextMuted);
            graphics.DrawString(
                "* shares a piece with a file that is wanted, so part of it arrives anyway",
                Theme.CaptionFont, note, 10, Height - 16);
        }
    }

    private Rectangle[] Columns(int y, int height)
    {
        int size = 80;
        int progress = 110;
        int priority = 130;
        int name = Math.Max(140, Width - size - progress - priority - 34);

        int x = 10;
        Rectangle[] columns = new Rectangle[4];
        columns[0] = new Rectangle(x, y, name - 10, height);
        x += name;
        columns[1] = new Rectangle(x, y, size - 10, height);
        x += size;
        columns[2] = new Rectangle(x, y, progress - 12, height);
        x += progress;
        columns[3] = new Rectangle(x, y, priority - 8, height);

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
        graphics.DrawString(_rows.Length > 0 ? $"{_rows.Length:N0} FILES" : "FILES", Theme.CaptionFont, text, columns[0], left);
        graphics.DrawString("SIZE", Theme.CaptionFont, text, columns[1], right);
        graphics.DrawString("HAVE", Theme.CaptionFont, text, columns[2], left);
        graphics.DrawString("PRIORITY", Theme.CaptionFont, text, columns[3], left);
    }
}
