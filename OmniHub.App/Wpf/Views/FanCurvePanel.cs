// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OmniHub.App.Wpf.Controls;
using OmniHub.Core.Fan;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// The curve in force, with the machine's present position marked on it.
///
/// This application exists to replace one fan table with another, and until now the only place to
/// see which one it is using was the editor you change it in. On a workspace beside a temperature
/// and a fan speed it answers the question those two raise: not just how hot and how loud, but
/// whether that is what the curve asked for.
///
/// Read-only on purpose. Editing lives on the Fans screen, which owns the rails, the floor and the
/// apply button; a second place to change the curve would be a second place for the two to
/// disagree about which rail is being edited.
/// </summary>
public sealed class FanCurvePanel : UserControl
{
    private readonly FanService _service;
    private readonly OmniHub.Core.Hardware.HardwareContext _ctx;
    private readonly FanCurveChart _chart = new();
    private readonly TextBlock _foot = new();

    public FanCurvePanel(OmniHub.Core.Hardware.HardwareContext ctx, FanService service)
    {
        _ctx = ctx;
        _service = service;
        Focusable = false;

        var mono = PanelChrome.Token<FontFamily>("MonoFont") ?? new FontFamily("Consolas");

        _foot.FontFamily = mono;
        _foot.FontSize = 9.5;
        _foot.Margin = new Thickness(0, 8, 0, 0);
        _foot.Foreground = PanelChrome.Brush("TextFaintBrush");
        _foot.TextWrapping = TextWrapping.Wrap;

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = "FAN CURVE",
            FontFamily = mono,
            FontSize = 10.5,
            FontWeight = FontWeights.Bold,
            Foreground = PanelChrome.Brush("TextFaintBrush"),
            Margin = new Thickness(0, 0, 0, 10),
        });
        stack.Children.Add(new ContentControl { Content = _chart, Height = 190, Focusable = false });
        stack.Children.Add(_foot);

        Content = PanelChrome.Card(stack);

        // Loaded/Unloaded, like every other panel: switching workspaces re-parents this control,
        // and a constructor-time subscription would leak a handler on each switch.
        Loaded += (_, _) =>
        {
            RefreshCurve();
            _ctx.OnReading += OnReading;
            _service.OnTick += OnTick;
        };

        Unloaded += (_, _) =>
        {
            _ctx.OnReading -= OnReading;
            _service.OnTick -= OnTick;
        };
    }

    /// <summary>
    /// Copies whatever the running service is steering by.
    ///
    /// Read from the service rather than from the settings file, because those two are not the
    /// same thing: the curve in force is the one for the rail the machine is on, and on battery
    /// with two curves configured the file holds both.
    /// </summary>
    private void RefreshCurve()
    {
        var curve = _service.Curve;

        _chart.Points = curve.Points;
        _chart.FloorTempC = curve.FloorTempC;
        _chart.FloorLevelPercent = curve.FloorLevelPercent;
        _chart.RefreshData();
    }

    private void OnReading(OmniHub.Core.Hardware.Reading r) => Dispatcher.BeginInvoke(() =>
    {
        // The curve can be swapped underneath this panel when the charger moves, so it is
        // re-read rather than captured once.
        RefreshCurve();

        _foot.Text = _service.IsRunning && _service.HasCommanded
            ? $"CURVE COMMANDING {_service.LastCommandedLevelPercent}%"
            : "THE CURVE IS NOT DRIVING THE FANS RIGHT NOW";
    });

    // The curve's own tick, which carries the temperature it actually steered on -- including the
    // predictive lead, which the raw reading does not show.
    private void OnTick(double tempC, byte levelPercent) =>
        Dispatcher.BeginInvoke(() => _chart.SetLive(tempC, levelPercent));
}
