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
            return RunSchTasks("/Create", "/TN", TaskName, "/XML", path, "/F");
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
              <Settings>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>false</AllowHardTerminate>
                <StartWhenAvailable>true</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <DisallowStartOnRemoteAppSession>false</DisallowStartOnRemoteAppSession>
                <UseUnifiedSchedulingEngine>true</UseUnifiedSchedulingEngine>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>7</Priority>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
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
            var psi = new ProcessStartInfo("schtasks.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            // Both pipes are redirected and neither was read, with an unbounded WaitForExit
            // after it. Enough output from schtasks to fill a pipe buffer would block the
            // child on that write and this call forever, with no timeout to escape by.
            // Draining both concurrently and bounding the wait removes both halves.
            _ = proc.StandardOutput.ReadToEndAsync();
            _ = proc.StandardError.ReadToEndAsync();

            if (!proc.WaitForExit(30_000)) { try { proc.Kill(true); } catch { } return false; }
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
