using OmniHub.Core.Hardware;

namespace OmniHub.Tests;

/// <summary>
/// Adapter selection for the vendor-neutral GPU path.
///
/// This runs on machines with no NVIDIA driver, where it is the only thing naming the GPU. The
/// case that matters is Microsoft's fallback display driver: it appears in
/// Win32_VideoController alongside the real adapter when a vendor driver has failed to start,
/// and enumeration order does not reliably put the real one first. Picking it would report
/// "Microsoft Basic Display Adapter" as the machine's GPU, which is a true statement about the
/// driver stack and a useless one about the hardware.
/// </summary>
public class GpuAdapterTests
{
    private static (string, string) Pick(params (string Name, string Vendor)[] adapters) =>
        GpuTelemetry.PickAdapter(adapters);

    [Fact]
    public void SingleAdapter_IsChosen()
    {
        Assert.Equal(
            ("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."),
            Pick(("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc.")));
    }

    [Fact]
    public void FallbackDriver_IsSkippedEvenWhenEnumeratedFirst()
    {
        var (name, _) = Pick(
            ("Microsoft Basic Display Adapter", "(Standard display types)"),
            ("Intel(R) Iris(R) Xe Graphics", "Intel Corporation"));

        Assert.Equal("Intel(R) Iris(R) Xe Graphics", name);
    }

    [Fact]
    public void BasicRenderDriver_IsAlsoSkipped()
    {
        var (name, _) = Pick(
            ("Microsoft Basic Render Driver", "Microsoft"),
            ("AMD Radeon RX 6600M", "Advanced Micro Devices, Inc."));

        Assert.Equal("AMD Radeon RX 6600M", name);
    }

    /// <summary>
    /// When the fallback driver is all there is, report it rather than nothing. A name that
    /// explains why the panel looks empty beats an empty panel.
    /// </summary>
    [Fact]
    public void FallbackDriverAlone_IsStillReported()
    {
        var (name, _) = Pick(("Microsoft Basic Display Adapter", "(Standard display types)"));
        Assert.Equal("Microsoft Basic Display Adapter", name);
    }

    /// <summary>
    /// Hybrid laptops list both adapters and neither is a fallback. The first is taken -- the
    /// point of the assertion is that selection is deterministic, not that it divines which
    /// GPU is "the" one, which from this data it cannot.
    /// </summary>
    [Fact]
    public void TwoRealAdapters_TakesTheFirstDeterministically()
    {
        var (name, vendor) = Pick(
            ("NVIDIA GeForce RTX 4060 Laptop GPU", "NVIDIA"),
            ("AMD Radeon(TM) Graphics", "Advanced Micro Devices, Inc."));

        Assert.Equal("NVIDIA GeForce RTX 4060 Laptop GPU", name);
        Assert.Equal("NVIDIA", vendor);
    }
}
