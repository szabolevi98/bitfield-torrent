using System.Drawing.Drawing2D;

namespace Bitfield.Controls;

/// <summary>
/// The last couple of minutes of throughput, down and up.
///
/// A single number tells you almost nothing about a swarm: the interesting part
/// is the shape — whether a download is holding steady, sawing as peers choke
/// and unchoke, or tailing off because the good peers have gone.
/// </summary>
internal sealed class SpeedGraphControl : Control
{
    private const int Samples = 120;

    private readonly double[] _down = new double[Samples];
    private readonly double[] _up = new double[Samples];
    private int _next;
    private int _count;

    public SpeedGraphControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.UserPaint
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.Surface;
    }

    public void Add(double down, double up)
    {
        _down[_next] = down;
        _up[_next] = up;
        _next = (_next + 1) % Samples;
        _count = Math.Min(_count + 1, Samples);
        Invalidate();
    }

    public void Clear()
    {
        Array.Clear(_down);
        Array.Clear(_up);
        _next = 0;
        _count = 0;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics graphics = e.Graphics;
        graphics.Clear(BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;

        Rectangle plot = new(8, 8, Width - 16, Height - 24);
        if (plot.Width <= 0 || plot.Height <= 0 || _count == 0)
        {
            return;
        }

        double peak = Math.Max(_down.Max(), _up.Max());
        if (peak <= 0)
        {
            peak = 1;
        }

        // A round ceiling above the peak, so the line does not touch the top
        // and the scale does not jump on every sample.
        double ceiling = peak * 1.15;

        DrawGridlines(graphics, plot, ceiling);
        DrawSeries(graphics, plot, _up, ceiling, Theme.Upload);
        DrawSeries(graphics, plot, _down, ceiling, Theme.Accent);

        using SolidBrush caption = new(Theme.TextMuted);
        graphics.DrawString(
            $"peak {Theme.Rate(peak)} · two minutes",
            Theme.CaptionFont, caption, 8, Height - 15);
    }

    private static void DrawGridlines(Graphics graphics, Rectangle plot, double ceiling)
    {
        using Pen line = new(Theme.Border);
        using SolidBrush label = new(Theme.TextMuted);

        for (int i = 1; i <= 2; i++)
        {
            int y = plot.Bottom - (plot.Height * i / 3);
            graphics.DrawLine(line, plot.Left, y, plot.Right, y);
            graphics.DrawString(Theme.Rate(ceiling * i / 3), Theme.CaptionFont, label, plot.Left + 2, y - 13);
        }
    }

    private void DrawSeries(Graphics graphics, Rectangle plot, double[] values, double ceiling, Color colour)
    {
        PointF[] points = new PointF[_count];

        for (int i = 0; i < _count; i++)
        {
            // The ring buffer is read oldest first, so the newest sample is at
            // the right-hand edge where it is expected.
            int index = (_next - _count + i + Samples) % Samples;
            float x = plot.Left + (plot.Width * (i / (float)Math.Max(_count - 1, 1)));
            float y = plot.Bottom - (float)(plot.Height * Math.Min(values[index] / ceiling, 1));
            points[i] = new PointF(x, y);
        }

        if (points.Length < 2)
        {
            return;
        }

        PointF[] area = [.. points, new PointF(plot.Right, plot.Bottom), new PointF(plot.Left, plot.Bottom)];

        using SolidBrush fill = new(Color.FromArgb(48, colour));
        graphics.FillPolygon(fill, area);

        using Pen pen = new(colour, 1.6f);
        graphics.DrawLines(pen, points);
    }
}
