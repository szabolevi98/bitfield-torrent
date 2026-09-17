using System.Runtime.InteropServices;

namespace Bitfield.Native;

/// <summary>
/// The few pieces of Windows this application has to ask for directly.
/// </summary>
internal static partial class NativeMethods
{
    private const int UseImmersiveDarkMode = 20;

    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpFrameChanged = 0x0020;

    /// <summary>
    /// Paints a window's title bar to match a dark application. Windows draws
    /// the frame itself and has no idea what is inside it, so a dark window with
    /// a light bar across the top is what you get by default — which looks less
    /// like a theme than like something unfinished.
    /// </summary>
    internal static void UseDarkTitleBar(nint window)
    {
        int enabled = 1;

        try
        {
            DwmSetWindowAttribute(window, UseImmersiveDarkMode, ref enabled, sizeof(int));

            // The frame is only repainted when it is told the style changed, so
            // without this the title bar keeps its light colours until the
            // window is resized.
            SetWindowPos(
                window, nint.Zero, 0, 0, 0, 0,
                SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
        }
        catch (DllNotFoundException)
        {
            // An older Windows than this asks for; the bar stays light.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// Opts the process into dark common controls. A scroll bar is drawn by
    /// Windows rather than by the control it belongs to, so it stays white
    /// however dark the list behind it is painted — a white stripe down the
    /// side of a dark window. The two entry points are exported by ordinal
    /// only, so they are bound that way.
    /// </summary>
    internal static void UseDarkCommonControls()
    {
        try
        {
            SetPreferredAppMode(AllowDarkMode);
            FlushMenuThemes();
        }
        catch (EntryPointNotFoundException)
        {
            // Windows 10 before 1903 does not export these.
        }
        catch (DllNotFoundException)
        {
        }
    }

    /// <summary>
    /// Puts one control on the dark visual style, which is what turns its
    /// scroll bars from white to dark once the process has asked for dark
    /// common controls.
    /// </summary>
    internal static void UseDarkStyle(nint control)
    {
        try
        {
            SetWindowTheme(control, "DarkMode_Explorer", nint.Zero);
        }
        catch (DllNotFoundException)
        {
        }
    }

    /// <summary>Allow dark mode where the application asks for it.</summary>
    private const int AllowDarkMode = 1;

    [LibraryImport("uxtheme.dll", EntryPoint = "#135", SetLastError = false)]
    private static partial int SetPreferredAppMode(int mode);

    [LibraryImport("uxtheme.dll", EntryPoint = "#136", SetLastError = false)]
    private static partial void FlushMenuThemes();

    [LibraryImport("uxtheme.dll", EntryPoint = "SetWindowTheme", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SetWindowTheme(nint window, string subAppName, nint subIdList);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint window, int attribute, ref int value, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
