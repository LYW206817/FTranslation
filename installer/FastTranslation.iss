; Fast Translation installer.
; Replaces the v1.x WiX MSI for two reasons the user asked for: a classic setup wizard that lets
; the user pick the install folder, and an unattended upgrade path the app can trigger itself.
; Per-user install (no UAC) so an automatic update never shows a consent prompt.

#define AppName "Fast Translation"
#define AppVersion "1.3.2"
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
; Default is "auto", which hides the folder page whenever this AppId is already registered —
; that is why an upgrade showed no way to change the path. Always offer the page instead.
DisableDirPage=no
; A silent upgrade must keep the folder the user originally picked.
UsePreviousAppDir=yes
PrivilegesRequired=lowest
OutputDir=..\publish\dist
OutputBaseFilename=FastTranslation-{#AppVersion}-Setup
SetupIconFile=..\assets\icon\FastTranslation.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; The app is a tray app whose Close button only hides the window, so Restart Manager cannot
; close it. The update path waits for the caller's PID explicitly instead (see [Code]).
CloseApplications=no
RestartApplications=no
DisableWelcomePage=no

; --- appearance -----------------------------------------------------------------------------
; "modern" drops the grey page fill and the bevel line, "polar" shifts the built-in palette towards
; blue instead of neutral grey, and "hidebevels" removes the remaining horizontal rules so the pages
; read as one surface. The page colour is the app's own #F7F9FC, so the wizard and the program look
; like the same product.
; Deliberately no "dynamic": the dark palette only recolours the form, not the notebook page
; surfaces, so a dark Windows would render near-white text on a white page. One cream-white wizard
; in both modes is honest; a half-recoloured one is unreadable.
WizardStyle=modern polar hidebevels
WizardBackColor=#F7F9FC
; Left banner on the welcome/finished pages and the small square in the page header. Several
; sizes are listed per DPI so the bitmap is never resampled; aspect ratio 164:314 and square.
WizardImageFile=..\assets\installer\wizard-240x459.png,..\assets\installer\wizard-336x643.png,..\assets\installer\wizard-403x772.png
WizardSmallImageFile=..\assets\installer\small-116.png,..\assets\installer\small-147.png,..\assets\installer\small-159.png
WizardImageStretch=yes
; Same font family as the app windows.
DefaultDialogFontName=Microsoft YaHei UI
; A single language means there is nothing to choose.
ShowLanguageDialog=no

[Languages]
Name: "zh"; MessagesFile: "Languages\ChineseSimplified.isl"

[CustomMessages]
zh.LaunchApp=启动 {#AppName}
zh.PrepareCaption=正在准备安装
zh.PrepareDesc=正在清理上一版留下的组件,请稍候…
zh.FinishedHint=以后有新版本时,程序会自己在后台发现并提示,不必再找安装包。
zh.CreateDesktopIcon=创建桌面快捷方式

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "开机时自动启动 {#AppName}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

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

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FastTranslation"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  { The app's own palette, as $BBGGRR (Delphi's TColor byte order), so the wizard and the
    program agree on every colour that Pascal Script is actually allowed to paint. }
  LightBrand = $FF6225;      { #2562FF }
  DarkBrand = $FF824A;       { #4A82FF }
  LightPrimary = $36251B;    { #1B2536 }
  DarkPrimary = $F3ECE8;     { #E8ECF3 }
  LightSecondary = $826B5C;  { #5C6B82 }
  DarkSecondary = $BFAEA3;   { #A3AEBF }
  LightHair = $F3EAE4;       { #E4EAF3 }
  DarkHair = $3B312B;        { #2B313B }
  LightBack = $FCF9F7;       { #F7F9FC, the WizardBackColor page fill }
  PageCount = 6;

var
  UpdatePid: Cardinal;
  WizardBack: TColor;
  ProgressTrack, ProgressFill, FooterHair: TPanel;
  ProgressTarget: Integer;
  AnimTimer: LongWord;
  AnimAlive: Boolean;

{ Pascal Script has no Win32 import table; these four are the only API calls we need. }
function OpenProcess(dwDesiredAccess: Cardinal; bInheritHandle: Boolean; dwProcessId: Cardinal): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(hHandle: THandle; dwMilliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(hObject: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';
function SetTimer(hWnd, nIDEvent, uElapse, lpTimerFunc: LongWord): LongWord;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd, uIDEvent: LongWord): Boolean;
  external 'KillTimer@user32.dll stdcall';

function BrandColor: TColor;
begin
  if IsDarkInstallMode then Result := DarkBrand else Result := LightBrand;
end;

function PrimaryColor: TColor;
begin
  if IsDarkInstallMode then Result := DarkPrimary else Result := LightPrimary;
end;

function SecondaryColor: TColor;
begin
  if IsDarkInstallMode then Result := DarkSecondary else Result := LightSecondary;
end;

function HairColor: TColor;
begin
  if IsDarkInstallMode then Result := DarkHair else Result := LightHair;
end;

{ Theme painting only works once a control stops deferring to the VCL style, and the style has
  to be switched off before the colour is assigned. }
procedure Paint(Panel: TPanel; Color: TColor; L, T, W, H: Integer);
begin
  Panel.StyleElements := [];
  Panel.BevelOuter := bvNone;
  Panel.Color := Color;
  Panel.SetBounds(L, T, W, H);
end;

function NewPanel(Parent: TWinControl): TPanel;
begin
  Result := TPanel.Create(WizardForm);
  Result.Parent := Parent;
end;

{ Dropping the style also drops the themed (transparent) background, so the label would paint a
  white box over the page; its own Color has to be set to the page colour to stay invisible. }
procedure ColorText(Text: TNewStaticText; Color: TColor; Back: TColor; Size: Integer; Bold: Boolean);
begin
  Text.StyleElements := [];
  Text.Color := Back;
  Text.Font.Color := Color;
  Text.Font.Size := Size;
  if Bold then Text.Font.Style := [fsBold] else Text.Font.Style := [];
end;

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

{ The app is a tray program: its Close button only hides the window, so Restart Manager (the
  CloseApplications machinery) cannot shut it down and every file it has loaded refuses to be
  replaced. A user who downloads Setup.exe and runs it while the app is in the tray would hit
  "DeleteFile failed; code 5" halfway through. Killing it here is safe — it keeps no unsaved state
  and [Run] starts it again — and harmless during a self-update, where it has already exited. }
procedure CloseRunningApp();
var
  rc: Integer;
begin
  if UpdatePid <> 0 then Exit;
  if not Exec('taskkill.exe', '/f /im ' + '{#AppExeName}', '', SW_HIDE, ewWaitUntilTerminated, rc) then
    Log('taskkill could not be started')
  else
    Log(Format('taskkill returned %d (128 = no such process)', [rc]));
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

{ --- motion --------------------------------------------------------------------------------- }
{ Ease towards the new width a quarter of the remaining distance at a time; a plain linear
  step reads as a stutter on a 20 ms timer. }
procedure AnimTick(Arg1, Arg2, Arg3, Arg4: LongWord);
var
  step: Integer;
begin
  if not AnimAlive then Exit;
  step := (ProgressTarget - ProgressFill.Width) div 4;
  if step = 0 then
  begin
    ProgressFill.Width := ProgressTarget;
    AnimAlive := False;
    KillTimer(0, AnimTimer);
    Exit;
  end;
  ProgressFill.Width := ProgressFill.Width + step;
end;

procedure AnimateProgress(Target: Integer);
begin
  ProgressTarget := Target;
  if HighContrastActive then Exit;
  AnimAlive := True;
  KillTimer(0, AnimTimer);
  AnimTimer := SetTimer(0, 0, 20, CreateCallback(@AnimTick));
end;

{ The finished page has no Next button and the installing page is busy, but both still carry the
  bar, so the step count stays fixed at six for every page the user can see. }
function PageStep(CurPageID: Integer): Integer;
begin
  if CurPageID = wpWelcome then Result := 1
  else if CurPageID = wpSelectDir then Result := 2
  else if CurPageID = wpSelectTasks then Result := 3
  else if CurPageID = wpReady then Result := 4
  else if CurPageID = wpInstalling then Result := 5
  else Result := PageCount;
end;

procedure LayoutChrome();
var
  w, h: Integer;
begin
  w := WizardForm.ClientWidth;
  h := WizardForm.ClientHeight;
  Paint(ProgressTrack, HairColor, 0, h - ScaleY(3), w, ScaleY(3));
  Paint(ProgressFill, BrandColor, 0, h - ScaleY(3), ProgressFill.Width, ScaleY(3));
  Paint(FooterHair, HairColor, 0, WizardForm.NextButton.Top - ScaleY(12), w, ScaleY(1));
  ProgressTrack.BringToFront();
  ProgressFill.BringToFront();
  FooterHair.BringToFront();
end;

procedure CurPageChanged(CurPageID: Integer);
var
  target: Integer;
begin
  target := (WizardForm.ClientWidth * PageStep(CurPageID)) div PageCount;
  AnimateProgress(target);

  { FinishedLabel carries a caption the wizard only fills in once this page is current, so its
    box can only be re-fitted here. }
  if CurPageID = wpFinished then WizardForm.FinishedLabel.AdjustHeight;
end;

{ The wizard lays these labels out for its own 9pt default. A larger font makes the text taller
  than the box it was given, so every heading overflows onto the line below it: re-fit each box,
  then push the following label down by whatever the box gained. }
procedure FitStackedLabels();
begin
  WizardForm.WelcomeLabel1.AdjustHeight;
  WizardForm.WelcomeLabel2.Top :=
    WizardForm.WelcomeLabel1.Top + WizardForm.WelcomeLabel1.Height + ScaleY(8);

  WizardForm.PageNameLabel.AdjustHeight;
  WizardForm.PageDescriptionLabel.Top :=
    WizardForm.PageNameLabel.Top + WizardForm.PageNameLabel.Height + ScaleY(2);
  WizardForm.PageDescriptionLabel.AdjustHeight;

  WizardForm.FinishedHeadingLabel.AdjustHeight;
  WizardForm.FinishedLabel.Top :=
    WizardForm.FinishedHeadingLabel.Top + WizardForm.FinishedHeadingLabel.Height + ScaleY(6);
end;

{ mbSecond | MinVersion 10.0 is enforced above; the hint on the finished page names the two
  things a first-time user needs to know. }
procedure InitializeWizard();
begin
  WizardBack := LightBack;
  ProgressTrack := NewPanel(WizardForm);
  ProgressFill := NewPanel(WizardForm);
  FooterHair := NewPanel(WizardForm);
  ProgressFill.Width := 0;
  LayoutChrome();

  if not HighContrastActive then
  begin
    ColorText(WizardForm.WelcomeLabel1, PrimaryColor, WizardBack, 22, True);
    ColorText(WizardForm.WelcomeLabel2, SecondaryColor, WizardBack, 11, False);
    ColorText(WizardForm.PageNameLabel, PrimaryColor, WizardBack, 15, True);
    ColorText(WizardForm.PageDescriptionLabel, SecondaryColor, WizardBack, 11, False);
    ColorText(WizardForm.FinishedHeadingLabel, PrimaryColor, WizardBack, 20, True);
    ColorText(WizardForm.FinishedLabel, SecondaryColor, WizardBack, 11, False);
    FitStackedLabels();
  end;

  { The polar style paints the button faces itself and offers no colour property, so weight and
    size are the only levers left on them. }
  WizardForm.NextButton.Font.Size := 11;
  WizardForm.NextButton.Font.Style := [fsBold];
  WizardForm.BackButton.Font.Size := 11;
  WizardForm.CancelButton.Font.Size := 11;

  WizardForm.FinishedLabel.Caption :=
    WizardForm.FinishedLabel.Caption + #13#10#13#10 + ExpandConstant('{cm:FinishedHint}');
end;

procedure DeinitializeSetup();
begin
  AnimAlive := False;
  KillTimer(0, AnimTimer);
end;

{ The old MSI must go before the new files land: both packages would claim the same paths.
  Removing it can take a while (msiexec runs a full uninstall), so an interactive run gets a
  marquee page instead of a frozen wizard; a silent run must stay silent. }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  page: TOutputMarqueeProgressWizardPage;
begin
  Result := '';
  NeedsRestart := False;
  if WizardSilent() then
  begin
    CloseRunningApp();
    UninstallLegacyMsi();
    Exit;
  end;
  page := CreateOutputMarqueeProgressPage(ExpandConstant('{cm:PrepareCaption}'),
    ExpandConstant('{cm:PrepareDesc}'));
  try
    page.Show();
    page.Animate();
    CloseRunningApp();
    UninstallLegacyMsi();
  finally
    page.Hide();
  end;
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
