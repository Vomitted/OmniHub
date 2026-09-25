# Renders OmniHub's windows to PNG from a sandbox. See README.md.
#
#   .\tools\snapshot\run.ps1                          every workspace, in the saved interface and theme
#   .\tools\snapshot\run.ps1 -Plan "iface=Cockpit,0"  the first workspace in Cockpit
#   .\tools\snapshot\run.ps1 -Plan "theme=Midnight,ws" -Replay
#
# -Replay feeds the last rows of today's thermal log into the live figures, for judging a layout
# with real-shaped numbers. Such an image shows how the screen lays out, never what was measured.
param(
    [string]$Plan = "ws",
    [string]$Out = (Join-Path $env:TEMP "omnihub-snapshot"),
    [switch]$Replay,
    [int]$TimeoutSec = 180
)
$ErrorActionPreference = "Stop"
$tool = $PSScriptRoot
$repo = Resolve-Path (Join-Path $tool "..\..")
$app = Join-Path $tool "bin\app"
$exeDir = Join-Path $tool "bin\tool"

# A private build of the App. Never the installed one: the running application holds it locked,
# and this must not depend on anyone closing their fan control first.
dotnet build (Join-Path $repo "OmniHub.App\OmniHub.App.csproj") -c Release -o $app -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "the App did not build" }
dotnet build (Join-Path $tool "Snapshot.csproj") -c Release -o $exeDir -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "the snapshot tool did not build" }

# Low integrity on the executable makes the process Low; Low on the output folder is the only place
# it may write. Everything else -- settings, the registry, WMI methods, the PawnIO device -- is denied.
$exe = Join-Path $exeDir "Snapshot.exe"
icacls $exe /setintegritylevel low | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
icacls $Out /setintegritylevel "(OI)(CI)low" | Out-Null
Remove-Item (Join-Path $Out "*.png") -ErrorAction SilentlyContinue

if ($Replay) {
    $env:SNAPSHOT_REPLAY = Join-Path $env:APPDATA ("OmniHub\logs\thermal-{0:yyyy-MM-dd}.csv" -f (Get-Date))
}

# A separate desktop that is never shown, so nothing the window opens -- a dialog, a balloon -- can
# appear in front of the person using the machine.
if (-not ("OmniHubSnapshotDesktop" -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class OmniHubSnapshotDesktop {
  [DllImport("advapi32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, int rev, out IntPtr sd, out int len);
  [StructLayout(LayoutKind.Sequential)] struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
  [DllImport("user32.dll", SetLastError=true, CharSet=CharSet.Unicode, EntryPoint="CreateDesktopW")]
  static extern IntPtr CreateDesktop(string name, IntPtr dev, IntPtr devmode, int flags, uint access, ref SECURITY_ATTRIBUTES sa);
  [DllImport("user32.dll")] static extern bool CloseDesktop(IntPtr h);
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
  struct STARTUPINFO { public int cb; public string lpReserved; public string lpDesktop; public string lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError; }
  [StructLayout(LayoutKind.Sequential)] struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)]
  static extern bool CreateProcess(string app, string cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string dir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
  [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr h, uint code);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  public static string Run(string exe, string cmdline, uint timeoutMs) {
    IntPtr sd; int sdLen;
    // Low mandatory label on the desktop, or a Low-integrity process cannot create windows on it.
    if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:(A;;GA;;;WD)S:(ML;;NW;;;LW)", 1, out sd, out sdLen))
      return "security descriptor failed: " + Marshal.GetLastWin32Error();
    var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf(typeof(SECURITY_ATTRIBUTES)), lpSecurityDescriptor = sd };
    IntPtr desk = CreateDesktop("OmniHubSnapshot", IntPtr.Zero, IntPtr.Zero, 0, 0x10000000, ref sa);
    if (desk == IntPtr.Zero) return "CreateDesktop failed: " + Marshal.GetLastWin32Error();
    var si = new STARTUPINFO(); si.cb = Marshal.SizeOf(si); si.lpDesktop = "OmniHubSnapshot";
    PROCESS_INFORMATION pi;
    if (!CreateProcess(exe, cmdline, IntPtr.Zero, IntPtr.Zero, false, 0x08000000, IntPtr.Zero, null, ref si, out pi))
    { int e = Marshal.GetLastWin32Error(); CloseDesktop(desk); return "CreateProcess failed: " + e; }
    uint w = WaitForSingleObject(pi.hProcess, timeoutMs);
    string result = w == 0 ? "finished" : "timed out and was terminated";
    if (w != 0) TerminateProcess(pi.hProcess, 99);
    uint code; GetExitCodeProcess(pi.hProcess, out code);
    CloseHandle(pi.hThread); CloseHandle(pi.hProcess); CloseDesktop(desk);
    return result + ", exit code " + code;
  }
}
"@
}

try {
    [OmniHubSnapshotDesktop]::Run($exe, "`"$exe`" `"$app`" `"$Out`" $Plan", [uint32]($TimeoutSec * 1000))
}
finally {
    Remove-Item Env:\SNAPSHOT_REPLAY -ErrorAction SilentlyContinue
}
Get-Content (Join-Path $Out "snapshot.log") -ErrorAction SilentlyContinue
"images in $Out"
