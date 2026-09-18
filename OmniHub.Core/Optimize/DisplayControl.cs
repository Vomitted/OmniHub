// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Runtime.InteropServices;

namespace OmniHub.Core.Optimize;

/// <summary>
/// Refresh-rate control for the primary display.
///
/// This is the part of OMEN Gaming Hub's Eco mode that actually saves measurable power on a
/// laptop: dropping a 144 Hz panel to 60 Hz cuts both panel and GPU work, and unlike the SMU
/// power limits on this machine it is not something firmware can quietly ignore.
///
/// Changes are deliberately NOT written to the registry. Passing CDS_UPDATEREGISTRY would make
/// a temporary eco switch survive a reboot, so a crash mid-eco would leave the panel at 60 Hz
/// permanently with nothing on screen explaining why. Session-only means the worst case is
/// fixed by logging out.
/// </summary>
public static class DisplayControl
{
    private const int EnumCurrentSettings = -1;
    private const int DmDisplayFrequency = 0x400000;
    private const int DispChangeSuccessful = 0;
    private const int DispChangeRestart = 1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2;
        public int dmPanningWidth, dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string? deviceName, int modeNum, ref DevMode devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(
        string? deviceName, ref DevMode devMode, IntPtr hwnd, uint flags, IntPtr param);

    /// <summary>The primary display's current refresh rate in Hz, or null if it cannot be read.</summary>
    public static int? CurrentRefreshHz()
    {
        var mode = new DevMode { dmDeviceName = string.Empty, dmFormName = string.Empty };
        mode.dmSize = (short)Marshal.SizeOf<DevMode>();
        return EnumDisplaySettings(null, EnumCurrentSettings, ref mode) ? mode.dmDisplayFrequency : null;
    }

    /// <summary>
    /// Every refresh rate the primary display supports at its CURRENT resolution.
    ///
    /// Filtered to the current resolution deliberately: the raw enumeration includes modes at
    /// other sizes, and offering a rate that silently also changes resolution is not what
    /// anyone means by "set the refresh rate".
    /// </summary>
    public static IReadOnlyList<int> AvailableRefreshHz()
    {
        var current = new DevMode { dmDeviceName = string.Empty, dmFormName = string.Empty };
        current.dmSize = (short)Marshal.SizeOf<DevMode>();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref current)) return Array.Empty<int>();

        var rates = new SortedSet<int>();
        for (int i = 0; ; i++)
        {
            var mode = new DevMode { dmDeviceName = string.Empty, dmFormName = string.Empty };
            mode.dmSize = (short)Marshal.SizeOf<DevMode>();
            if (!EnumDisplaySettings(null, i, ref mode)) break;

            if (mode.dmPelsWidth == current.dmPelsWidth
                && mode.dmPelsHeight == current.dmPelsHeight
                && mode.dmDisplayFrequency > 1)
            {
                rates.Add(mode.dmDisplayFrequency);
            }
        }
        return rates.ToList();
    }

    /// <summary>Sets the primary display's refresh rate, leaving resolution alone.</summary>
    public static TuningResult SetRefreshHz(int hz)
    {
        var mode = new DevMode { dmDeviceName = string.Empty, dmFormName = string.Empty };
        mode.dmSize = (short)Marshal.SizeOf<DevMode>();
        if (!EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
            return new TuningResult(false, "Could not read the current display mode.");

        if (mode.dmDisplayFrequency == hz)
            return new TuningResult(true, $"Display already at {hz} Hz.");

        // Only the frequency field is declared, so the driver keeps everything else as-is.
        mode.dmFields = DmDisplayFrequency;
        mode.dmDisplayFrequency = hz;

        int result = ChangeDisplaySettingsEx(null, ref mode, IntPtr.Zero, 0, IntPtr.Zero);
        return result switch
        {
            DispChangeSuccessful => new TuningResult(true, $"Display set to {hz} Hz."),
            DispChangeRestart => new TuningResult(false, $"{hz} Hz needs a restart on this display."),
            _ => new TuningResult(false, $"The display refused {hz} Hz (code {result})."),
        };
    }
}
