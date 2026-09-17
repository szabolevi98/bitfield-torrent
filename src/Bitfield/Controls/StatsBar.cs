namespace Bitfield.Controls;

/// <summary>
/// The row of figures above the piece map: a handful of numbers that say where
/// a download stands without having to read anything else.
/// </summary>
internal sealed class StatsBar : Control
{
    private (string Caption, string Value, Color Colour)[] _cards = [];

    public StatsBar()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Background;
        Height = 74;
    }

    public void Set(params (string Caption, string Value, Color Colour)[] cards)
    {
        _cards = cards;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);

        if (_cards.Length == 0)
        {
            return;
        }

        graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        const int gap = 8;
        int width = (Width - (gap * (_cards.Length - 1))) / _cards.Length;

        for (int i = 0; i < _cards.Length; i++)
        {
            Rectangle card = new(i * (width + gap), 0, width, Height);
            Theme.FillRoundedRectangle(graphics, card, 6, Theme.Surface);

            (string caption, string value, Color colour) = _cards[i];

            using SolidBrush captionBrush = new(Theme.TextMuted);
            using SolidBrush valueBrush = new(colour);

            graphics.DrawString(caption.ToUpperInvariant(), Theme.CaptionFont, captionBrush, card.X + 12, card.Y + 10);
            graphics.DrawString(value, Theme.NumberFont, valueBrush, card.X + 9, card.Y + 26);
        }
    }
}
