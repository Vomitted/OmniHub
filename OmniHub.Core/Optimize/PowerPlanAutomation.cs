namespace OmniHub.Core.Optimize;

/// <summary>
/// Switches the Windows power scheme when the charger goes in or out.
///
/// Deliberately separate from the tuning-profile switching that already rides on
/// <see cref="PowerSourceWatcher"/>. Those are firmware wattage caps; this is the OS policy
/// that governs processor states and idle behaviour. They are independent decisions and a user
/// may reasonably want one automated and not the other, so they get their own toggle rather
/// than being bundled because they happen to share a trigger.
///
/// Nothing here edits a power scheme. It only activates one of the two schemes
/// <see cref="PowerPlanSetup"/> created, which is what keeps the machine's own Balanced plan
/// exactly as its owner configured it.
/// </summary>
public sealed class PowerPlanAutomation : IDisposable
{
    private readonly PowerSourceWatcher _watcher;
    private readonly bool _ownsWatcher;
    private Guid? _acPlan;
    private Guid? _dcPlan;

    /// <summary>
    /// Uses a shared watcher when given one, and its own otherwise.
    ///
    /// Two of these were polling GetSystemPowerStatus independently, five seconds apart, for the
    /// same answer. Sharing removes something subtler than the duplicate call, too: with separate
    /// watchers the consumers observe a charger change at slightly different moments, so the
    /// tuning profile and the power plan could briefly disagree about which rail the machine is
    /// on.
    ///
    /// A borrowed watcher is not stopped by Stop and not disposed here. It belongs to whoever
    /// created it, and stopping it would silently take the other consumer's notifications away.
    /// </summary>
    public PowerPlanAutomation(PowerSourceWatcher? shared = null)
    {
        _watcher = shared ?? new PowerSourceWatcher();
        _ownsWatcher = shared is null;
        _watcher.OnChanged += Apply;
    }

    /// <summary>The last thing that happened, for the UI to show. Never thrown as an error.</summary>
    public string LastResult { get; private set; } = "Not started.";

    /// <summary>True while the watcher is polling.</summary>
    public bool IsRunning { get; private set; }

    /// <summary>Raised after a switch attempt so the UI can refresh without polling.</summary>
    public event Action<string>? OnApplied;


    /// <summary>
    /// Begins watching. Both plan ids must be known; with either missing this does nothing
    /// rather than half-automating, because a rule that fires on one rail and not the other is
    /// harder to reason about than no rule.
    /// </summary>
    public void Start(Guid acPlan, Guid dcPlan)
    {
        _acPlan = acPlan;
        _dcPlan = dcPlan;
        if (IsRunning) return;

        _watcher.Start();
        IsRunning = true;
        LastResult = "Watching for power source changes.";
    }

    /// <summary>
    /// Applies the plan for the source the machine is on right now.
    ///
    /// Start only re-points the targets; the watcher raises its event on a change, and on its
    /// first observation. Re-pointing an already-running automation would otherwise sit inert
    /// until a cable moved, which makes picking a plan look like it did nothing.
    /// </summary>
    public void ApplyNow() => Apply(_watcher.Current);

    public void Stop()
    {
        if (!IsRunning) return;

        // Only a watcher this owns may be stopped. A shared one keeps running for whoever else
        // is listening; IsRunning below is what stops this from acting on it.
        if (_ownsWatcher) _watcher.Stop();

        IsRunning = false;
        LastResult = "Automation off. The active power plan was left as it is.";
    }

    /// <summary>
    /// Applies the plan matching the current source.
    ///
    /// Unknown does nothing at all. Windows reports an unknown line status in real situations --
    /// briefly during resume, and on some docks -- and guessing "battery" there would drop a
    /// plugged-in machine onto the saver plan for no reason.
    /// </summary>
    private void Apply(PowerSource source)
    {
        // A shared watcher keeps reporting after Stop, because other consumers still want it.
        // This is what makes the switch actually off rather than merely quiet.
        if (!IsRunning) return;

        try
        {
            Guid? want = source switch
            {
                PowerSource.Mains => _acPlan,
                PowerSource.Battery => _dcPlan,
                _ => null,
            };
            if (want is not { } plan) return;

            // Skip the call when it is already active: PowerSetActiveScheme on the current
            // scheme is harmless but it re-broadcasts a power-setting change to every window on
            // the desktop, and the watcher re-reports the source on its first observation.
            if (PowerPlan.GetActiveSchemeId() == plan)
            {
                LastResult = $"Already on the {(source == PowerSource.Mains ? "mains" : "battery")} plan.";
                OnApplied?.Invoke(LastResult);
                return;
            }

            var result = PowerPlan.Activate(plan);
            LastResult = result.Applied
                ? $"{(source == PowerSource.Mains ? "Plugged in" : "On battery")} -- {result.Detail}"
                : $"Could not switch plan: {result.Detail}";
        }
        catch (Exception ex)
        {
            // A failed switch must not take down the poll loop; the next transition retries.
            LastResult = $"Power plan switch failed: {ex.Message}";
        }

        OnApplied?.Invoke(LastResult);
    }

    public void Dispose()
    {
        _watcher.OnChanged -= Apply;

        // Only dispose a watcher this created. A shared one outlives this object.
        if (_ownsWatcher) _watcher.Dispose();
    }
}
