; OmniHub installer (Inno Setup 6).
;
; Build it with installer\build.ps1, which publishes the self-contained application first and
; then passes the publish directory in as SourceDir. Compiling this file on its own will fail
; with "no files found", and that is deliberate: an installer built from a stale or
; framework-dependent output would install an application that cannot start on a machine with
; no .NET runtime, and would do it silently.

#define AppName        "OmniHub"
#define AppPublisher   "Vomitted"
#define AppUrl         "https://github.com/Vomitted/OmniHub"
#define AppExe         "OmniHub.exe"

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir is not defined. Build through installer\build.ps1 rather than compiling this file directly.
#endif

[Setup]
; A fixed AppId is what makes an upgrade an upgrade rather than a second copy in Add/Remove
; Programs. It must never change once a build carrying it has been published.
AppId={{7F3C9A21-5E48-4C6B-9B2D-1A64E0C7D835}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}

DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
AllowNoIcons=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename={#AppName}-{#AppVersion}-setup
SetupIconFile=..\OmniHub.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes

; The application drives the HP BIOS interface over WMI and loads a ring-0 driver, neither of
; which works unelevated, and Program Files is not writable by a standard user in any case.
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Setup ASKS the user to close a running OmniHub rather than closing it itself.
;
; This is not politeness. Exiting through the tray hands fan control back to the BIOS first; a
; process killed outright never reaches that path and can leave the fans pinned at whatever
; level was last commanded. An installer that terminates the app to free a file lock would do
; exactly the thing the application exists to prevent.
AppMutex=Local\OmniHub_SingleInstance_Mutex
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start {#AppName} when I sign in"; GroupDescription: "Startup:"
Name: "pawnio"; Description: "Install the PawnIO driver (needed for CPU tuning on AMD Ryzen)"; GroupDescription: "Additional components:"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; The sign-in task points at a path that is about to stop existing. Left behind, it becomes a
; scheduled task that fails at every sign-in forever.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN OmniHub_AutoStart /F"; Flags: runhidden; RunOnceId: "RemoveStartupTask"

[Code]

{ PawnIO is namazso's ring-0 driver runtime, from pawnio.eu, and it is not redistributed here.
  It is fetched through winget so the user gets it from its own publisher, signed, rather than
  from a copy inside this installer that nobody would think to re-verify.

  Failure is reported and offered a manual route rather than swallowed: CPU tuning silently
  doing nothing, with no explanation, is the outcome worth avoiding. }
procedure InstallPawnIo;
var
  ResultCode: Integer;
  Ok: Boolean;
begin
  WizardForm.StatusLabel.Caption := 'Installing the PawnIO driver...';
  Ok := Exec(ExpandConstant('{cmd}'),
             '/c winget install --id namazso.PawnIO --silent --accept-package-agreements --accept-source-agreements',
             '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);

  if not Ok then
  begin
    { Pascal does not join adjacent string literals the way C does; every break needs a
      real '+'. }
    if MsgBox('PawnIO could not be installed automatically.' + #13#10#13#10 +
              'It is only needed for the Tuning tab, which reads AMD SMU power and ' +
              'thermal limits. Everything else in OmniHub works without it.' + #13#10#13#10 +
              'Open pawnio.eu to install it by hand?',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExec('open', 'https://pawnio.eu', '', '', SW_SHOW, ewNoWait, ResultCode);
  end;
end;

{ Registered by asking the application to do it, not by writing the XML here.

  That XML is not boilerplate: it sets DisallowStartIfOnBatteries and StopIfGoingOnBatteries
  to false, which is what stops Task Scheduler refusing to start OmniHub on battery and
  terminating it the moment the charger comes out. A second copy of it in this script would
  drift from the one in StartupManager, and the copy that drifts is the one nobody tests. }
procedure RegisterStartupTask;
var
  ResultCode: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Registering the sign-in task...';
  if not Exec(ExpandConstant('{app}\{#AppExe}'), '-InstallStartup', '',
              SW_HIDE, ewWaitUntilTerminated, ResultCode) or (ResultCode <> 0) then
    MsgBox('The sign-in task could not be registered.' + #13#10#13#10 +
           'OmniHub is installed and will run normally; it just will not start by ' +
           'itself. You can turn it on later under Settings, "Launch when you sign in".',
           mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then
    Exit;

  { Order matters: PawnIO first, because it is the slow one and the sign-in task is the step
    whose failure the user most needs to see reported at the end. }
  if WizardIsTaskSelected('pawnio') then
    InstallPawnIo;

  if WizardIsTaskSelected('startup') then
    RegisterStartupTask;
end;
