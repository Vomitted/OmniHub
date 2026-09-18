// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using OmniHub.Core.Hardware;

namespace OmniHub.Core.Vendors;

/// <summary>
/// HP's GPU power control behind the two-member interface the vendor-neutral code uses.
///
/// A wrapper with nothing of its own, for the same reason HpFanBackend is one: the commit that
/// introduces a seam should be unable to change what the hardware is told. The mapping below is
/// the only content, and it is a rename -- HP's Eco, Balanced and Performance bytes are already
/// the three steps the interface names.
/// </summary>
public sealed class HpGpuBackend : IGpuPowerBackend
{
    private readonly GpuController _gpu;

    public HpGpuBackend(GpuController gpu) => _gpu = gpu;

    public void SetPreset(GpuPowerPreset preset) => _gpu.SetPowerPreset(preset switch
    {
        GpuPowerPreset.Economy => GpuPowerLevel.Eco,
        GpuPowerPreset.Balanced => GpuPowerLevel.Balanced,
        _ => GpuPowerLevel.Performance,
    });

    public bool HoldAtMaximum
    {
        get => _gpu.ForceMaxPower;
        set => _gpu.ForceMaxPower = value;
    }
}
