// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (C) 2026 Vomitted

using System.Reflection;
using OmniHub.Core.Fan;
using OmniHub.Core.Hardware;
using OmniHub.Core.Vendors;
using Xunit;

namespace OmniHub.Tests;

/// <summary>
/// The rule that writing to hardware requires having verified that hardware.
///
/// It is the safety property the whole widening effort rests on, and this project has learned
/// twice that a rule it states without asserting is a rule it has already broken. So the check
/// here is not "the backends I remembered to think about refuse" -- it is every void method on
/// the interface, found by reflection, on every implementation, so that a backend written for a
/// Lenovo next month cannot quietly omit one.
/// </summary>
public class WriteGateTests
{
    /// <summary>
    /// Every implementation, built without touching hardware.
    ///
    /// HpFanBackend is handed a FanController over a null interop deliberately. With the gate
    /// shut, a refusal must arrive before anything reaches the hardware at all -- so if the gate
    /// were ever moved after the vendor call, this would surface as a NullReferenceException
    /// instead, and the test would say so rather than passing for the wrong reason.
    /// </summary>
    private static IEnumerable<IFanBackend> Shut() => new IFanBackend[]
    {
        new ScriptedFanBackend { Tier = VendorTier.Detected, Board = "TEST1" },
        new ScriptedFanBackend { Tier = VendorTier.Reading, Board = "TEST1" },
        new HpFanBackend(new FanController(null!), vendorInterfaceAvailable: false, board: "8C2F"),
    };

    /// <summary>The interface's write surface: everything that returns nothing.</summary>
    private static IEnumerable<MethodInfo> Writes() =>
        typeof(IFanBackend).GetMethods().Where(m => m.ReturnType == typeof(void));

    private static object?[] ArgsFor(MethodInfo method) =>
        method.GetParameters().Select(p => Activator.CreateInstance(p.ParameterType)).ToArray();

    [Fact]
    public void EveryWriteOnEveryBackendRefusesUntilItIsVerified()
    {
        var writes = Writes().ToList();
        Assert.NotEmpty(writes);   // a reflection test that found nothing passes for free

        foreach (var backend in Shut())
        {
            foreach (var method in writes)
            {
                var thrown = Record.Exception(() => Invoke(backend, method));

                Assert.True(thrown is HardwareWriteRefusedException,
                            $"{backend.GetType().Name}.{method.Name} at tier {backend.Tier} "
                            + $"threw {thrown?.GetType().Name ?? "nothing"} instead of refusing");
            }
        }
    }

    [Fact]
    public void AVerifiedBackendIsNotObstructed()
    {
        // The converse, and it has to be here: a gate that refuses everything would pass the test
        // above and make the application useless.
        var fan = new ScriptedFanBackend { Tier = VendorTier.Verified };

        foreach (var method in Writes())
            Assert.Null(Record.Exception(() => Invoke(fan, method)));

        Assert.Contains("control", fan.Calls);
    }

    [Fact]
    public void ARefusalSaysWhichMachineAndWhatWouldChangeIt()
    {
        // A refusal nobody can act on is only a slightly politer silent failure.
        var reading = Assert.Throws<HardwareWriteRefusedException>(
            () => new ScriptedFanBackend { Tier = VendorTier.Reading, Board = "8C2F" }.SetLevels(30, 30));

        Assert.Equal("8C2F", reading.Board);
        Assert.Equal(VendorTier.Reading, reading.Tier);
        Assert.Contains("8C2F", reading.Message);
        Assert.Contains("verified", reading.Message, StringComparison.OrdinalIgnoreCase);

        // Detected and Reading are different situations and should not read identically: one has
        // never been read from, the other reads fine and has not been written to.
        var detected = Assert.Throws<HardwareWriteRefusedException>(
            () => new ScriptedFanBackend { Tier = VendorTier.Detected, Board = "8C2F" }.SetLevels(30, 30));

        Assert.NotEqual(reading.Message, detected.Message);
    }

    [Fact]
    public void AskingWhetherAWriteIsAllowedAgreesWithTrying()
    {
        // The UI asks Allows to decide whether to offer a control at all; the backend asks
        // Require when the control is used. Two answers to one question is how a disabled button
        // ends up over a working write, or a working button over a refusal.
        foreach (VendorTier tier in Enum.GetValues<VendorTier>())
        {
            var fan = new ScriptedFanBackend { Tier = tier };
            bool threw = Record.Exception(() => fan.SetLevels(30, 30)) is not null;

            Assert.Equal(WriteGate.Allows(tier), !threw);
        }
    }

    [Fact]
    public async Task TheCoolingLoopSurvivesAGateItCannotOpen()
    {
        // An unverified machine must not produce a dead loop or a crash -- it produces a loop
        // that keeps trying, records why it is achieving nothing, and commands nothing.
        var fan = new ScriptedFanBackend { Tier = VendorTier.Reading, Board = "UNKNOWN" };
        using var service = new FanService(
            fan,
            () => new TemperatureReading(70, TemperatureSource.SmuDieTctl),
            FanCurve.CreateDefault(),
            TimeSpan.FromMilliseconds(5));

        service.Start();

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (service.LastError is null && DateTime.UtcNow < deadline) await Task.Delay(5);

        Assert.NotNull(service.LastError);
        Assert.True(service.IsRunning, "the loop died rather than reporting the refusal");
        Assert.Empty(fan.Levels);
        Assert.False(service.HasCommanded);

        service.Stop();
    }

    private static void Invoke(IFanBackend backend, MethodInfo method)
    {
        try { method.Invoke(backend, ArgsFor(method)); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            // Reflection wraps whatever the method threw; the wrapper is not the finding.
            throw ex.InnerException;
        }
    }
}
