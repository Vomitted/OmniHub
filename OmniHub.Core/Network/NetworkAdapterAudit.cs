using System.Management;

namespace OmniHub.Core.Network;

/// <summary>How much a finding is actually worth acting on.</summary>
public enum FindingWeight
{
    /// <summary>Measurable, and capable of causing real stalls or dropped links.</summary>
    Worthwhile,

    /// <summary>Real but small. Included so the list is complete, labelled so it is not oversold.</summary>
    Marginal,
}

/// <summary>One adapter setting worth mentioning, and what to do about it.</summary>
public sealed record AdapterFinding(
    string Adapter,
    string Setting,
    string CurrentValue,
    FindingWeight Weight,
    string Why,
    string FixCommand);

/// <summary>
/// Reports network adapter settings that cost latency, and never changes them.
///
/// Read-only is a deliberate choice, not an unfinished one. Every one of these settings is applied
/// by resetting the adapter, which drops the link for a second or two; a button doing that
/// silently could disconnect the user mid-match, which is a worse outcome than the setting it was
/// fixing. This project also has a standing rule against quietly rewriting system configuration
/// and reporting it afterwards. So each finding carries the exact command that applies it, and
/// the decision stays with the person whose machine it is.
///
/// The weights matter as much as the findings. Most published lists of "gaming network tweaks"
/// are folklore delivered with total confidence -- disabling Nagle for games that use UDP, where
/// it does nothing whatsoever, or NetworkThrottlingIndex for traffic nowhere near its ceiling.
/// What survives once that is stripped out is short, and it is mostly about the adapter being
/// allowed to power parts of itself down between packets. That is a real effect with a real
/// mechanism, and it is still worth only a few milliseconds, so it is labelled accordingly rather
/// than sold as a cure for lag.
/// </summary>
public static class NetworkAdapterAudit
{
    /// <summary>
    /// Settings worth reporting, keyed by the driver's own registry keyword.
    ///
    /// Keyed on keyword rather than display name because the display name is localised and varies
    /// between vendors, while the keyword is what the driver and PowerShell both use. The leading
    /// asterisk on some marks a standardised NDIS keyword; vendor-specific ones have no prefix,
    /// which is why Realtek's Green Ethernet sits beside the standard EEE entry doing a closely
    /// related job.
    /// </summary>
    private static readonly (string Keyword, string TriggerValue, FindingWeight Weight, string Why)[] Watched =
    {
        ("EnableGreenEthernet", "Enabled", FindingWeight.Worthwhile,
            "Reduces transmit power based on measured cable length. When it misjudges, the link "
            + "renegotiates -- and a renegotiation is a complete interruption, not merely a slow "
            + "packet."),

        ("PowerSavingMode", "Enabled", FindingWeight.Worthwhile,
            "Lets the adapter idle parts of itself between packets. Coming back out of that costs "
            + "time on the first packet after a quiet moment, which is the one carrying your input "
            + "just after you stopped moving."),

        ("*EEE", "Enabled", FindingWeight.Worthwhile,
            "Energy Efficient Ethernet (802.3az) parks the link between frames and wakes it on "
            + "demand. The wake costs microseconds per frame, but the transitions are a known "
            + "source of link instability on cheaper switches."),

        ("*FlowControl", "Rx & Tx Enabled", FindingWeight.Worthwhile,
            "802.3x pause frames let the switch stop ALL of your traffic to relieve congestion "
            + "rather than dropping the one flow causing it, so game packets are paused along with "
            + "everything else. Modern queueing handles this better without it."),

        ("*InterruptModeration", "Enabled", FindingWeight.Marginal,
            "Batches interrupts to save CPU, adding up to roughly 100 microseconds before packets "
            + "reach Windows. Genuinely real, genuinely tiny, and switching it off raises CPU use "
            + "under load -- which on a thermally limited laptop is a cost of its own."),
    };

    /// <summary>
    /// Reads current settings for every adapter that exposes them.
    ///
    /// Returns an empty list rather than throwing when WMI is unavailable. This is advisory
    /// information sitting beside real measurements; it is not worth taking a screen down over,
    /// and an empty section reads correctly as "nothing to report".
    /// </summary>
    public static List<AdapterFinding> Run()
    {
        var findings = new List<AdapterFinding>();

        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"root\StandardCimv2",
                "SELECT InterfaceDescription, RegistryKeyword, DisplayName, DisplayValue "
                + "FROM MSFT_NetAdapterAdvancedPropertySettingData");

            foreach (var mo in searcher.Get())
            {
                using (mo)
                {
                    string keyword = mo["RegistryKeyword"]?.ToString() ?? "";
                    string value = mo["DisplayValue"]?.ToString() ?? "";
                    string adapter = mo["InterfaceDescription"]?.ToString() ?? "";
                    string display = mo["DisplayName"]?.ToString() ?? keyword;

                    foreach (var w in Watched)
                    {
                        if (!string.Equals(keyword, w.Keyword, StringComparison.OrdinalIgnoreCase)) continue;

                        // StartsWith rather than equality: Flow Control reports "Rx & Tx Enabled",
                        // and the other variants ("Tx Enabled", "Rx Enabled") are the same setting
                        // half on. Matching the prefix catches them without a second table.
                        if (!value.StartsWith(w.TriggerValue, StringComparison.OrdinalIgnoreCase)) continue;

                        findings.Add(new AdapterFinding(
                            Adapter: adapter,
                            Setting: display,
                            CurrentValue: value,
                            Weight: w.Weight,
                            Why: w.Why,
                            FixCommand: FixFor(adapter, w.Keyword)));
                    }
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (PlatformNotSupportedException) { }

        // Worthwhile first, so a list read from the top is read in the order worth acting on.
        return findings
            .OrderBy(f => f.Weight)
            .ThenBy(f => f.Setting, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The command that turns one setting off.
    ///
    /// Written against the interface DESCRIPTION rather than its name, because the description
    /// identifies the hardware while the name is whatever the user renamed the connection to.
    /// Quoted for the same reason: these descriptions always contain spaces.
    /// </summary>
    private static string FixFor(string adapter, string keyword) =>
        $"Set-NetAdapterAdvancedProperty -InterfaceDescription \"{adapter}\" "
        + $"-RegistryKeyword \"{keyword}\" -DisplayValue \"Disabled\"";
}
