using System.Drawing.Drawing2D;

namespace Bitfield.Controls;

/// <summary>
/// The window's centrepiece: one cell per piece, in the torrent's own order,
/// filling in as the download proceeds.
///
/// It is the <c>bitfield</c> message drawn out — the same thing a peer sends to
/// say what it holds — and it shows at a glance what no number does: whether
/// the pieces are arriving spread across the torrent or all from one place,
/// how many are in flight at once, and where the last stubborn gaps are.
///
/// The cells are sized to fill the control rather than fixed, because a torrent
/// may have sixty pieces or sixty thousand and both have to be legible.
/// </summary>
internal sealed class PieceMapControl : Control
{
    private const int MinimumCell = 2;
    private const int Gap = 1;

    private bool[] _have = [];
    private bool[] _busy = [];
    private int _pieceCount;
    private int _hovered = -1;
    private Rectangle _grid;
    private int _cell;
    private int _columns;

    public PieceMapControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
        Font = Theme.CaptionFont;
    }

    /// <summary>How many pieces are held, shown in the corner.</summary>
    public int HeldCount { get; private set; }

    public void SetPieces(int pieceCount, ReadOnlySpan<byte> haveBits, int[] inProgress)
    {
        if (_pieceCount != pieceCount)
        {
            _pieceCount = pieceCount;
            _have = new bool[pieceCount];
            _busy = new bool[pieceCount];
            LayoutGrid();
        }

        int held = 0;
        for (int piece = 0; piece < pieceCount; piece++)
        {
            bool set = piece >> 3 < haveBits.Length && (haveBits[piece >> 3] & (0x80 >> (piece & 7))) != 0;
            _have[piece] = set;
            held += set ? 1 : 0;
        }

        Array.Clear(_busy);
        foreach (int piece in inProgress)
        {
            if ((uint)piece < (uint)pieceCount)
            {
                _busy[piece] = true;
            }
        }

        HeldCount = held;
        Invalidate();
    }

    public void Clear()
    {
        _pieceCount = 0;
        _have = [];
        _busy = [];
        HeldCount = 0;
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        LayoutGrid();
    }

    /// <summary>
    /// Chooses the largest square cell that fits every piece into the control.
    /// Tried from large to small rather than solved for, because the number of
    /// columns changes what fits and a couple of dozen attempts cost nothing.
    /// </summary>
    private void LayoutGrid()
    {
        _grid = new Rectangle(10, 10, Math.Max(1, Width - 20), Math.Max(1, Height - 28));

        if (_pieceCount == 0)
        {
            _cell = 0;
            _columns = 0;
            return;
        }

        for (int cell = 24; cell >= MinimumCell; cell--)
        {
            int step = cell + Gap;
            int columns = Math.Max(1, (_grid.Width + Gap) / step);
            int rows = (_pieceCount + columns - 1) / columns;

            if ((rows * step) - Gap <= _grid.Height)
            {
                _cell = cell;
                _columns = columns;
                return;
            }
        }

        // More pieces than pixels: the smallest cell, and the map scrolls off
        // the bottom rather than lying about how much is there.
        _cell = MinimumCell;
        _columns = Math.Max(1, (_grid.Width + Gap) / (MinimumCell + Gap));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        int piece = PieceAt(e.Location);
        if (piece != _hovered)
        {
            _hovered = piece;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if (_hovered >= 0)
        {
            _hovered = -1;
            Invalidate();
        }
    }

    private int PieceAt(Point point)
    {
        if (_cell == 0 || !_grid.Contains(point))
        {
            return -1;
        }

        int step = _cell + Gap;
        int column = (point.X - _grid.X) / step;
        int row = (point.Y - _grid.Y) / step;

        if (column >= _columns)
        {
            return -1;
        }

        int piece = (row * _columns) + column;
        return piece < _pieceCount ? piece : -1;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);

        if (_pieceCount == 0)
        {
            using SolidBrush empty = new(Theme.TextMuted);
            using StringFormat centred = new() { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            graphics.DrawString("no torrent open", Theme.UiFont, empty, ClientRectangle, centred);
            return;
        }

        int step = _cell + Gap;

        using SolidBrush missing = new(Theme.PieceMissing);
        using SolidBrush busy = new(Theme.PieceBusy);
        using SolidBrush held = new(Theme.Accent);
        using SolidBrush hovered = new(Theme.TextPrimary);

        for (int piece = 0; piece < _pieceCount; piece++)
        {
            int column = piece % _columns;
            int row = piece / _columns;

            int x = _grid.X + (column * step);
            int y = _grid.Y + (row * step);

            if (y > _grid.Bottom)
            {
                break;
            }

            Brush brush = piece == _hovered ? hovered : _have[piece] ? held : _busy[piece] ? busy : missing;
            graphics.FillRectangle(brush, x, y, _cell, _cell);
        }

        DrawCaption(graphics);
    }

    private void DrawCaption(Graphics graphics)
    {
        string caption = _hovered >= 0
            ? $"piece {_hovered:N0} — {(_have[_hovered] ? "held" : _busy[_hovered] ? "arriving" : "missing")}"
            : $"{HeldCount:N0} of {_pieceCount:N0} pieces";

        using SolidBrush text = new(_hovered >= 0 ? Theme.TextSecondary : Theme.TextMuted);
        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        graphics.DrawString(caption, Theme.CaptionFont, text, 10, Height - 16);
    }
}
