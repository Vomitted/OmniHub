// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Hardware;

// Lifted out of RyzenSmu.cs, which cannot compile without a Windows kernel driver wrapper.
// This is a bag of watts, amps and degrees, plus the arithmetic for which of them is binding.
// Reading one needs the SMU; holding one does not. Metrics and LimitHistory both take it, so
// leaving it there made "what is limiting this machine" a Windows-only question.

/// <summary>
/// Live package power draw against the limits currently in force, in watts.
///
/// STAPM is the long-run sustained average, fast is the short burst ceiling, slow is the
/// medium window between them. The limits are what the SMU is actually enforcing right now,
/// which is not necessarily what was requested -- the platform clamps.
/// </summary>
public sealed record PowerSnapshot(
    double StapmLimitWatts, double StapmWatts,
    double FastLimitWatts, double FastWatts,
    double SlowLimitWatts, double SlowWatts,
    double ApuSlowLimitWatts, double ApuSlowWatts,
    double TdcVddLimitAmps, double TdcVddAmps,
    double TdcSocLimitAmps, double TdcSocAmps,
    double EdcVddLimitAmps, double EdcVddAmps,
    double EdcSocLimitAmps, double EdcSocAmps,
    double ThermalLimitC, double CoreTempC,
    double SocThermalLimitC, double SocTempC,
    double GfxThermalLimitC, double GfxTempC)
{
    /// <summary>
    /// The constraint the processor is actually up against right now, as a name and a
    /// percentage. This is the whole point of reading the table: knowing you are at 99% of the
    /// current limit and 60% of the power limit tells you which knob would matter, where a
    /// wattage on its own tells you nothing.
    /// </summary>
    public (string Name, double Percent) TightestLimit()
    {
        var tightest = Limits().MaxBy(c => c.Percent);
        return (tightest.Name, tightest.Percent);
    }

    /// <summary>
    /// Every constraint the processor is under, each as a percentage of its own limit, in a
    /// fixed order.
    ///
    /// The tightest one alone answers "what is holding this back". All five answer "and how much
    /// room is left in the others", which is the difference between knowing the machine is
    /// throttled and knowing which knob would change it: being at 99% of core current and 60% of
    /// the power limit says plainly that raising the power limit will do nothing, and that is
    /// exactly the conclusion nobody can reach from a wattage on its own.
    ///
    /// The order is stable, so a caller can build one row per entry once and afterwards only
    /// update the values rather than rebuilding its display on every refresh.
    /// </summary>
    public (string Name, double Percent)[] Limits() => new[]
    {
        ("Sustained power", Ratio(StapmWatts, StapmLimitWatts)),
        ("Boost power", Ratio(FastWatts, FastLimitWatts)),
        ("Core current (EDC)", Ratio(EdcVddAmps, EdcVddLimitAmps)),
        ("Core current (TDC)", Ratio(TdcVddAmps, TdcVddLimitAmps)),
        ("Temperature", Ratio(CoreTempC, ThermalLimitC)),
    };

    // A limit of zero means the table reported none, so there is no ratio to take. Zero is
    // returned rather than a division by zero, and it reads as "no constraint measured here".
    private static double Ratio(double value, double limit) => limit > 0 ? value / limit * 100.0 : 0;
}

