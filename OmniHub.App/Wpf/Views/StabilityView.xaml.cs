// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Windows;
using System.Windows.Controls;
using OmniHub.Core.Diagnostics;
using OmniHub.Core.Telemetry;
using UserControl = System.Windows.Controls.UserControl;

namespace OmniHub.App.Wpf.Views;

/// <summary>
/// Every time this machine stopped without shutting down, reconstructed.
///
/// Nine of those between 10 and 15 September, no established cause, and each round of
/// investigation was done by hand: a dozen commands, a spreadsheet of timestamps, and two wrong
/// conclusions along the way. The reasoning that eventually worked lives in StabilityCorrelator,
/// where it is pure and tested; this screen is the part that shows it.
///
/// It reports what happened and when. It does not say why. That restraint is the feature: this
/// application has twice been blamed for a fault it did not cause, and once had a diagnosis read
/// into a field that had been misunderstood, and on each occasion the confident sentence was the
/// expensive part.
/// </summary>
public partial class StabilityView : UserControl
{
    /// <summary>
    /// How far back to look.
    ///
    /// Matched to the thermal trace's own fourteen-day retention rather than chosen: beyond that
    /// there is no trace to recover a stop time from, so an older incident could only ever be
    /// reported as a bare recovery timestamp -- which is the shape of answer this screen exists
    /// to improve on.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private readonly TelemetryHistory _history = new();

    public StabilityView()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e) => Refresh();

    private async void Refresh()
    {
        RefreshBtn.IsEnabled = false;
        Summary.Text = "Reading the event log...";
        Incidents.Children.Clear();
        ReadError.Text = "";

        DateTime from = DateTime.UtcNow - Window;

        // First, because it is the fast half and it is about right now rather than about last
        // week. The event-log read below can take seconds on a large System log, and leaving the
        // live answer behind it would mean waiting on history to find out what is awake.
        await RefreshAwakeAsync().ConfigureAwait(true);

        try
        {
            // The event log read is synchronous and can take a moment on a machine with a large
            // System log, so it goes off the UI thread along with the two file reads.
            var read = await Task.Run(() =>
            {
                var list = StabilityHistory.Read(from, out string? error);
                return (List: list, Error: error);
            }).ConfigureAwait(true);

            var thermal = await _history.ReadThermalAsync(from, DateTime.UtcNow).ConfigureAwait(true);
            var power = await _history.ReadPowerEventsAsync(from, DateTime.UtcNow).ConfigureAwait(true);

            var incidents = StabilityCorrelator.Correlate(
                read.List,
                thermal.Select(t => t.AtUtc).ToList(),
                power.Select(p => (p.AtUtc, $"{p.Source} {p.Event} {p.Detail}".Trim())).ToList());

            Summary.Text = StabilityCorrelator.Summarise(incidents);

            if (read.Error is { Length: > 0 })
                ReadError.Text = $"The Windows event log could not be read fully: {read.Error}";

            if (incidents.Count == 0)
            {
                Incidents.Children.Add(Note("Nothing to show. That is the good outcome."));
            }
            else
            {
                // Newest first: the one somebody is here about is almost always the last one.
                foreach (var incident in incidents.OrderByDescending(i => i.RecoveredUtc))
                    Incidents.Children.Add(BuildCard(incident));
            }
        }
        catch (Exception ex)
        {
            Summary.Text = $"Could not build the timeline ({ex.Message}).";
        }
        finally
        {
            RefreshBtn.IsEnabled = true;
        }
    }

    /// <summary>
    /// What is holding a power request at this moment.
    ///
    /// It sits on this screen rather than on Diagnostics because it belongs to the same question
    /// as everything else here: the machine not going to sleep, and the machine not coming back.
    /// Answering it by hand took a dozen commands the last time it mattered, and the answer turned
    /// out to be a USB headset.
    /// </summary>
    private async Task RefreshAwakeAsync()
    {
        AwakeSummary.Text = "Asking Windows...";
        AwakeRequests.Children.Clear();

        // Off the UI thread: this starts a child process, and a wedged powercfg must not be able
        // to freeze the window of an application that is on this screen to investigate freezes.
        var read = await Task.Run(() =>
        {
            var list = PowerRequests.Read(out string? error);
            return (List: list, Error: error);
        }).ConfigureAwait(true);

        if (read.Error is { Length: > 0 })
        {
            AwakeSummary.Text = $"The power requests could not be read: {read.Error}";
            return;
        }

        AwakeSummary.Text = PowerRequests.Summarise(read.List);

        // System requests first -- they are the ones that stop the machine sleeping, which is the
        // question anybody opens this card with. OrderBy is stable, so the rest keep powercfg's
        // own order underneath.
        foreach (var request in read.List.OrderByDescending(r => r.Kind == PowerRequestKind.System))
            AwakeRequests.Children.Add(RequestRow(request));
    }

    private UIElement RequestRow(PowerRequest request)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };

        var line = new TextBlock
        {
            Text = $"{request.Category}   {request.Origin}   {request.FriendlyName}",
            Style = (Style)FindResource("BodyText"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 760,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
        };

        // The shortened name is what fits; the full one is what tells two copies of the same
        // executable apart, so it is a hover away rather than gone.
        if (request.FriendlyName != request.Name)
            line.ToolTip = request.Name;

        stack.Children.Add(line);

        if (request.Reason is { Length: > 0 })
            stack.Children.Add(Note("    " + request.Reason));

        return stack;
    }

    private UIElement BuildCard(StabilityIncident incident)
    {
        var card = new Border { Style = (Style)FindResource("CardBorderStyle"), Margin = new Thickness(0, 0, 0, 12) };
        var stack = new StackPanel();

        // Headline: when it stopped. That was the hardest thing to establish, and every other
        // question hangs off it.
        stack.Children.Add(Line(
            incident.StoppedUtc is { } stopped
                ? $"Stopped {stopped.ToLocalTime():ddd d MMM HH:mm:ss}"
                : "Stopped at an unknown time",
            "BigNumberText", 17));

        stack.Children.Add(Note(
            incident.StoppedUtc is null
                ? "Nothing was logging when it happened, so there is no evidence of the last moment it "
                  + "was executing. Reporting the recovery time as the failure would have said the "
                  + "outage lasted no time at all."
                : "Last row in the thermal trace before the machine came back. Recovered "
                  + $"{incident.RecoveredUtc.ToLocalTime():HH:mm:ss}"
                  + (incident.Silence is { } s ? $", down for {Humanise(s)}." : ".")));

        var facts = new List<string>
        {
            incident.BugcheckCode is > 0
                ? $"Bugcheck 0x{incident.BugcheckCode:X}"
                : "No bugcheck: Windows did not diagnose this one",

            incident.NearSleep
                ? $"A sleep transition happened within {StabilityCorrelator.SleepProximity.TotalMinutes:0} minutes"
                : $"No sleep transition within {StabilityCorrelator.SleepProximity.TotalMinutes:0} minutes",
        };

        if (incident.SleepInProgress is { } sip)
            facts.Add($"Kernel-Power 41 SleepInProgress = {sip}");

        stack.Children.Add(Note(string.Join("  |  ", facts)));

        if (incident.Context.Count > 0)
        {
            stack.Children.Add(Line("WHAT OMNIHUB RECORDED JUST BEFORE", "DataLabelText", 10, top: 12));

            foreach (string entry in incident.Context)
                stack.Children.Add(Note("  " + entry));
        }
        else
        {
            stack.Children.Add(Note(
                "OmniHub recorded nothing in the three minutes before it stopped.", top: 10));
        }

        var grid = new Grid();
        grid.Children.Add(new Border { Style = (Style)FindResource("CardSheenStyle") });
        grid.Children.Add(stack);
        card.Child = grid;
        return card;
    }

    private TextBlock Line(string text, string style, double size, double top = 0) => new()
    {
        Text = text,
        Style = (Style)FindResource(style),
        FontSize = size,
        Margin = new Thickness(0, top, 0, 4),
        TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock Note(string text, double top = 0) => new()
    {
        Text = text,
        Style = (Style)FindResource("MutedText"),
        FontSize = 11.5,
        Margin = new Thickness(0, top, 0, 2),
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 760,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
    };

    /// <summary>Durations a person reads, rather than 01:43:06.4213.</summary>
    private static string Humanise(TimeSpan span) =>
        span.TotalMinutes < 1 ? $"{span.TotalSeconds:0} seconds"
        : span.TotalHours < 1 ? $"{span.TotalMinutes:0} minutes"
        : span.TotalDays < 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
        : $"{(int)span.TotalDays} d {span.Hours} h";
}
