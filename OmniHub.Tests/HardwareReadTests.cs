// Explicit since this project enabled UseWPF: that changes the implicit-using set, and
// System.IO no longer arrives on its own.
using System.IO;
using OmniHub.Core.Optimize;

namespace OmniHub.Tests;

/// <summary>
/// Live reads against the real machine. These are integration tests, not unit tests: they
/// exercise the WMI and system-call paths the pure-logic suites deliberately avoid, which is
/// where a marshalling mistake or a wrong class name actually shows up.
///
/// Strictly read-only. Nothing here changes brightness, writes the registry or deletes a
/// file: a suite that alters the machine it runs on is a bad trade, and the write paths are
/// guarded by read-back checks in their own code instead.
///
/// What these CANNOT cover: the discharging branch of the battery reader. On AC the firmware
/// genuinely reports a discharge rate of zero, so the arithmetic that divides by it is
/// unreachable until the cable comes out. That is a hardware state, not a coverage gap -- the
/// arithmetic itself is covered in FanScaleTests against synthetic values.
/// </summary>
public class HardwareReadTests
{
    [Fact]
    public void BatteryDraw_ReadsAndIsSelfConsistent()
    {
        var draw = BatterySaver.ReadDraw();

        // A desktop or a VM has no battery; that is a legitimate outcome, not a failure.
        if (draw is null) return;

        Assert.True(draw.RemainingCapacityMWh > 0, "a present battery should report some capacity");
        Assert.True(draw.MilliVolts > 0, "a present battery should report a voltage");

        // The firmware cannot be discharging and on mains at once. This is the invariant the
        // UI's branching depends on.
        if (draw.OnAc) Assert.True(draw.DischargeMilliwatts == 0, "on AC the discharge rate should be zero");
        Assert.True(draw.DischargeMilliwatts >= 0, "discharge rate should never be negative");
        Assert.True(draw.ChargeMilliwatts >= 0, "charge rate should never be negative");
    }

    [Fact]
    public void BatteryDraw_DescribesItselfWithoutThrowing()
    {
        // Guards the formatting path against a null or zero-valued reading, which is what it
        // will meet most of the time on a machine that stays plugged in.
        var text = BatterySaver.DescribeDraw(BatterySaver.ReadDraw());
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void Brightness_ReadsAPlausiblePercentage()
    {
        var brightness = BatterySaver.GetBrightness();

        // External monitors do not expose the WMI brightness class; null is expected there.
        if (brightness is null) return;
        Assert.InRange(brightness.Value, 0, 100);
    }

    [Fact]
    public void TimerResolution_ReportsRealValues()
    {
        double current = SystemTuning.CurrentTimerResolutionMs();
        double best = SystemTuning.BestTimerResolutionMs();

        Assert.False(double.IsNaN(current), "the current timer resolution should be readable");
        Assert.False(double.IsNaN(best), "the finest timer resolution should be readable");

        // "best" means finest, i.e. the smallest interval. The native API's naming reads the
        // other way round, and this assertion pins which one is meant.
        Assert.True(best <= current + 0.0001, "the finest resolution cannot be coarser than the current one");
        Assert.InRange(current, 0.01, 200.0);
    }

    [Fact]
    public void MemoryStatus_ReadsConsistentTotals()
    {
        ulong available = MemoryTools.AvailablePhysicalBytes();
        ulong total = MemoryTools.TotalPhysicalBytes();

        Assert.True(total > 0, "total physical memory should be readable");
        Assert.True(available > 0, "available physical memory should be readable");
        Assert.True(available <= total, "available memory cannot exceed the total");
    }

    [Fact]
    public void ShaderCacheScan_IsReadOnlyAndInternallyConsistent()
    {
        // Scan must never throw on a machine missing a given vendor's directory, and every
        // entry it reports must be non-empty -- an entry with no files would make the UI
        // offer to clear something that is not there.
        foreach (var location in ShaderCache.Scan())
        {
            Assert.True(location.Files > 0, $"{location.Label} was reported with no files");
            Assert.True(location.Bytes >= 0);
            Assert.False(string.IsNullOrWhiteSpace(location.Path));
        }
    }

    [Fact]
    public void DiskCleanupScan_OnlyReportsAllowlistedLocations()
    {
        // Every reported path must sit under a directory this tool is allowed to touch. This
        // is the guard against a future edit widening the allowlist by accident.
        string temp = Path.GetFullPath(Path.GetTempPath());
        string windows = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string local = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

        foreach (var target in DiskCleanup.Scan())
        {
            string full = Path.GetFullPath(target.Path);
            bool allowed =
                full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(local, StringComparison.OrdinalIgnoreCase);

            Assert.True(allowed, $"cleanup target outside the allowlist: {full}");
        }
    }

    [Fact]
    public void PowerPlans_EnumerateAndOneIsActive()
    {
        var schemes = PowerPlan.List();
        if (schemes.Count == 0) return; // a policy-locked machine can hide them entirely

        Assert.All(schemes, s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));

        var active = PowerPlan.GetActiveSchemeId();
        Assert.NotNull(active);
        Assert.Contains(schemes, s => s.Id == active!.Value);
    }

    [Fact]
    public void WindowsGamingToggles_ReadWithoutClaimingUnknownState()
    {
        foreach (var toggle in WindowsGaming.ReadAll())
        {
            Assert.False(string.IsNullOrWhiteSpace(toggle.Name));
            Assert.False(string.IsNullOrWhiteSpace(toggle.Description));
            // Enabled is deliberately nullable: absent means "Windows default", and reporting
            // that as false would be the app asserting something it does not know.
        }
    }
}
