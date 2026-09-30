// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

// This project references WinForms (for the tray icon), so the unqualified names Color and
// Window are ambiguous across the two UI stacks. Same aliasing pattern used throughout.
using Window = System.Windows.Window;

namespace OmniHub.App.Wpf;

/// <summary>
/// Paints the OS-drawn window frame to match the app.
///
/// WPF only draws the client area. The title bar, its buttons and the window border are
/// drawn by the desktop window manager, which defaults to the system light theme -- which
/// is why a pure-black app was sitting under a bright white caption bar. There is no XAML
/// fix for this; the frame is not part of the visual tree. The alternative would be
/// WindowStyle="None" plus a hand-built title bar, and that is exactly the trap
/// OmniControlSuite fell into: it loses dragging, snapping, minimise animations and the
/// system menu, all of which then have to be reimplemented by hand and got subtly wrong.
///
/// So: keep the real OS window, and tell DWM what colour to paint it.
/// </summary>
public static class DwmTheme
{
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    // Windows 10 1809+. Switches the caption to the dark variant: dark background, light
    // glyphs, and correctly themed hover states on the min/max/close buttons.
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // Windows 11 22000+. Exact colours for the caption, its text and the window border.
    // These fail harmlessly (non-zero HRESULT, no exception) on Windows 10, where the
    // immersive-dark-mode flag above still does the important part.
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_TEXT_COLOR = 36;

    /// <summary>Sentinel meaning "let DWM pick", used to hand the frame back to the system.</summary>
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    /// <summary>
    /// Applies dark mode and the given caption/border/text colours to a window's frame.
    /// Requires a live HWND, so call it from OnSourceInitialized rather than the
    /// constructor, where the handle is still IntPtr.Zero.
    /// </summary>
    public static void Apply(Window window, Color caption, Color captionText, Color border)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int dark = 1;
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark);

        int captionRef = ToColorRef(caption);
        int textRef = ToColorRef(captionText);
        int borderRef = ToColorRef(border);

        TrySet(hwnd, DWMWA_CAPTION_COLOR, ref captionRef);
        TrySet(hwnd, DWMWA_TEXT_COLOR, ref textRef);
        TrySet(hwnd, DWMWA_BORDER_COLOR, ref borderRef);
    }

    /// <summary>Hands the frame back to the system default, for a light theme.</summary>
    public static void ApplySystemDefault(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int darkFlag = dark ? 1 : 0;
        TrySet(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkFlag);

        int def = DWMWA_COLOR_DEFAULT;
        TrySet(hwnd, DWMWA_CAPTION_COLOR, ref def);
        TrySet(hwnd, DWMWA_TEXT_COLOR, ref def);
        TrySet(hwnd, DWMWA_BORDER_COLOR, ref def);
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    // Windows 11 22621+. 1 is no backdrop, 3 the transient material: acrylic, blurred and tinted.
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    /// <summary>
    /// Puts one of Windows' own materials behind the whole window, frame and all -- Mica (2),
    /// Acrylic (3) or Mica Alt (4), the system's backdrop types -- or takes it away (null). The
    /// material is the desktop window manager's rather than something drawn to look like it: WPF is
    /// told to clear to nothing, the frame is extended over the client area, and DWM fills what
    /// shows through. Returns false where the build of Windows has no such material, so the caller
    /// can keep its surfaces opaque rather than going translucent over a plain black.
    /// </summary>
    public static bool SetBackdrop(Window window, int? material)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        bool on = material is not null;
        int type = material ?? 1;
        int hr;
        try { hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int)); }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
        if (on && hr != 0) on = false;

        var margins = on ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : default;
        try { DwmExtendFrameIntoClientArea(hwnd, ref margins); } catch { return false; }

        // WPF's own clear colour, black unless told otherwise, sits between the window and DWM.
        if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
            target.BackgroundColor = on ? Colors.Transparent : Colors.Black;

        // The caption's painted colour would cover the material in the title bar; hand it back.
        if (on) ApplySystemDefault(window, dark: true);
        return on;
    }

    private static void TrySet(IntPtr hwnd, int attribute, ref int value)
    {
        // Unsupported attributes return E_INVALIDARG rather than throwing, so an older
        // Windows build simply keeps the parts it does understand.
        try { DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)); }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    // DWM wants a COLORREF: 0x00BBGGRR, i.e. byte order reversed from the usual RGB.
    private static int ToColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);
}
