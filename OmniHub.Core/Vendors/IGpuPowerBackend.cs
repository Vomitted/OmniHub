// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

namespace OmniHub.Core.Vendors;

/// <summary>How much power the discrete GPU is allowed, in the only three steps anything asks for.</summary>
public enum GpuPowerPreset
{
    /// <summary>As little as the firmware will settle for.</summary>
    Economy,

    /// <summary>The firmware's own default.</summary>
    Balanced,

    /// <summary>Everything the board will give.</summary>
    Maximum,
}

/// <summary>
/// The two things the vendor-neutral half of this application does to a discrete GPU.
///
/// Small because the need is small. Optimize/ holds the code that runs on any laptop -- power
/// plans, process priority, timer resolution, the return-to-stock sequence -- and it reached into
/// HP's GpuController for exactly one method call and one property. Those two references were
/// enough to make a vendor-neutral namespace name a vendor type.
///
/// Deliberately NOT a general GPU interface. HP's Custom TGP and Dynamic Boost flags are HP's,
/// the graphics-mode switch is HP's, and the GPU screen is the right home for all of it. What
/// crosses this boundary is only what code that does not care about the brand needs to say.
/// </summary>
public interface IGpuPowerBackend
{
    /// <summary>Asks for one of the three steps. What the firmware does with it is its own business.</summary>
    void SetPreset(GpuPowerPreset preset);

    /// <summary>
    /// While true, nothing may lower the GPU's power ceiling.
    ///
    /// A latch rather than a setting, and it exists because three separate paths write this
    /// ceiling and only one of them knows the user asked for maximum. Two of them build their
    /// request from a preset, where Balanced means less power -- so choosing a balanced profile
    /// silently undid an explicit unlock and put the card back under its stock cap.
    /// </summary>
    bool HoldAtMaximum { get; set; }
}
