; Fast Translation 安装器。
; 用它替掉 v1.x 的 WiX MSI，原因有两条（都是用户提的）：要一个经典安装向导，让用户
; 自选安装目录，还要一条程序自己就能触发的无人值守升级路径。
; 按用户安装（不弹 UAC），这样自动更新永远不会弹同意框。

#define AppName "Fast Translation"
#define AppVersion "1.5.0"
#define AppPublisher "FastTranslation Project"
#define AppExeName "FastTranslation.exe"

[Setup]
; AppId 与 MSI 的 product code 不同：这是另一个包，如果复用
; MSI 身份，Windows Installer 会去尝试修复旧的那个。
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
; 默认值是 "auto"：只要这个 AppId 已注册，就隐藏目录页 ——
; 这就是升级时看不到改路径入口的原因。改成始终显示该页。
DisableDirPage=no
; 静默升级必须保留用户当初选的目录。
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
; 应用是托盘程序，关闭按钮只是隐藏窗口，所以 Restart Manager 无法
; 关掉它。升级路径改为显式等待调用方的 PID（见 [Code]）。
CloseApplications=no
RestartApplications=no
DisableWelcomePage=no

; --- 外观 -----------------------------------------------------------------------------
; "modern" 去掉灰色页面填充和斜面分隔线，"polar" 把内置配色
; 从中性灰偏向蓝色，"hidebevels" 去掉剩下那些横向分隔线，让各页
; 看起来是一整块表面。页面底色就用应用自己的 #F7F9FC，让向导和程序
; 看起来像同一个产品。
; 故意不用 "dynamic"：深色调色板只重绘窗体，不重绘 notebook 页面
; 的底色，所以深色 Windows 会在白底页面上渲染出接近白色的文字。两种模式下
; 都用同一套奶白向导才诚实；只重绘一半的向导没法看。
WizardStyle=modern polar hidebevels
WizardBackColor=#F7F9FC
; 欢迎页/完成页左侧的横幅，以及页头里的小方块。
; 按 DPI 列了多个尺寸，位图永远不会被重采样；横幅比例 164:314，方块为正方形。
WizardImageFile=..\assets\installer\wizard-240x459.png,..\assets\installer\wizard-336x643.png,..\assets\installer\wizard-403x772.png
WizardSmallImageFile=..\assets\installer\small-116.png,..\assets\installer\small-147.png,..\assets\installer\small-159.png
WizardImageStretch=yes
; 字体族与应用窗口一致。
DefaultDialogFontName=Microsoft YaHei UI
; 只有一种语言，没什么可选的。
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
; 静默升级时也会执行（没有 skipifsilent）：应用把安装交给我们后退出，
; 否则就没有别的东西能把它重新拉起来。
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "FastTranslation"; ValueData: """{app}\{#AppExeName}"""; Flags: uninsdeletevalue; Tasks: autostart

[Code]
const
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall';
  { 应用自己的调色板，按 $BBGGRR（Delphi 的 TColor 字节序），让向导和
    程序在 Pascal Script 真正能绘制的每一种颜色上保持一致。 }
  LightBrand = $FF6225;      { #2562FF }
  DarkBrand = $FF824A;       { #4A82FF }
  LightPrimary = $36251B;    { #1B2536 }
  DarkPrimary = $F3ECE8;     { #E8ECF3 }
  LightSecondary = $826B5C;  { #5C6B82 }
  DarkSecondary = $BFAEA3;   { #A3AEBF }
  LightHair = $F3EAE4;       { #E4EAF3 }
  DarkHair = $3B312B;        { #2B313B }
  LightBack = $FCF9F7;       { #F7F9FC，即 WizardBackColor 页面底色 }
  PageCount = 6;

var
  UpdatePid: Cardinal;
  WizardBack: TColor;
  ProgressTrack, ProgressFill, FooterHair: TPanel;
  ProgressTarget: Integer;
  AnimTimer: LongWord;
  AnimAlive: Boolean;

{ Pascal Script 没有 Win32 导入表；这几项是我们唯一需要的 API 调用。 }
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

{ 只有控件不再跟随 VCL 样式后主题绘制才生效，而且必须先关掉样式
  再赋值颜色。 }
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

{ 去掉样式同时也去掉了主题（透明）背景，标签会在页面上画出一个白块；
  必须把它自己的 Color 设成页面底色才看不出来。 }
procedure ColorText(Text: TNewStaticText; Color: TColor; Back: TColor; Size: Integer; Bold: Boolean);
begin
  Text.StyleElements := [];
  Text.Color := Back;
  Text.Font.Color := Color;
  Text.Font.Size := Size;
  if Bold then Text.Font.Style := [fsBold] else Text.Font.Style := [];
end;

{ /UPDATEPID= —— 应用自我升级时设置。 }
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

{ WaitForSingleObject 返回 WAIT_TIMEOUT 说明进程还在跑。已死的 pid
  打不开，这正是正常的退出条件。 }
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

{ 应用是托盘程序：关闭按钮只是隐藏窗口，所以 Restart Manager（
  CloseApplications 那套机制）关不掉它，它加载过的每个文件都拒绝被覆盖。
  用户下载 Setup.exe 在应用还缩在托盘时运行，会在中途撞上
  "DeleteFile failed; code 5"。这里直接杀掉它是安全的——它没有未保存的状态，
  而且 [Run] 会把它重新启动——自我更新时也无害，那时它已经退出了。 }
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

{ 卸载 1.0.x 的 MSI。否则两个包会在同一目录里拥有重叠的文件，
  之后 MSI 修复会删掉它认为属于自己的那份（v29 事故）。 }
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

{ --- 动效 --------------------------------------------------------------------------------- }
{ 每次走剩余距离的四分之一，缓动到新宽度；
  纯线性步进在 20 ms 定时器上看起来会一顿一顿。 }
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

{ 完成页没有「下一步」按钮，安装中的页面又很忙，但两者仍然带着
  进度条，所以用户能看到的每个页面都把步数固定为六。 }
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

  { FinishedLabel 的文案要等这一页成为当前页后向导才会填，所以它的
    框只能在这里重新适配。 }
  if CurPageID = wpFinished then WizardForm.FinishedLabel.AdjustHeight;
end;

{ 向导是按自己默认的 9pt 给这些标签排版的。字号调大后文字比
  给定的框更高，每个标题都会溢到下一行：逐个重新适配框，
  再按框增加的高度把后面的标签往下推。 }
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

{ mbSecond | MinVersion 10.0 在上面已强制；完成页的提示点明
  首次使用者需要知道的两件事。 }
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

  { polar 样式自己绘制按钮外观，也不提供颜色属性，所以只剩下字重和
    字号两个可调的杠杆。 }
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

{ 旧 MSI 必须在新文件落地之前清掉：两个包会抢占同样的路径。
  卸载可能要花一阵（msiexec 跑的是完整卸载），所以交互式运行给一个
  跑马灯页面而不是卡死的向导；静默运行则必须保持安静。 }
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
