using System.Drawing.Drawing2D;

namespace Bitfield;

/// <summary>
/// One place for the palette, so the custom drawn controls and the stock
/// Windows Forms ones end up the same colour.
///
/// The accent is the colour a piece turns when it has arrived and verified, so
/// it is the colour the window is mostly made of once a download is going.
/// </summary>
internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(0x0E, 0x13, 0x1C);
    public static readonly Color Surface = Color.FromArgb(0x15, 0x1C, 0x28);
    public static readonly Color SurfaceRaised = Color.FromArgb(0x1C, 0x25, 0x33);
    public static readonly Color SurfaceHover = Color.FromArgb(0x25, 0x30, 0x41);
    public static readonly Color Border = Color.FromArgb(0x28, 0x33, 0x45);

    public static readonly Color TextPrimary = Color.FromArgb(0xE8, 0xEE, 0xF7);
    public static readonly Color TextSecondary = Color.FromArgb(0x90, 0xA2, 0xB9);
    public static readonly Color TextMuted = Color.FromArgb(0x5E, 0x6E, 0x86);

    public static readonly Color Accent = Color.FromArgb(0x5B, 0x9C, 0xFF);
    public static readonly Color AccentPressed = Color.FromArgb(0x3B, 0x7E, 0xE8);
    public static readonly Color Selection = Color.FromArgb(0x1B, 0x30, 0x4C);

    /// <summary>Bytes going out, which is the other half of a swarm.</summary>
    public static readonly Color Upload = Color.FromArgb(0x54, 0xD1, 0x9B);

    public static readonly Color Warning = Color.FromArgb(0xE8, 0xB4, 0x50);

    /// <summary>A piece being fetched right now, between missing and held.</summary>
    public static readonly Color PieceBusy = Color.FromArgb(0x33, 0x52, 0x7E);

    public static readonly Color PieceMissing = Color.FromArgb(0x1A, 0x22, 0x2F);

    public static readonly Font UiFont = new("Segoe UI", 9F);
    public static readonly Font UiFontBold = new("Segoe UI Semibold", 9F);
    public static readonly Font NumberFont = new("Segoe UI Light", 17F);
    public static readonly Font CaptionFont = new("Segoe UI", 8F);

    public static void FillRoundedRectangle(Graphics graphics, Rectangle bounds, int radius, Color fill)
    {
        using GraphicsPath path = RoundedPath(bounds, radius);
        using SolidBrush brush = new(fill);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.FillPath(brush, path);
    }

    public static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        int diameter = Math.Max(1, radius * 2);
        GraphicsPath path = new();

        if (diameter >= bounds.Width || diameter >= bounds.Height)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>Bytes in the units people read them in.</summary>
    public static string Bytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        int unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes:N0} B" : $"{value:N1} {units[unit]}";
    }

    public static string Rate(double bytesPerSecond) => $"{Bytes((long)bytesPerSecond)}/s";

    public static string Duration(TimeSpan span) => span switch
    {
        { TotalDays: >= 1 } => $"{(int)span.TotalDays}d {span.Hours}h",
        { TotalHours: >= 1 } => $"{(int)span.TotalHours}h {span.Minutes}m",
        { TotalMinutes: >= 1 } => $"{(int)span.TotalMinutes}m {span.Seconds}s",
        _ => $"{(int)span.TotalSeconds}s",
    };
}
