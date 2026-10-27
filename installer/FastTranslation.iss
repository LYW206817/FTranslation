; Fast Translation installer.
; Replaces the v1.x WiX MSI for two reasons the user asked for: a classic setup wizard that lets
; the user pick the install folder, and an unattended upgrade path the app can trigger itself.
; Per-user install (no UAC) so an automatic update never shows a consent prompt.

#define AppName "Fast Translation"
#define AppVersion "1.1.0"
#define AppPublisher "FastTranslation Project"
#define AppExeName "FastTranslation.exe"

[Setup]
; A different AppId than the MSI product code: this is a separate package, and reusing the
; MSI identity would make Windows Installer try to repair the old one.
AppId={{8B1F2C34-7A6D-4E58-9C21-5F3D8E7A1B60}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/LYW206817/FTranslation-Updates
AppSupportURL=https://github.com/LYW206817/FTranslation-Updates
DefaultDirName={localappdata}\Programs\FastTranslation
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\publish\dist
OutputBaseFilename=FastTranslation-{#AppVersion}-Setup
SetupIconFile=..\assets\icon\FastTranslation.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; The app is a tray app whose Close button only hides the window, so Restart Manager cannot
; close it. The update path waits for the caller's PID explicitly instead (see [Code]).
CloseApplications=no
RestartApplications=no
DisableWelcomePage=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
en.LaunchApp=Launch {#AppName}

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\ft-folder\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{cm:UninstallProgram,{#AppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Runs in a silent upgrade too (no skipifsilent): the app hands the install over to us and exits,
; so nothing else would bring it back.
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';

var
  UpdatePid: Cardinal;

{ Pascal Script has no Win32 import table; these three are the only API calls we need. }
function OpenProcess(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; dwProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

{ /UPDATEPID= — set by the app when it upgrades itself. }
procedure ParseUpdatePid();
var
  i, p: Integer;
  param: String;
begin
  UpdatePid := 0;
  for i := 1 to ParamCount do
  begin
    param := ParamStr(i);
    if CompareText(Copy(param, 1, 11), '/UPDATEPID=') = 0 then
    begin
      p := StrToIntDef(Copy(param, 12, Length(param)), 0);
      if p > 0 then
        UpdatePid := Cardinal(p);
    end;
  end;
end;

{ WaitForSingleObject returning WAIT_TIMEOUT means the process is still running. A dead pid
  cannot be opened, which is the normal exit condition. }
function ProcessAlive(Pid: Cardinal): Boolean;
var
  h: THandle;
begin
  Result := False;
  if Pid = 0 then Exit;
  h := OpenProcess($00100000 or $1000, False, Pid);
  if h = 0 then Exit;
  try
    Result := WaitForSingleObject(h, 0) = $00000102;
  finally
    CloseHandle(h);
  end;
end;

procedure WaitForUpdateProcess();
var
  waited: Integer;
begin
  if UpdatePid = 0 then Exit;
  Log(Format('waiting for the updating app (pid %d) to exit', [UpdatePid]));
  waited := 0;
  while ProcessAlive(UpdatePid) and (waited < 60000) do
  begin
    Sleep(250);
    waited := waited + 250;
  end;
  Log(Format('waited %d ms for pid %d', [waited, UpdatePid]));
end;

{ Uninstalls a 1.0.x MSI. Both packages would otherwise own overlapping files in the same
  folder, and a later MSI repair deletes what it thinks is its own (the v29 incident). }
procedure UninstallLegacyMsi();
var
  roots: array[0..2] of Integer;
  root, i, j, rc: Integer;
  names: TArrayOfString;
  displayName, uninstallString: String;
begin
  roots[0] := HKEY_CURRENT_USER;
  roots[1] := HKEY_LOCAL_MACHINE;
  roots[2] := HKEY_LOCAL_MACHINE_32;
  for i := 0 to 2 do
  begin
    root := roots[i];
    if not RegGetSubkeyNames(root, UninstallKey, names) then Continue;
    for j := 0 to GetArrayLength(names) - 1 do
    begin
      if not RegQueryStringValue(root, UninstallKey + '\' + names[j], 'DisplayName', displayName) then Continue;
      if CompareText(displayName, 'Fast Translation') <> 0 then Continue;
      if Copy(names[j], 1, 1) <> '{' then Continue;
      if not RegQueryStringValue(root, UninstallKey + '\' + names[j], 'UninstallString', uninstallString) then Continue;
      if Pos('msiexec', Lowercase(uninstallString)) = 0 then Continue;
      Log('uninstalling legacy MSI ' + names[j]);
      Exec('msiexec.exe', '/x ' + names[j] + ' /qn /norestart', '', SW_HIDE, ewWaitUntilTerminated, rc);
    end;
  end;
end;

{ The old MSI must go before the new files land: both packages would claim the same paths. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  NeedsRestart := False;
  UninstallLegacyMsi();
end;

function InitializeSetup(): Boolean;
begin
  ParseUpdatePid();
  Log('localappdata = ' + ExpandConstant('{localappdata}'));
  Log('default dir = ' + ExpandConstant('{localappdata}\Programs\FastTranslation'));
  Log('previous dir = ' + GetPreviousData('AppDir', '<none>'));
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    WaitForUpdateProcess();
end;
