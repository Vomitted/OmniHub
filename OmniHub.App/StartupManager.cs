using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text;

namespace OmniHub.App;

/// <summary>Manages an auto-start-at-logon Scheduled Task for OmniHub. A plain
/// HKCU...\Run registry entry doesn't reliably auto-elevate -- OmniHub needs
/// admin for BIOS access, so a Run-key launch either shows a UAC prompt every
/// login or silently fails to start, depending on the user's UAC settings.
/// Task Scheduler's "Run with highest privileges" flag is the standard,
/// documented way to auto-start an elevated app at logon without a prompt,
/// for a user account that is itself an administrator.</summary>
public static class StartupManager
{
    private const string TaskName = "OmniHub_AutoStart";

    /// <summary>What schtasks said when the last call failed, or null when it succeeded.</summary>
    public static string? LastError { get; private set; }

    public static bool IsEnabled() => RunSchTasks("/Query", "/TN", TaskName);

    public static bool SetEnabled(bool enabled)
    {
        if (!enabled) return RunSchTasks("/Delete", "/TN", TaskName, "/F");

        string exePath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Could not determine the running executable's path.");

        // Registered from XML rather than with /TR and /SC.
        //
        // schtasks' command-line form cannot express the two settings that matter most here, and
        // Task Scheduler defaults BOTH of them to true on a battery-powered machine:
        //
        //   DisallowStartIfOnBatteries -- OmniHub does not start at sign-in while unplugged,
        //                                 which is one of the two times the fan curve matters.
        //   StopIfGoingOnBatteries     -- Task Scheduler TERMINATES OmniHub the moment the
        //                                 charger comes out.
        //
        // The second is the serious one, and it does not present as a settings problem: it looks
        // exactly like the application crashing on unplug, which is how it was reported. Worse,
        // it is a hard kill, so the fan controller never reaches its shutdown path and never
        // hands control back to the BIOS. The laptop can be left with its fans pinned at whatever
        // level was last commanded, by a power event, on the rail where a stuck fan is also
        // draining the battery.
        //
        // ExecutionTimeLimit is PT0S, meaning none. The default of three days would otherwise
        // stop a machine that simply stays awake.
        string xml = TaskXml(exePath);
        string path = Path.Combine(Path.GetTempPath(), $"omnihub-task-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16: schtasks /XML rejects a plain UTF-8 file with a parse error that names no
            // encoding, which is a genuinely confusing way to fail.
            File.WriteAllText(path, xml, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
            if (RunSchTasks("/Create", "/TN", TaskName, "/XML", path, "/F")) return true;

            // Fall back to the plain form rather than leave the machine with nothing.
            //
            // /Create deletes any existing task before it writes the new one, so a failure here
            // does not leave the previous entry standing -- it leaves none at all, and OmniHub
            // stops coming back after a reboot. That is a worse outcome than a task whose battery
            // settings Windows' command-line form cannot express, which is merely the behaviour
            // every previous version shipped with.
            //
            // The XML error is kept and handed on, because a working-but-degraded task that says
            // nothing is how this failure stayed invisible in the first place.
            string xmlError = LastError ?? "the XML form was rejected";

            if (RunSchTasks("/Create", "/TN", TaskName, "/TR", exePath, "/SC", "ONLOGON", "/RL", "HIGHEST", "/F"))
            {
                LastError =
                    "Startup is enabled, but with Windows' own battery defaults: OmniHub will not "
                    + "start at sign-in while unplugged, and Task Scheduler will stop it when the "
                    + "charger comes out.\n\nThe settings that prevent this could not be applied. "
                    + $"Windows said:\n{xmlError}";
                return true;
            }

            return false;
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    private static string TaskXml(string exePath)
    {
        string user = SecurityElement.Escape($@"{Environment.UserDomainName}\{Environment.UserName}") ?? "";
        string command = SecurityElement.Escape(exePath) ?? "";

        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>Starts OmniHub at sign-in with the privileges its BIOS access needs.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <!-- Element ORDER matters and the schema version matters.
                   TaskSettingsType is an xsd:sequence, so these are not interchangeable, and
                   DisallowStartOnRemoteAppSession and UseUnifiedSchedulingEngine belong to
                   schema 1.3 -- declaring 1.2 and including them is rejected with "the task XML
                   contains an unexpected node", which is exactly how this failed the first time.
                   They are dropped rather than the version raised, because neither is wanted. -->
              <Settings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <WakeToRun>false</WakeToRun>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{command}</Command>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    private static bool RunSchTasks(params string[] args)
    {
        try
        {
            // Absolute path, not the bare name.
            //
            // UseShellExecute = false resolves a bare name off PATH, and a failed CreateProcess
            // reports "The system cannot find the file specified." -- which is precisely the
            // message this returned, for both the create and the query, which is what made it
            // look like a problem with the task XML rather than with launching schtasks at all.
            // A system binary should never be reached through an inherited PATH anyway.
            string schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");

            var psi = new ProcessStartInfo(schtasks)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null)
            {
                LastError = $"{schtasks} could not be started.";
                return false;
            }

            // Both pipes are redirected and neither was read, with an unbounded WaitForExit
            // after it. Enough output from schtasks to fill a pipe buffer would block the
            // child on that write and this call forever, with no timeout to escape by.
            // Draining both concurrently and bounding the wait removes both halves.
            var stdout = proc.StandardOutput.ReadToEndAsync();
            var stderr = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(30_000))
            {
                try { proc.Kill(true); } catch { }
                LastError = $"{schtasks} did not finish within 30 seconds.";
                return false;
            }

            // Kept, so a failure can say what schtasks said.
            //
            // It reported "ERROR: The task XML contains an unexpected node." and named the
            // element and line, and every word of that was discarded -- the user saw "Could not
            // update the startup task" and nothing else, for a one-line schema mistake. The
            // output was already being drained to stop the pipe filling; keeping it costs
            // nothing and is the difference between a diagnosis and a guess.
            LastError = proc.ExitCode == 0
                ? null
                : string.Concat(stderr.Result, stdout.Result).Trim() is { Length: > 0 } detail
                    ? detail
                    : $"schtasks exited with code {proc.ExitCode}.";

            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            // Named rather than swallowed. Every silent "return false" here reaches the user as
            // an unexplained refusal, and this class has already cost one round of guessing that
            // way.
            // The path is named: every previous round of this ended with a bare sentence that
            // could have come from three different failures.
            LastError = $"{ex.Message} (running {Path.Combine(Environment.SystemDirectory, "schtasks.exe")})";
            return false;
        }
    }
}
