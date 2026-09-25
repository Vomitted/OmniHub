// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted
//
// Renders OmniHub's real main window -- every workspace, in any interface and theme -- to PNG, so a
// change to the interface can be looked at rather than reasoned about. See README.md; run it through
// run.ps1, which is what puts it at Low integrity on a desktop nobody can see.
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

internal static class Snapshot
{
    const BindingFlags Any = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static string AppDir = "";
    static StreamWriter? LogFile;

    static void Say(string s)
    {
        s = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s;
        Console.WriteLine(s);
        LogFile?.WriteLine(s);
    }

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length < 2) { Console.WriteLine("usage: Snapshot <app build folder> <output folder> [plan]"); return 2; }

        AppDir = Path.GetFullPath(args[0]);
        string outDir = Path.GetFullPath(args[1]);
        string plan = args.Length > 2 ? args[2] : "ws";
        Directory.CreateDirectory(outDir);
        LogFile = new StreamWriter(Path.Combine(outDir, "snapshot.log"), append: false) { AutoFlush = true };

        AssemblyLoadContext.Default.Resolving += (ctx, name) =>
        {
            foreach (var p in new[] {
                Path.Combine(AppDir, "runtimes", "win", "lib", "net8.0", name.Name + ".dll"),
                Path.Combine(AppDir, name.Name + ".dll") })
                if (File.Exists(p)) return ctx.LoadFromAssemblyPath(p);
            return null;
        };

        try
        {
            if (Precondition() is { } breach) { Say("REFUSING TO RUN: " + breach); return 3; }
            return Render(outDir, plan);
        }
        catch (Exception ex) { Say("FAILED: " + ex); return 1; }
    }

    // ------------------------------------------------------------------ sandbox

    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr p, uint a, out IntPtr t);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr t, int c, IntPtr i, int l, out int r);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
    [DllImport("advapi32.dll")] static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();

    static int IntegrityRid()
    {
        OpenProcessToken(GetCurrentProcess(), 0x0008, out var tok);
        GetTokenInformation(tok, 25, IntPtr.Zero, 0, out int len);
        var buf = Marshal.AllocHGlobal(len);
        GetTokenInformation(tok, 25, buf, len, out _);
        var sid = Marshal.ReadIntPtr(buf);
        int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
        return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
    }

    /// <summary>
    /// The window about to be built could command the fans if it were allowed to, and the real
    /// OmniHub is usually running beside it. So nothing is constructed unless Windows is actually
    /// denying this process the settings file, HP's BIOS interface and the PawnIO driver.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    static string? Precondition()
    {
        int rid = IntegrityRid();
        if (rid != 0x1000) return $"integrity level 0x{rid:X} is not Low; run through run.ps1";

        string probe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                    "OmniHub", $"snapshot-writetest-{Guid.NewGuid():N}.tmp");
        try { File.WriteAllText(probe, "x"); File.Delete(probe); return "the settings folder accepted a write"; }
        catch (UnauthorizedAccessException) { }
        catch (DirectoryNotFoundException) { }

        // The application's own hardware path, not a reimplementation of it.
        using var ctx = new OmniHub.Core.Hardware.HardwareContext();
        if (ctx.VendorSupported) return "the HP BIOS interface is reachable";
        if (ctx.Smu is not null) return "the SMU opened";

        Say("sandbox holds: Low integrity, settings write denied, no vendor interface, no SMU");
        return null;
    }

    // ------------------------------------------------------------------ rendering

    static void Pump(int ms)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(16);
        }
    }

    static object? Field(object o, string name) => o.GetType().GetField(name, Any)!.GetValue(o);
    static object? Call(object o, string name, params object?[] a) => o.GetType().GetMethod(name, Any)!.Invoke(o, a);

    static void SavePng(Window win, string path)
    {
        var root = (FrameworkElement)win.Content;
        root.UpdateLayout();
        double w = root.ActualWidth, h = root.ActualHeight;
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(win.Background ?? Brushes.Black, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                             null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
        Say($"wrote {Path.GetFileName(path)}");
    }

    /// <summary>
    /// The window swaps pages instantly when Windows has client-area animation off, and otherwise
    /// places the page in a fade's Completed handler, which never runs off-screen. This flips WPF's
    /// cached copy of the setting in this process only; the system setting is untouched.
    /// </summary>
    static void DisableClientAreaAnimationInThisProcess()
    {
        _ = SystemParameters.ClientAreaAnimation;
        typeof(SystemParameters).GetField("_clientAreaAnimation", Any)?.SetValue(null, false);
    }

    static void Show(Window win, int index)
    {
        // Start-minimised hides the window on Loaded, and a hidden window never loads its pages.
        // It is on a desktop nobody can see, so visible costs nothing.
        if (!win.IsVisible) { win.Show(); Pump(300); }
        Call(win, "ShowWorkspace", index);
        Call(win, "SelectNavItem", index);
    }

    /// <summary>
    /// Review renders only. Replays the last rows of this machine's own thermal log into the
    /// window's metric source, so a layout can be judged with real-shaped figures: the sandbox
    /// has no hardware access, and without this every live reading shows as unavailable. Never
    /// use such an image as evidence of what the application measured.
    /// </summary>
    static void Replay(Window win, string csv)
    {
        var metrics = Field(win, "_metrics");
        if (metrics is null || !File.Exists(csv)) return;
        var set = metrics.GetType().GetMethod("Set", Any)!;

        string[] lines;
        using (var fs = new FileStream(csv, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        using (var sr = new StreamReader(fs)) lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var head = lines[0].Trim().Split(',');
        int Col(string n) => Array.IndexOf(head, n);
        double? Num(string[] r, string n) =>
            Col(n) is var i && i >= 0 && i < r.Length
            && double.TryParse(r[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v >= 0 ? v : null;

        double? fan = null;
        string? limit = null;
        foreach (var line in lines.Skip(Math.Max(1, lines.Length - 40)))
        {
            var r = line.Trim().Split(',');
            if (Num(r, "fan1_raw") is { } raw) fan = raw * 100;
            foreach (var (key, value) in new[] { ("cpu", Num(r, "temp_c")), ("gpu", Num(r, "gpu_c")), ("fan", fan),
                                                 ("pkg", Num(r, "pkg_w")), ("gpuw", Num(r, "gpu_w")), ("limit", Num(r, "limit_pct")) })
                set.Invoke(metrics, new object?[] { key, value });
            if (Col("limit") is var li && li >= 0 && li < r.Length && r[li].Length > 0) limit = r[li];
        }

        metrics.GetType().GetProperty("BindingLimitName", Any)!.SetValue(metrics, limit);
        if (metrics.GetType().GetField("Updated", Any)?.GetValue(metrics) is Action updated) updated();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static int Render(string outDir, string plan)
    {
        typeof(Application).GetField("_resourceAssembly", Any)!.SetValue(null, typeof(OmniHub.App.App).Assembly);

        // A plain Application, never OmniHub's App. Constructing any Application queues its
        // OnStartup, and OmniHub's is the real launch path: the single-instance guard, which puts
        // an "already running" dialog in front of the user, and, when it wins, a second main window.
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        foreach (var dict in new[] { "Wpf/Palettes/OledBlack.xaml", "Wpf/Theme.xaml", "Wpf/Styles.xaml" })
            app.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"pack://application:,,,/OmniHub;component/{dict}", UriKind.Absolute),
            });

        // What App.OnStartup does before it builds the window, read-only.
        var startup = OmniHub.App.AppSettings.Load();
        OmniHub.App.Wpf.ThemeManager.ApplyDensity(startup.Density);
        if (startup.BuildCustomPalette() is { } custom) OmniHub.App.Wpf.ThemeManager.SetCustom(custom);
        OmniHub.App.Wpf.ThemeManager.Apply(startup.ThemeName);

        DisableClientAreaAnimationInThisProcess();
        var win = new OmniHub.App.Wpf.MainWindow();

        // A second OmniHub icon beside the real one is an invitation to exit the wrong one.
        if (Field(win, "_trayIcon") is System.ComponentModel.Component tray)
            tray.GetType().GetProperty("Visible")!.SetValue(tray, false);

        // Nothing built from here on may start an automation, even one the sandbox would refuse.
        var settings = (OmniHub.App.AppSettings)Field(win, "_settings")!;
        settings.AutoPowerPlan = false;
        settings.AutoEcoEnabled = false;
        settings.AutoEcoOnBattery = false;
        settings.GameRulesEnabled = false;
        settings.AutoSwitchProfiles = false;
        settings.StartupAdaptive = false;
        settings.OverlayEnabled = false;

        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -32000; win.Top = -32000;
        win.Width = int.Parse(Environment.GetEnvironmentVariable("SNAPSHOT_WIDTH") ?? "1100");
        win.Height = int.Parse(Environment.GetEnvironmentVariable("SNAPSHOT_HEIGHT") ?? "740");
        win.ShowActivated = false;
        win.ShowInTaskbar = false;
        win.Show();
        Pump(1200);

        string? replay = Environment.GetEnvironmentVariable("SNAPSHOT_REPLAY");
        var layout = (OmniHub.Core.Workspaces.WorkspaceLayout)Field(win, "_layout")!;
        string theme = settings.ThemeName ?? "theme";
        string iface = settings.Interface.ToString();

        void Capture(int i)
        {
            Show(win, i);
            Pump(900);
            if (replay is { Length: > 0 }) { Replay(win, replay); Pump(300); }
            string name = new(layout.Workspaces[i].Name.Where(char.IsLetterOrDigit).ToArray());
            SavePng(win, Path.Combine(outDir, $"{iface}-{theme}-{i}-{name}.png"));
        }

        // Steps, comma separated: "ws" for every workspace, a number for one, "theme=Midnight",
        // "iface=Cockpit". Order matters: a step applies to everything after it.
        foreach (string step in plan.Split(','))
        {
            if (step.StartsWith("theme=")) { theme = step[6..]; OmniHub.App.Wpf.ThemeManager.Apply(theme); Pump(500); }
            else if (step.StartsWith("iface="))
            {
                var mode = Enum.Parse<OmniHub.App.InterfaceMode>(step[6..]);
                win.SetInterfaceMode(mode);
                iface = mode.ToString();
                Pump(700);
            }
            else if (step == "ws") for (int i = 0; i < layout.Workspaces.Count; i++) Capture(i);
            else if (int.TryParse(step, out int only)) Capture(only);
        }

        try { Call(win, "Cleanup"); } catch { }
        Environment.Exit(0);
        return 0;
    }
}
