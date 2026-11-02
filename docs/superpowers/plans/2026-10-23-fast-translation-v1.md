# Fast Translation Windows v1.0 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **License note (2026-11-02):** the project was relicensed to AGPL-3.0; "MIT" mentions in this plan are historical records. See `LICENSE`.

**Goal:** Build the Windows v1.0 public beta of Fast Translation: a low-memory WPF tray app that captures selected text, translates it through a user-supplied Qwen-MT key, and supports copy/replace/insert.

**Architecture:** Single-process WPF app split into `Core` (platform-free logic), `Platform.Windows` (hooks, UIA, clipboard, overlay, tray), `Providers.QwenMt` (HTTP provider), and `App` (composition/UI). M0 is a throwaway probe that proves UIA coverage, no-activate focus behavior, memory, and write-back feasibility before product code starts.

**Tech Stack:** C# / .NET 10 LTS / WPF; `UIAutomationClient`; native Win32 P/Invoke; `HttpClient`; `System.Text.Json`; DPAPI; xUnit. No MVVM/DI/logging frameworks.

**Spec:** [2026-10-22-fast-translation-design.md](../specs/2026-10-22-fast-translation-design.md)

---

## Global Constraints

- Target: Windows 10 22H2 / Windows 11, x64. `Core` targets `net10.0`; `App` and `Platform.Windows` target `net10.0-windows`.
- Publish: `win-x64`, self-contained, not single-file, not trimmed.
- Memory gate: cold idle ≤50MB private working set after 5 minutes; after 100 translations ≤60MB and no monotonic growth.
- Latency gates: button p50 ≤300ms; 200-char translation p50 ≤2s (network-dependent).
- No MVVM, DI, logging, or auto-update frameworks. Native `Shell_NotifyIcon` tray; no WinForms. The only allowed runtime NuGet package is Microsoft's `System.Security.Cryptography.ProtectedData` for DPAPI.
- BYO Qwen-MT key; default model `qwen-mt-flash`; source `"auto"`; target English language name; 5,000-character cap; no streaming/chunking in v1.0.
- Clipboard fallback default ON; terminals and password managers excluded by default; text-only backup/restore; 300ms polling default.
- Write-back only after M0 gate; require source HWND + UIA RuntimeId + selection-text verification; clipboard paste first, native Unicode `SendInput` fallback; no automatic retry; terminals/read-only/admin windows disabled.
- No text in logs; no telemetry; API key via DPAPI CurrentUser.
- Unsigned beta; MIT; GitHub Releases; no auto-update; `THIRD_PARTY_NOTICES` if MIT/Apache code is adapted.
- User workflow: **do not `git init` or commit unless the user explicitly approves.** Every task ends with a checkpoint that reports changed files + test output; commits are deferred.
- M0 must pass before Phase 1. If M0 fails the 50MB gate or UIA/write-back gates, update the spec and stop for a decision.

---

## File Structure

```
D:\FTranslation
├─ FastTranslation.sln
├─ src/
│  ├─ FastTranslation.Core/
│  │  ├─ Abstractions/ISelectionProvider.cs
│  │  ├─ Abstractions/IOverlayPresenter.cs
│  │  ├─ Abstractions/ITranslationProvider.cs
│  │  ├─ Abstractions/IWriteBackService.cs
│  │  ├─ Abstractions/ISecretStore.cs
│  │  ├─ Abstractions/ISettingsStore.cs
│  │  ├─ Selection/ScreenPoint.cs
│  │  ├─ Selection/ScreenRect.cs
│  │  ├─ Selection/SelectionSource.cs
│  │  ├─ Selection/SelectionSnapshot.cs
│  │  ├─ Selection/TextNormalizer.cs
│  │  ├─ Selection/SelectionCoordinator.cs
│  │  ├─ Language/LanguagePolicy.cs
│  │  ├─ Language/LanguageCatalog.cs
│  │  ├─ Translation/TranslationRequest.cs
│  │  ├─ Translation/TranslationResult.cs
│  │  ├─ Translation/TranslationError.cs
│  │  ├─ Translation/TranslationException.cs
│  │  ├─ Translation/LruCache.cs
│  │  ├─ Translation/TranslationService.cs
│  │  ├─ WriteBack/WriteBackStatus.cs
│  │  ├─ WriteBack/WriteBackResult.cs
│  │  ├─ WriteBack/WriteBackDecision.cs
│  │  └─ Settings/SettingsModel.cs
│  │     Settings/SettingsStore.cs
│  ├─ FastTranslation.Platform.Windows/
│  │  ├─ Input/GlobalMouseMonitor.cs
│  │  ├─ Input/MouseGestureDetector.cs
│  │  ├─ Uia/UiaSelectionReader.cs
│  │  ├─ Clipboard/ClipboardService.cs
│  │  ├─ Clipboard/ClipboardFallback.cs
│  │  ├─ WriteBack/ClipboardWriteBack.cs
│  │  ├─ Overlay/ButtonWindow.xaml(.cs)
│  │  ├─ Overlay/ResultWindow.xaml(.cs)
│  │  ├─ Overlay/OverlayPresenter.cs
│  │  ├─ Hotkey/GlobalHotkeyManager.cs
│  │  ├─ Tray/ShellTrayIcon.cs
│  │  ├─ Startup/StartupRegistration.cs
│  │  └─ Security/DpapiSecretStore.cs
│  ├─ FastTranslation.Providers.QwenMt/
│  │  ├─ QwenMtProvider.cs
│  │  └─ QwenMtContracts.cs
│  └─ FastTranslation.App/
│     ├─ App.xaml(.cs)
│     ├─ CompositionRoot.cs
│     ├─ app.manifest
│     ├─ Settings/SettingsWindow.xaml(.cs)
│     └─ Onboarding/OnboardingWindow.xaml(.cs)
├─ tests/
│  ├─ FastTranslation.Core.Tests/
│  ├─ FastTranslation.Providers.QwenMt.Tests/
│  └─ FastTranslation.Platform.Windows.Tests/
├─ tools/Probe/
├─ installer/FastTranslation.iss
├─ docs/superpowers/specs/2026-10-22-fast-translation-design.md
├─ docs/superpowers/plans/2026-10-23-fast-translation-v1.md
├─ README.md
└─ THIRD_PARTY_NOTICES.md
```

---

## Phase 0 — M0 Feasibility Gate

### Task 0.1: Environment and probe scaffold

**Files:**
- Create: `tools/Probe/Probe.csproj`
- Create: `tools/Probe/App.xaml`, `App.xaml.cs`
- Create: `tools/Probe/MainWindow.xaml`, `MainWindow.xaml.cs`
- Create: `tools/Probe/RESULTS.md`

**Interfaces:**
- Produces: a runnable throwaway probe with a log surface and buttons for capture/memory.

- [ ] **Step 1: Verify SDK**

Run:
```bash
dotnet --list-sdks
```
Expected: a `10.x` SDK. If only runtimes are listed, stop and ask the user to install .NET 10 SDK; do not install system software without approval.

- [ ] **Step 2: Create probe project files**

`tools/Probe/Probe.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>Probe</AssemblyName>
  </PropertyGroup>
</Project>
```

`tools/Probe/App.xaml`:
```xml
<Application x:Class="Probe.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml" />
```

`tools/Probe/App.xaml.cs`:
```csharp
namespace Probe;

public partial class App : System.Windows.Application { }
```

- [ ] **Step 3: Create probe window**

`tools/Probe/MainWindow.xaml`:
```xml
<Window x:Class="Probe.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Fast Translation Probe" Width="760" Height="520">
  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="*"/>
    </Grid.RowDefinitions>
    <StackPanel Orientation="Horizontal" Grid.Row="0">
      <Button x:Name="StartButton" Content="Start hook" Padding="12,6" Click="StartButton_Click"/>
      <Button x:Name="MeasureButton" Content="Measure memory" Padding="12,6" Margin="8,0,0,0" Click="MeasureButton_Click"/>
      <CheckBox x:Name="CaptureOnClickCheck" Content="Capture on click only (STranslate-style)"
                Margin="16,0,0,0" VerticalAlignment="Center"/>
      <TextBlock x:Name="MemoryText" Margin="16,0,0,0" VerticalAlignment="Center" FontWeight="SemiBold"/>
    </StackPanel>
    <TextBlock Grid.Row="1" Margin="0,8,0,4" Text="Log (this probe is throwaway; do not merge into product):"/>
    <TextBox x:Name="LogBox" Grid.Row="2" AcceptsReturn="True" VerticalScrollBarVisibility="Auto"
             HorizontalScrollBarVisibility="Auto" FontFamily="Consolas" IsReadOnly="True"/>
  </Grid>
</Window>
```

`tools/Probe/MainWindow.xaml.cs` minimal shell:
```csharp
using System.Windows;
using System.Diagnostics;

namespace Probe;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void Log(string message)
    {
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
        LogBox.ScrollToEnd();
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        Log("Probe started. Implement hook/UIA in Task 0.2.");
    }

    private void MeasureButton_Click(object sender, RoutedEventArgs e)
    {
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        var ws = p.WorkingSet64 / 1024 / 1024;
        var priv = p.PrivateMemorySize64 / 1024 / 1024;
        MemoryText.Text = $"WS {ws}MB / Private {priv}MB / Handles {p.HandleCount}";
        Log(MemoryText.Text);
    }
}
```

- [ ] **Step 4: Build**

Run:
```bash
dotnet build tools/Probe/Probe.csproj -c Release
```
Expected: build succeeds.

- [ ] **Step 5: Checkpoint**

Report: created probe files, SDK version, build result. Do not commit unless the user explicitly approves.

---

### Task 0.2: Probe hook, UIA, no-activate overlay, memory

**Files:**
- Modify: `tools/Probe/MainWindow.xaml.cs`
- Create: `tools/Probe/RESULTS.md`

**Interfaces:**
- Produces: measurement data for UIA text/rect success, focus preservation, button latency, memory, and clipboard side effects.

- [ ] **Step 1: Add P/Invoke and hook code**

Add to `MainWindow.xaml.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;

private const int WH_MOUSE_LL = 14;
private const int WM_LBUTTONDOWN = 0x0201;
private const int WM_LBUTTONUP = 0x0202;
private const int WM_MOUSEACTIVATE = 0x0021;
private const int MA_NOACTIVATE = 3;
private const int GWL_EXSTYLE = -20;
private const int WS_EX_NOACTIVATE = 0x08000000;
private const int WS_EX_TOOLWINDOW = 0x00000080;
private const int WS_EX_TOPMOST = 0x00000008;
private const int IDC_IBEAM = 32513;

private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

[StructLayout(LayoutKind.Sequential)]
private struct POINT { public int X; public int Y; }

[StructLayout(LayoutKind.Sequential)]
private struct MSLLHOOKSTRUCT
{
    public POINT pt;
    public uint mouseData;
    public uint flags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
private struct CURSORINFO
{
    public int cbSize;
    public int flags;
    public IntPtr hCursor;
    public POINT ptScreenPos;
}

[DllImport("user32.dll", SetLastError = true)]
private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

[DllImport("user32.dll", SetLastError = true)]
private static extern bool UnhookWindowsHookEx(IntPtr hhk);

[DllImport("user32.dll")]
private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
private static extern IntPtr GetModuleHandle(string? lpModuleName);

[DllImport("user32.dll")]
private static extern bool GetCursorInfo(ref CURSORINFO pci);

[DllImport("user32.dll")]
private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

[DllImport("user32.dll")]
private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

[DllImport("user32.dll")]
private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

private IntPtr _hook;
private LowLevelMouseProc? _hookProc;
private Thread? _hookThread;
private IntPtr _ibeam;

private void StartButton_Click(object sender, RoutedEventArgs e)
{
    if (_hook != IntPtr.Zero) return;
    _ibeam = LoadCursor(IntPtr.Zero, IDC_IBEAM);
    _hookThread = new Thread(() =>
    {
        _hookProc = HookProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            Dispatcher.Invoke(() => Log($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}"));
            return;
        }
        Dispatcher.Invoke(() => Log("WH_MOUSE_LL installed."));
        System.Windows.Threading.Dispatcher.Run();
    }) { IsBackground = true, Name = "Probe.MouseHook" };
    _hookThread.SetApartmentState(ApartmentState.STA);
    _hookThread.Start();
}

private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
{
    if (nCode >= 0 && wParam == WM_LBUTTONUP)
    {
        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
        var pt = new ScreenPoint(data.pt.X, data.pt.Y);
        if (IsIBeam())
        {
            _ = Dispatcher.InvokeAsync(async () =>
            {
                var sw = Stopwatch.StartNew();
                var selection = ReadSelectionAt(pt);
                sw.Stop();
                Log($"UIA {selection.Text.Length} chars / {selection.Rects.Count} rects / {sw.ElapsedMilliseconds}ms / {selection.Text.Replace("\n", "\\n")}");
                ShowProbeButton(pt);
            });
        }
    }
    return CallNextHookEx(_hook, nCode, wParam, lParam);
}

private bool IsIBeam()
{
    var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
    return GetCursorInfo(ref ci) && ci.hCursor == _ibeam;
}

private (string Text, List<ScreenRect> Rects) ReadSelectionAt(ScreenPoint pt)
{
    var element = AutomationElement.FromPoint(new System.Windows.Point(pt.X, pt.Y));
    for (var i = 0; i < 10 && element is not null; i++)
    {
        if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
            patternObj is TextPattern pattern)
        {
            var ranges = pattern.GetSelection();
            var text = string.Join("\n", ranges.Select(r => r.GetText(-1)));
            var rects = ranges
                .SelectMany(r => r.GetBoundingRectangles())
                .Chunk(4)
                .Select(a => new ScreenRect((int)a[0], (int)a[1], (int)a[2], (int)a[3]))
                .ToList();
            return (text, rects);
        }
        element = TreeWalker.ControlViewWalker.GetParent(element);
    }
    return (string.Empty, []);
}

private Window? _probeWindow;

private void ShowProbeButton(ScreenPoint pt)
{
    if (_probeWindow is null)
    {
        _probeWindow = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Width = 80,
            Height = 32,
            Content = new Border
            {
                Background = Brushes.White,
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Child = new TextBlock
                {
                    Text = "翻译",
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        };
        _probeWindow.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(_probeWindow).Handle;
            var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
            HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
            {
                if (msg == WM_MOUSEACTIVATE) { handled = true; return MA_NOACTIVATE; }
                return IntPtr.Zero;
            });
        };
    }
    _probeWindow.Left = pt.X;
    _probeWindow.Top = pt.Y + 8;
    if (!_probeWindow.IsVisible) _probeWindow.Show();
    else _probeWindow.Topmost = true;
}
```

Add the `ScreenPoint`/`ScreenRect` structs at the bottom of the probe file:
```csharp
public readonly record struct ScreenPoint(int X, int Y);
public readonly record struct ScreenRect(int X, int Y, int Width, int Height);
```

- [ ] **Step 2: Build and run probe**

Run:
```bash
dotnet run --project tools/Probe/Probe.csproj -c Release
```
Expected: window opens; Start hook installs; selecting text in Chrome/Edge/Word shows UIA text length and a “翻译” pill.

- [ ] **Step 3: Measure in target apps**

For each app: Chrome, Edge, Firefox, Word, Edge PDF, Acrobat (if installed), Notepad, VSCode, Windows Terminal:
- Drag-select a word and a sentence.
- Double-click a word, triple-click a paragraph.
- Record: text captured? rects captured? button appeared? source focus preserved? clipboard changed? p50 latency.
- Measure memory: Start probe, wait 5 minutes, click Measure; then perform 20 captures and measure again.

Write results to `tools/Probe/RESULTS.md`:
```markdown
| App | Text | Rects | Focus preserved | Clipboard changed | p50 ms | Notes |
|---|---|---|---|---|---|---|
| Chrome | | | | | | |
| Edge | | | | | | |
| Word | | | | | | |
| Edge PDF | | | | | | |
| Notepad | | | | | | |
```

- [ ] **Step 4: A/B capture timing**

Run the probe twice:
- `CaptureOnClickCheck` unchecked: mouse-up reads UIA immediately.
- Checked: mouse-up only shows the pill; add a pill click handler that calls `ReadSelectionAt` and logs. To add it, set `_probeWindow.MouseLeftButtonUp += (_, _) => { var s = ReadSelectionAt(new ScreenPoint((int)_probeWindow.Left, (int)_probeWindow.Top - 8)); Log($"click capture {s.Text.Length}"); };`
- Record UIA calls, Ctrl+C count (zero in this pure-UIA probe), latency, false positives on non-I-beam drags.

- [ ] **Step 5: Checkpoint**

Report: measurements, any UIA failures, focus issues, memory. No commit unless authorized.

---

### Task 0.3: Probe write-back and memory gate

**Files:**
- Modify: `tools/Probe/MainWindow.xaml.cs`
- Modify: `tools/Probe/RESULTS.md`

**Interfaces:**
- Produces: replace/insert feasibility data and the final M0 gate result.

- [ ] **Step 1: Add a replace action to the probe**

Add P/Invoke for `SendInput` and a `ReplaceSelection` method:
```csharp
[StructLayout(LayoutKind.Sequential)]
private struct INPUT { public uint type; public INPUTUNION data; }
[StructLayout(LayoutKind.Explicit)]
private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }
[StructLayout(LayoutKind.Sequential)]
private struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
private const uint INPUT_KEYBOARD = 1;
private const ushort VK_CONTROL = 0x11;
private const ushort VK_V = 0x56;
private const uint KEYEVENTF_KEYUP = 0x0002;

[DllImport("user32.dll", SetLastError = true)]
private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

private static void SendCtrlV()
{
    var inputs = new[]
    {
        new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL } } },
        new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V } } },
        new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYEVENTF_KEYUP } } },
        new INPUT { type = INPUT_KEYBOARD, data = new INPUTUNION { ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYEVENTF_KEYUP } } }
    };
    SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
}
```

Add a `ReplaceButton` to `MainWindow.xaml` and a handler:
```csharp
private void ReplaceButton_Click(object sender, RoutedEventArgs e)
{
    // Probe only: verify the foreground window still has the same selection before pasting.
    var selection = ReadSelectionAt(new ScreenPoint((int)(_probeWindow?.Left ?? 0), (int)(_probeWindow?.Top ?? 0) - 8));
    if (string.IsNullOrWhiteSpace(selection.Text)) { Log("No selection to replace."); return; }
    Clipboard.SetText("REPLACED: " + selection.Text);
    SendCtrlV();
    Log("Sent Ctrl+V replace attempt.");
}
```
Manual test in Notepad, Word, Chrome input/textarea/contenteditable, VSCode. Record success/failure and whether the wrong window was affected.

- [ ] **Step 2: Memory gate**

Run the probe, wait 5 minutes, measure; perform 100 captures; measure again. Record both. If private working set >50MB, record the delta and stop for a stack decision (WinForms/Rust) before Phase 1.

- [ ] **Step 3: Final M0 decision**

Update `tools/Probe/RESULTS.md` with one of:
- `PASS`: official apps text ≥90%, no focus loss, memory ≤50MB, write-back success ≥90% in writable targets.
- `PASS_WITH_SCOPE`: UIA gaps; keep scope to apps that passed; document failures.
- `FAIL_MEMORY`: >50MB; stop and choose WinForms/Rust.
- `FAIL_WRITEBACK`: disable replace/insert for v1.0; copy-only.
- `FAIL_UIA`: switch default to clipboard-first/onClick or reduce scope.

- [ ] **Step 4: Gate checkpoint**

Report: M0 result, measurements, decision. Do not start Phase 1 until the user confirms the gate result. No commit unless authorized.

---

## Phase 1 — Core Logic

### Task 1: Solution and core primitives

**Files:**
- Create: `FastTranslation.sln`
- Create: `src/FastTranslation.Core/FastTranslation.Core.csproj`
- Create: `src/FastTranslation.Core/Selection/ScreenPoint.cs`
- Create: `src/FastTranslation.Core/Selection/ScreenRect.cs`
- Create: `src/FastTranslation.Core/Selection/SelectionSource.cs`
- Create: `src/FastTranslation.Core/Selection/SelectionSnapshot.cs`
- Create: `tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj`
- Create: `tests/FastTranslation.Core.Tests/ScreenRectTests.cs`

**Interfaces:**
- Produces: `ScreenPoint(int X,int Y)`, `ScreenRect(int X,int Y,int Width,int Height)` with `Right`/`Bottom`/`IsEmpty`, `SelectionSource`, `SelectionSnapshot`.

- [ ] **Step 1: Create projects**

Run:
```bash
dotnet new sln -n FastTranslation
dotnet new classlib -n FastTranslation.Core -o src/FastTranslation.Core -f net10.0
dotnet new xunit -n FastTranslation.Core.Tests -o tests/FastTranslation.Core.Tests -f net10.0
dotnet sln FastTranslation.sln add src/FastTranslation.Core/FastTranslation.Core.csproj tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj
dotnet add tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj reference src/FastTranslation.Core/FastTranslation.Core.csproj
```

- [ ] **Step 2: Write failing test**

`tests/FastTranslation.Core.Tests/ScreenRectTests.cs`:
```csharp
using FastTranslation.Core.Selection;

namespace FastTranslation.Core.Tests;

public class ScreenRectTests
{
    [Fact]
    public void Right_And_Bottom_Are_Computed()
    {
        var rect = new ScreenRect(10, 20, 30, 40);
        Assert.Equal(40, rect.Right);
        Assert.Equal(60, rect.Bottom);
    }

    [Fact]
    public void IsEmpty_When_Width_Or_Height_Is_Zero()
    {
        Assert.True(new ScreenRect(0, 0, 0, 10).IsEmpty);
        Assert.True(new ScreenRect(0, 0, 10, 0).IsEmpty);
        Assert.False(new ScreenRect(0, 0, 1, 1).IsEmpty);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: compile error / missing `ScreenRect`.

- [ ] **Step 4: Implement primitives**

`ScreenPoint.cs`:
```csharp
namespace FastTranslation.Core.Selection;

public readonly record struct ScreenPoint(int X, int Y);
```

`ScreenRect.cs`:
```csharp
namespace FastTranslation.Core.Selection;

public readonly record struct ScreenRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
}
```

`SelectionSource.cs`:
```csharp
namespace FastTranslation.Core.Selection;

public enum SelectionSource
{
    UiaPoint,
    UiaFocus,
    ClipboardFallback
}
```

`SelectionSnapshot.cs`:
```csharp
namespace FastTranslation.Core.Selection;

public sealed record SelectionSnapshot(
    string Text,
    IReadOnlyList<ScreenRect> BoundingRects,
    string? ProcessName,
    bool IsPassword,
    SelectionSource Source,
    nint SourceWindowHandle,
    string? SourceElementRuntimeId,
    bool IsWritable);
```

- [ ] **Step 5: Run tests to verify pass**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: 2 passed.

- [ ] **Step 6: Checkpoint**

Report: solution created, tests passed. No commit unless authorized.

---

### Task 2: Text normalization and language policy

**Files:**
- Create: `src/FastTranslation.Core/Selection/TextNormalizer.cs`
- Create: `src/FastTranslation.Core/Language/LanguagePolicy.cs`
- Create: `tests/FastTranslation.Core.Tests/TextNormalizerTests.cs`
- Create: `tests/FastTranslation.Core.Tests/LanguagePolicyTests.cs`

**Interfaces:**
- Produces: `TextNormalizer.Normalize(string?)`, `TextNormalizer.IsTranslatable(string?)`, `LanguagePolicy.Detect(string)`, `LanguagePolicy.ResolveTargetLanguage(string, string?)`.

- [ ] **Step 1: Write failing tests**

`TextNormalizerTests.cs`:
```csharp
using FastTranslation.Core.Selection;

namespace FastTranslation.Core.Tests;

public class TextNormalizerTests
{
    [Fact]
    public void Removes_Zero_Width_And_Nbsp()
    {
        Assert.Equal("hello world", TextNormalizer.Normalize("hello\u200B\u00A0 world"));
    }

    [Fact]
    public void Collapses_Excess_Blank_Lines()
    {
        Assert.Equal("a\n\nb", TextNormalizer.Normalize("a\r\n\r\n\r\nb"));
    }

    [Theory]
    [InlineData("hello", true)]
    [InlineData("你好", true)]
    [InlineData("123 !!!", false)]
    [InlineData("", false)]
    public void IsTranslatable_Requires_Letters(string text, bool expected)
    {
        Assert.Equal(expected, TextNormalizer.IsTranslatable(text));
    }
}
```

`LanguagePolicyTests.cs`:
```csharp
using FastTranslation.Core.Language;

namespace FastTranslation.Core.Tests;

public class LanguagePolicyTests
{
    private readonly LanguagePolicy _policy = new();

    [Theory]
    [InlineData("hello world", DetectedLanguage.English)]
    [InlineData("这是中文", DetectedLanguage.Chinese)]
    [InlineData("これは日本語です", DetectedLanguage.Japanese)]
    [InlineData("안녕하세요", DetectedLanguage.Korean)]
    public void Detect_Uses_Script(string text, DetectedLanguage expected)
    {
        Assert.Equal(expected, _policy.Detect(text));
    }

    [Fact]
    public void Auto_Target_Is_English_For_Chinese()
    {
        Assert.Equal("English", _policy.ResolveTargetLanguage("你好", null));
    }

    [Fact]
    public void Auto_Target_Is_Chinese_For_Non_Chinese()
    {
        Assert.Equal("Chinese", _policy.ResolveTargetLanguage("hello", null));
    }

    [Fact]
    public void User_Override_Wins()
    {
        Assert.Equal("Japanese", _policy.ResolveTargetLanguage("hello", "Japanese"));
    }
}
```

- [ ] **Step 2: Run tests to verify fail**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: missing types.

- [ ] **Step 3: Implement**

`TextNormalizer.cs`:
```csharp
using System.Text.RegularExpressions;

namespace FastTranslation.Core.Selection;

public static partial class TextNormalizer
{
    private static readonly char[] ZeroWidth = ['\u200B', '\u200C', '\u200D', '\uFEFF'];

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Replace('\u00A0', ' ');
        foreach (var z in ZeroWidth) s = s.Replace(z.ToString(), string.Empty);
        s = LineBreakRegex().Replace(s, "\n");
        s = MultiBlankLineRegex().Replace(s, "\n\n");
        s = MultiSpaceRegex().Replace(s, " ");
        return s.Trim();
    }

    public static bool IsTranslatable(string? raw)
    {
        var s = Normalize(raw);
        return s.Length > 0 && s.Any(c => char.IsLetter(c) || IsCjk(c));
    }

    public static bool IsCjk(char c) =>
        (c >= '\u3040' && c <= '\u30FF') ||
        (c >= '\u4E00' && c <= '\u9FFF') ||
        (c >= '\uAC00' && c <= '\uD7AF');

    [GeneratedRegex(@"\r\n|\r")]
    private static partial Regex LineBreakRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiBlankLineRegex();

    [GeneratedRegex(@"[ \t]{2,}")]
    private static partial Regex MultiSpaceRegex();
}
```

`LanguagePolicy.cs`:
```csharp
namespace FastTranslation.Core.Language;

public enum DetectedLanguage
{
    Chinese,
    English,
    Japanese,
    Korean,
    Other
}

public sealed class LanguagePolicy
{
    public DetectedLanguage Detect(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return DetectedLanguage.Other;
        if (text.Any(IsKana)) return DetectedLanguage.Japanese;
        if (text.Any(IsHangul)) return DetectedLanguage.Korean;

        var letters = text.Count(char.IsLetter);
        var han = text.Count(IsHan);
        if (letters > 0 && han * 4 >= letters) return DetectedLanguage.Chinese;
        if (text.Any(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')) return DetectedLanguage.English;
        return DetectedLanguage.Other;
    }

    public string ResolveTargetLanguage(string text, string? userOverride)
    {
        if (!string.IsNullOrWhiteSpace(userOverride)) return userOverride.Trim();
        return Detect(text) == DetectedLanguage.Chinese ? "English" : "Chinese";
    }

    private static bool IsKana(char c) => c >= '\u3040' && c <= '\u30FF';
    private static bool IsHangul(char c) => c >= '\uAC00' && c <= '\uD7AF';
    private static bool IsHan(char c) => c >= '\u4E00' && c <= '\u9FFF';
}
```

- [ ] **Step 4: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: all pass.

- [ ] **Step 5: Checkpoint**

Report: normalization/language tests pass; note mixed-script edge cases. No commit unless authorized.

---

### Task 2b: Language catalog

**Files:**
- Create: `src/FastTranslation.Core/Language/LanguageCatalog.cs`
- Create: `src/FastTranslation.Core/Language/languages.json`
- Create: `tests/FastTranslation.Core.Tests/LanguageCatalogTests.cs`

**Interfaces:**
- Produces: `LanguageDefinition(string Code,string QwenName,string DisplayName)`, `LanguageCatalog.All`, `LanguageCatalog.ToQwenName(string?)`.

- [ ] **Step 1: Write failing test**

`LanguageCatalogTests.cs`:
```csharp
using FastTranslation.Core.Language;

namespace FastTranslation.Core.Tests;

public class LanguageCatalogTests
{
    [Fact]
    public void Contains_Common_Languages()
    {
        Assert.Contains(LanguageCatalog.All, x => x.Code == "zh" && x.QwenName == "Chinese");
        Assert.Contains(LanguageCatalog.All, x => x.Code == "en" && x.QwenName == "English");
        Assert.Contains(LanguageCatalog.All, x => x.Code == "ja" && x.QwenName == "Japanese");
    }

    [Fact]
    public void Qwen_Names_Are_Resolvable()
    {
        Assert.Equal("Chinese", LanguageCatalog.ToQwenName("zh"));
        Assert.Equal("Traditional Chinese", LanguageCatalog.ToQwenName("zh-TW"));
    }
}
```

- [ ] **Step 2: Create catalog data**

Create `languages.json` with the official Qwen-MT 92-language table from:
`https://help.aliyun.com/zh/model-studio/machine-translation`

Use this exact schema (include all rows from the official table; at minimum the rows below):
```json
[
  { "code": "zh", "qwenName": "Chinese", "displayName": "简体中文" },
  { "code": "zh-TW", "qwenName": "Traditional Chinese", "displayName": "繁體中文" },
  { "code": "en", "qwenName": "English", "displayName": "English" },
  { "code": "ja", "qwenName": "Japanese", "displayName": "日本語" },
  { "code": "ko", "qwenName": "Korean", "displayName": "한국어" },
  { "code": "fr", "qwenName": "French", "displayName": "Français" },
  { "code": "de", "qwenName": "German", "displayName": "Deutsch" },
  { "code": "es", "qwenName": "Spanish", "displayName": "Español" },
  { "code": "ru", "qwenName": "Russian", "displayName": "Русский" },
  { "code": "pt", "qwenName": "Portuguese", "displayName": "Português" },
  { "code": "it", "qwenName": "Italian", "displayName": "Italiano" },
  { "code": "ar", "qwenName": "Arabic", "displayName": "العربية" }
]
```
Add the remaining official entries with the same keys. Do not invent names; copy the English name and code from the official “支持的语言” table.

- [ ] **Step 3: Implement catalog**

`LanguageCatalog.cs`:
```csharp
using System.Text.Json;

namespace FastTranslation.Core.Language;

public sealed record LanguageDefinition(string Code, string QwenName, string DisplayName);

public static class LanguageCatalog
{
    public static IReadOnlyList<LanguageDefinition> All { get; } = Load();

    public static string? ToQwenName(string? codeOrName)
    {
        if (string.IsNullOrWhiteSpace(codeOrName)) return null;
        var value = codeOrName.Trim();
        return All.FirstOrDefault(x =>
            string.Equals(x.Code, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.QwenName, value, StringComparison.OrdinalIgnoreCase))?.QwenName;
    }

    private static IReadOnlyList<LanguageDefinition> Load()
    {
        using var stream = typeof(LanguageCatalog).Assembly
            .GetManifestResourceStream("FastTranslation.Core.Language.languages.json")
            ?? throw new InvalidOperationException("languages.json is missing.");
        return JsonSerializer.Deserialize<List<LanguageDefinition>>(stream) ?? [];
    }
}
```

Add to `FastTranslation.Core.csproj`:
```xml
<ItemGroup>
  <EmbeddedResource Include="Language/languages.json" />
</ItemGroup>
```

- [ ] **Step 4: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: catalog tests pass; `All` contains the official entries.

- [ ] **Step 5: Checkpoint**

Report: language count, missing/invalid entries. No commit unless authorized.

---

### Task 3: Settings and DPAPI secret store

**Files:**
- Create: `src/FastTranslation.Core/Settings/SettingsModel.cs`
- Create: `src/FastTranslation.Core/Settings/SettingsStore.cs`
- Create: `src/FastTranslation.Core/Abstractions/ISettingsStore.cs`
- Create: `src/FastTranslation.Core/Abstractions/ISecretStore.cs`
- Create: `src/FastTranslation.Platform.Windows/Security/DpapiSecretStore.cs`
- Create: `src/FastTranslation.Platform.Windows/FastTranslation.Platform.Windows.csproj`
- Create: `tests/FastTranslation.Core.Tests/SettingsStoreTests.cs`
- Create: `tests/FastTranslation.Platform.Windows.Tests/DpapiSecretStoreTests.cs`

**Interfaces:**
- Produces: `SettingsModel`, `SettingsStore.Load/Save`, `ISecretStore`, `DpapiSecretStore`.

- [ ] **Step 1: Write failing settings test**

`SettingsStoreTests.cs`:
```csharp
using FastTranslation.Core.Settings;

namespace FastTranslation.Core.Tests;

public class SettingsStoreTests
{
    [Fact]
    public void Missing_File_Returns_Defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var store = new SettingsStore(path);
        var settings = store.Load();
        Assert.Equal("qwen-mt-flash", settings.Provider.Model);
        Assert.Equal(300, settings.ClipboardFetchTimeoutMs);
        Assert.True(settings.ClipboardFallback);
    }

    [Fact]
    public void Round_Trips_Unknown_Fields()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        File.WriteAllText(path, """{"schemaVersion":1,"unknown":"keep-me","provider":{"model":"qwen-mt-flash"}}""");
        var store = new SettingsStore(path);
        store.Save(store.Load());
        Assert.Contains("keep-me", File.ReadAllText(path));
    }
}
```

- [ ] **Step 2: Implement settings**

`SettingsModel.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastTranslation.Core.Settings;

public sealed class SettingsModel
{
    public int SchemaVersion { get; set; } = 1;
    public ProviderSettings Provider { get; set; } = new();
    public LanguageSettings Language { get; set; } = new();
    public string Hotkey { get; set; } = "Ctrl+Alt+T";
    public int ClipboardFetchTimeoutMs { get; set; } = 300;
    public string WriteBackMethod { get; set; } = "auto";
    public bool ClipboardFallback { get; set; } = true;
    public string[] ExcludedApps { get; set; } =
        ["KeePass.exe", "1Password.exe", "Bitwarden.exe", "LastPass.exe"];
    public string[] ClipboardFallbackExcludedApps { get; set; } =
        ["WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe"];
    public bool EnableWriteBack { get; set; } = true;
    public string[] WriteBackExcludedApps { get; set; } =
        ["WindowsTerminal.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "conhost.exe"];
    public bool StartWithWindows { get; set; }
    public string Theme { get; set; } = "system";
    public string UiLanguage { get; set; } = "zh-CN";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class ProviderSettings
{
    public string Model { get; set; } = "qwen-mt-flash";
    public string? BaseUrl { get; set; }
}

public sealed class LanguageSettings
{
    public string Mode { get; set; } = "auto";
    public string? TargetLanguage { get; set; }
}
```

`SettingsStore.cs`:
```csharp
using System.Text.Json;
using FastTranslation.Core.Abstractions;

namespace FastTranslation.Core.Settings;

public sealed class SettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _path;

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastTranslation", "settings.json");
    }

    public SettingsModel Load()
    {
        if (!File.Exists(_path)) return new SettingsModel();
        try
        {
            return JsonSerializer.Deserialize<SettingsModel>(File.ReadAllText(_path), Options)
                   ?? new SettingsModel();
        }
        catch (JsonException)
        {
            var backup = _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(_path, backup, overwrite: true);
            return new SettingsModel();
        }
    }

    public void Save(SettingsModel settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
        File.Move(temp, _path, overwrite: true);
    }
}
```

`ISettingsStore.cs`:
```csharp
using FastTranslation.Core.Settings;
namespace FastTranslation.Core.Abstractions;
public interface ISettingsStore { SettingsModel Load(); void Save(SettingsModel settings); }
```

`ISecretStore.cs`:
```csharp
namespace FastTranslation.Core.Abstractions;
public interface ISecretStore
{
    string? ReadApiKey();
    void WriteApiKey(string apiKey);
    void DeleteApiKey();
}
```

- [ ] **Step 3: Implement DPAPI store**

`DpapiSecretStore.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using FastTranslation.Core.Abstractions;

namespace FastTranslation.Platform.Windows.Security;

public sealed class DpapiSecretStore : ISecretStore
{
    private readonly string _path;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FastTranslation.v1");

    public DpapiSecretStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FastTranslation", "secret.dat");
    }

    public string? ReadApiKey()
    {
        if (!File.Exists(_path)) return null;
        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(_path), Entropy, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    public void WriteApiKey(string apiKey)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(apiKey), Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(_path, bytes);
    }

    public void DeleteApiKey()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }
}
```

Create `FastTranslation.Platform.Windows.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\FastTranslation.Core\FastTranslation.Core.csproj" />
    <PackageReference Include="System.Security.Cryptography.ProtectedData" Version="10.0.0" />
  </ItemGroup>
</Project>
```

Create Windows tests project:
```bash
dotnet new xunit -n FastTranslation.Platform.Windows.Tests -o tests/FastTranslation.Platform.Windows.Tests -f net10.0
dotnet add tests/FastTranslation.Platform.Windows.Tests reference src/FastTranslation.Platform.Windows
```
Set the test project to Windows-aware:
```bash
# edit tests/FastTranslation.Platform.Windows.Tests/FastTranslation.Platform.Windows.Tests.csproj
```
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.*" />
    <PackageReference Include="xunit" Version="2.*" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.*" />
    <ProjectReference Include="..\..\src\FastTranslation.Platform.Windows\FastTranslation.Platform.Windows.csproj" />
  </ItemGroup>
</Project>
```

`DpapiSecretStoreTests.cs`:
```csharp
using FastTranslation.Platform.Windows.Security;

namespace FastTranslation.Platform.Windows.Tests;

public class DpapiSecretStoreTests
{
    [Fact]
    public void Round_Trips_Key()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".dat");
        var store = new DpapiSecretStore(path);
        store.WriteApiKey("sk-test");
        Assert.Equal("sk-test", store.ReadApiKey());
        store.DeleteApiKey();
        Assert.Null(store.ReadApiKey());
    }
}
```

- [ ] **Step 4: Run tests**

Run:
```bash
dotnet test FastTranslation.sln -v minimal
```
Expected: settings/DPAPI tests pass.

- [ ] **Step 5: Checkpoint**

Report: settings defaults, unknown-field preservation, DPAPI round-trip. No commit unless authorized.

---

### Task 4: Qwen-MT provider

**Files:**
- Create: `src/FastTranslation.Core/Translation/TranslationRequest.cs`
- Create: `src/FastTranslation.Core/Translation/TranslationResult.cs`
- Create: `src/FastTranslation.Core/Translation/TranslationError.cs`
- Create: `src/FastTranslation.Core/Translation/TranslationException.cs`
- Create: `src/FastTranslation.Core/Abstractions/ITranslationProvider.cs`
- Create: `src/FastTranslation.Providers.QwenMt/FastTranslation.Providers.QwenMt.csproj`
- Create: `src/FastTranslation.Providers.QwenMt/QwenMtContracts.cs`
- Create: `src/FastTranslation.Providers.QwenMt/QwenMtProvider.cs`
- Create: `tests/FastTranslation.Providers.QwenMt.Tests/QwenMtProviderTests.cs`

**Interfaces:**
- Produces: `TranslationRequest(string Text,string SourceLanguage,string TargetLanguage,string Model)`, `TranslationResult(...)`, `TranslationError`, `ITranslationProvider.TranslateAsync`.

- [ ] **Step 1: Write failing provider test**

`QwenMtProviderTests.cs`:
```csharp
using System.Net;
using System.Text;
using FastTranslation.Core.Translation;
using FastTranslation.Providers.QwenMt;

namespace FastTranslation.Providers.QwenMt.Tests;

public class QwenMtProviderTests
{
    private sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }

    [Fact]
    public async Task Sends_Translation_Options_And_Parses_Content()
    {
        const string json = """{"choices":[{"message":{"role":"assistant","content":"你好"}}],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}""";
        var handler = new Handler(json);
        var provider = new QwenMtProvider(new HttpClient(handler), () => "sk-test", "https://example.test/compatible-mode/v1");

        var result = await provider.TranslateAsync(
            new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"), default);

        Assert.Equal("你好", result.Text);
        Assert.Equal("Bearer sk-test", handler.Authorization);
        Assert.Contains("\"translation_options\"", handler.Body);
        Assert.Contains("\"source_lang\":\"auto\"", handler.Body);
        Assert.Contains("\"target_lang\":\"Chinese\"", handler.Body);
        Assert.Contains("\"model\":\"qwen-mt-flash\"", handler.Body);
    }

    [Fact]
    public async Task Maps_401_To_InvalidKey()
    {
        var provider = new QwenMtProvider(
            new HttpClient(new Handler("""{"error":{"message":"bad key"}}""", HttpStatusCode.Unauthorized)),
            () => "sk-test", "https://example.test/compatible-mode/v1");

        var ex = await Assert.ThrowsAsync<TranslationException>(() =>
            provider.TranslateAsync(new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"), default));
        Assert.Equal(TranslationError.InvalidKey, ex.Code);
    }
}
```

- [ ] **Step 2: Implement contracts and provider**

`TranslationRequest.cs`:
```csharp
namespace FastTranslation.Core.Translation;
public sealed record TranslationRequest(string Text, string SourceLanguage, string TargetLanguage, string Model);
```
`TranslationResult.cs`:
```csharp
namespace FastTranslation.Core.Translation;
public sealed record TranslationResult(string Text, int? InputTokens = null, int? OutputTokens = null);
```
`TranslationError.cs`:
```csharp
namespace FastTranslation.Core.Translation;
public enum TranslationError { InvalidKey, RateLimited, InvalidRequest, Server, Network, Timeout, Empty, TooLong, SameLanguage, Unknown }
```
`TranslationException.cs`:
```csharp
namespace FastTranslation.Core.Translation;
public sealed class TranslationException(TranslationError code, string message) : Exception(message)
{
    public TranslationError Code { get; } = code;
}
```
`ITranslationProvider.cs`:
```csharp
using FastTranslation.Core.Translation;
namespace FastTranslation.Core.Abstractions;
public interface ITranslationProvider
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct);
}
```

`QwenMtContracts.cs`:
```csharp
using System.Text.Json.Serialization;

namespace FastTranslation.Providers.QwenMt;

internal sealed record QwenMtRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] QwenMtMessage[] Messages,
    [property: JsonPropertyName("translation_options")] QwenMtOptions TranslationOptions,
    [property: JsonPropertyName("stream")] bool Stream = false);

internal sealed record QwenMtMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

internal sealed record QwenMtOptions(
    [property: JsonPropertyName("source_lang")] string SourceLang,
    [property: JsonPropertyName("target_lang")] string TargetLang);

internal sealed record QwenMtResponse(
    [property: JsonPropertyName("choices")] QwenMtChoice[] Choices,
    [property: JsonPropertyName("usage")] QwenMtUsage? Usage);

internal sealed record QwenMtChoice(
    [property: JsonPropertyName("message")] QwenMtMessage Message);

internal sealed record QwenMtUsage(
    [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
    [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
    [property: JsonPropertyName("total_tokens")] int TotalTokens);
```

`QwenMtProvider.cs`:
```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Translation;

namespace FastTranslation.Providers.QwenMt;

public sealed class QwenMtProvider : ITranslationProvider
{
    private readonly HttpClient _http;
    private readonly Func<string?> _apiKey;
    private readonly string _baseUrl;

    public QwenMtProvider(HttpClient http, Func<string?> apiKey, string? baseUrl = null)
    {
        _http = http;
        _apiKey = apiKey;
        _baseUrl = (baseUrl ?? "https://dashscope.aliyuncs.com/compatible-mode/v1").TrimEnd('/');
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        var key = _apiKey();
        if (string.IsNullOrWhiteSpace(key))
            throw new TranslationException(TranslationError.InvalidKey, "API key is missing.");

        using var message = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        message.Content = JsonContent.Create(new QwenMtRequest(
            request.Model,
            [new QwenMtMessage("user", request.Text)],
            new QwenMtOptions(request.SourceLanguage, request.TargetLanguage)));

        using var response = await _http.SendAsync(message, ct);
        if (!response.IsSuccessStatusCode)
            throw MapError(response.StatusCode);

        var payload = await response.Content.ReadFromJsonAsync<QwenMtResponse>(cancellationToken: ct);
        var text = payload?.Choices.FirstOrDefault()?.Message.Content;
        if (string.IsNullOrWhiteSpace(text))
            throw new TranslationException(TranslationError.Empty, "Provider returned empty content.");

        return new TranslationResult(text, payload?.Usage?.PromptTokens, payload?.Usage?.CompletionTokens);
    }

    private static TranslationException MapError(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden =>
            new(TranslationError.InvalidKey, "API key is invalid or lacks permission."),
        HttpStatusCode.TooManyRequests =>
            new(TranslationError.RateLimited, "Rate limited or quota exhausted."),
        HttpStatusCode.BadRequest =>
            new(TranslationError.InvalidRequest, "Request is invalid."),
        >= HttpStatusCode.InternalServerError =>
            new(TranslationError.Server, "Provider is temporarily unavailable."),
        _ => new(TranslationError.Unknown, $"Provider returned {(int)status}.")
    };
}
```

- [ ] **Step 3: Create provider test project**

Run:
```bash
dotnet new xunit -n FastTranslation.Providers.QwenMt.Tests -o tests/FastTranslation.Providers.QwenMt.Tests -f net10.0
dotnet sln FastTranslation.sln add src/FastTranslation.Providers.QwenMt/FastTranslation.Providers.QwenMt.csproj tests/FastTranslation.Providers.QwenMt.Tests/FastTranslation.Providers.QwenMt.Tests.csproj
dotnet add tests/FastTranslation.Providers.QwenMt.Tests reference src/FastTranslation.Providers.QwenMt
```

Create `FastTranslation.Providers.QwenMt.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\FastTranslation.Core\FastTranslation.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 4: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Providers.QwenMt.Tests/FastTranslation.Providers.QwenMt.Tests.csproj -v minimal
```
Expected: provider tests pass.

- [ ] **Step 5: Checkpoint**

Report: request JSON shape, error mapping. No commit unless authorized.

---

### Task 5: Translation service, cache, cancellation

**Files:**
- Create: `src/FastTranslation.Core/Translation/LruCache.cs`
- Create: `src/FastTranslation.Core/Translation/TranslationService.cs`
- Create: `tests/FastTranslation.Core.Tests/LruCacheTests.cs`
- Create: `tests/FastTranslation.Core.Tests/TranslationServiceTests.cs`

**Interfaces:**
- Produces: `LruCache<TKey,TValue>`, `TranslationService.TranslateAsync(TranslationRequest, CancellationToken)`.

- [ ] **Step 1: Write failing tests**

`LruCacheTests.cs`:
```csharp
using FastTranslation.Core.Translation;

namespace FastTranslation.Core.Tests;

public class LruCacheTests
{
    [Fact]
    public void Evicts_Least_Recently_Used()
    {
        var cache = new LruCache<string, int>(2);
        cache.Set("a", 1);
        cache.Set("b", 2);
        Assert.True(cache.TryGet("a", out _));
        cache.Set("c", 3);
        Assert.False(cache.TryGet("b", out _));
        Assert.True(cache.TryGet("a", out _));
        Assert.True(cache.TryGet("c", out _));
    }
}
```

`TranslationServiceTests.cs`:
```csharp
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Translation;

namespace FastTranslation.Core.Tests;

public class TranslationServiceTests
{
    private sealed class FakeProvider : ITranslationProvider
    {
        public int Calls { get; private set; }
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new TranslationResult("译文"));
        }
    }

    [Fact]
    public async Task Caches_Repeated_Requests()
    {
        var provider = new FakeProvider();
        var service = new TranslationService(provider, TimeSpan.FromSeconds(2));
        var request = new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash");

        await service.TranslateAsync(request, default);
        await service.TranslateAsync(request, default);

        Assert.Equal(1, provider.Calls);
    }
}
```

- [ ] **Step 2: Implement LRU and service**

`LruCache.cs`:
```csharp
namespace FastTranslation.Core.Translation;

public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    private readonly int _capacity;
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = [];
    private readonly LinkedList<(TKey Key, TValue Value)> _list = [];

    public LruCache(int capacity) => _capacity = Math.Max(1, capacity);

    public bool TryGet(TKey key, out TValue value)
    {
        if (_map.TryGetValue(key, out var node))
        {
            _list.Remove(node);
            _list.AddFirst(node);
            value = node.Value.Value;
            return true;
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value)
    {
        if (_map.TryGetValue(key, out var existing))
        {
            _list.Remove(existing);
            _map.Remove(key);
        }
        var node = _list.AddFirst((key, value));
        _map[key] = node;
        if (_map.Count <= _capacity) return;
        var last = _list.Last!;
        _list.RemoveLast();
        _map.Remove(last.Value.Key);
    }
}
```

`TranslationService.cs`:
```csharp
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Translation;

namespace FastTranslation.Core.Translation;

public sealed class TranslationService
{
    private readonly ITranslationProvider _provider;
    private readonly LruCache<string, TranslationResult> _cache = new(200);
    private readonly TimeSpan _timeout;

    public TranslationService(ITranslationProvider provider, TimeSpan? timeout = null)
    {
        _provider = provider;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
    {
        var key = BuildKey(request);
        if (_cache.TryGet(key, out var cached)) return cached;

        for (var attempt = 0; ; attempt++)
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_timeout);
            try
            {
                var result = await _provider.TranslateAsync(request, timeoutCts.Token);
                _cache.Set(key, result);
                return result;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TranslationException(TranslationError.Timeout, "Translation timed out.");
            }
            catch (HttpRequestException) when (attempt == 0)
            {
                await Task.Delay(300, ct);
            }
            catch (TranslationException ex) when (ex.Code == TranslationError.Server && attempt == 0)
            {
                await Task.Delay(300, ct);
            }
        }
    }

    private static string BuildKey(TranslationRequest request)
    {
        var raw = $"{request.Model}\n{request.SourceLanguage}\n{request.TargetLanguage}\n{request.Text}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
    }
}
```

- [ ] **Step 3: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: cache/service tests pass. Add a timeout test using a provider that delays 5s and a 20ms service timeout; expected `TranslationError.Timeout`.

- [ ] **Step 4: Checkpoint**

Report: cache behavior, timeout/retry behavior. No commit unless authorized.

---

### Task 6: Selection coordinator

**Files:**
- Create: `src/FastTranslation.Core/Abstractions/ISelectionProvider.cs`
- Create: `src/FastTranslation.Core/Abstractions/IOverlayPresenter.cs`
- Create: `src/FastTranslation.Core/Abstractions/IWriteBackService.cs`
- Create: `src/FastTranslation.Core/Selection/SelectionCoordinator.cs`
- Create: `src/FastTranslation.Core/Translation/TranslationView.cs`
- Create: `src/FastTranslation.Core/WriteBack/WriteBackStatus.cs`
- Create: `src/FastTranslation.Core/WriteBack/WriteBackResult.cs`
- Create: `tests/FastTranslation.Core.Tests/SelectionCoordinatorTests.cs`

**Interfaces:**
- Consumes: `ISelectionProvider`, `IOverlayPresenter`, `ITranslationProvider`, `ILanguagePolicy`, `IWriteBackService`.
- Produces: `SelectionCoordinator.OnGestureAsync`, `OnButtonClickedAsync`, `OnHotkeyAsync`, `ReplaceAsync`, `InsertAsync`, `Dismiss`; `TranslationView`.

- [ ] **Step 1: Define interfaces and view model**

`ISelectionProvider.cs`:
```csharp
using FastTranslation.Core.Selection;
namespace FastTranslation.Core.Abstractions;
public interface ISelectionProvider
{
    Task<SelectionSnapshot?> ReadAtPointAsync(ScreenPoint point, bool allowClipboard, CancellationToken ct);
    Task<SelectionSnapshot?> ReadFromFocusAsync(bool allowClipboard, CancellationToken ct);
}
```

`IOverlayPresenter.cs`:
```csharp
using FastTranslation.Core.Selection;
using FastTranslation.Core.Translation;
namespace FastTranslation.Core.Abstractions;
public interface IOverlayPresenter
{
    void ShowButton(ScreenPoint anchor);
    void ShowLoading(ScreenPoint anchor);
    void ShowResult(TranslationView view);
    void ShowError(TranslationError error, ScreenPoint anchor);
    void ShowStatus(string message);
    void HideAll();
}
```

`IWriteBackService.cs`:
```csharp
using FastTranslation.Core.Selection;
using FastTranslation.Core.WriteBack;
namespace FastTranslation.Core.Abstractions;
public interface IWriteBackService
{
    bool CanWriteBack(SelectionSnapshot snapshot);
    Task<WriteBackResult> ReplaceAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct);
    Task<WriteBackResult> InsertAfterAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct);
}
```

`WriteBackStatus.cs`:
```csharp
namespace FastTranslation.Core.WriteBack;
public enum WriteBackStatus
{
    Success, Unsupported, SourceChanged, SelectionChanged,
    ClipboardBusy, ClipboardNonText, Unverified, Failed
}
```
`WriteBackResult.cs`:
```csharp
namespace FastTranslation.Core.WriteBack;
public sealed record WriteBackResult(WriteBackStatus Status, string? Message = null);
```
`TranslationView.cs`:
```csharp
using FastTranslation.Core.Selection;
using FastTranslation.Core.Translation;
namespace FastTranslation.Core.Translation;
public sealed record TranslationView(
    SelectionSnapshot Snapshot,
    TranslationResult Result,
    string TargetLanguage,
    bool CanWriteBack);
```

- [ ] **Step 2: Write failing coordinator tests**

`SelectionCoordinatorTests.cs`:
```csharp
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Language;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Settings;
using FastTranslation.Core.Translation;
using FastTranslation.Core.WriteBack;

namespace FastTranslation.Core.Tests;

public class SelectionCoordinatorTests
{
    private sealed class FakeSelection : ISelectionProvider
    {
        public SelectionSnapshot? Snapshot { get; set; }
        public int PointReads { get; private set; }
        public Task<SelectionSnapshot?> ReadAtPointAsync(ScreenPoint point, bool allowClipboard, CancellationToken ct)
        { PointReads++; return Task.FromResult(Snapshot); }
        public Task<SelectionSnapshot?> ReadFromFocusAsync(bool allowClipboard, CancellationToken ct)
            => Task.FromResult(Snapshot);
    }

    private sealed class FakeOverlay : IOverlayPresenter
    {
        public string Last { get; private set; } = "";
        public void ShowButton(ScreenPoint anchor) => Last = "button";
        public void ShowLoading(ScreenPoint anchor) => Last = "loading";
        public void ShowResult(TranslationView view) => Last = "result";
        public void ShowError(TranslationError error, ScreenPoint anchor) => Last = $"error:{error}";
        public void ShowStatus(string message) => Last = "status";
        public void HideAll() => Last = "hidden";
    }

    private sealed class FakeProvider : ITranslationProvider
    {
        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct)
            => Task.FromResult(new TranslationResult("译文"));
    }

    private sealed class FakeWriteBack : IWriteBackService
    {
        public bool CanWriteBack(SelectionSnapshot snapshot) => true;
        public Task<WriteBackResult> ReplaceAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct)
            => Task.FromResult(new WriteBackResult(WriteBackStatus.Success));
        public Task<WriteBackResult> InsertAfterAsync(SelectionSnapshot snapshot, string translation, CancellationToken ct)
            => Task.FromResult(new WriteBackResult(WriteBackStatus.Success));
    }

    private static SelectionSnapshot Snapshot() => new(
        "hello", [new ScreenRect(0, 0, 10, 10)], "test", false,
        SelectionSource.UiaPoint, 1, "runtime-1", true);

    [Fact]
    public async Task Gesture_Shows_Button_And_Click_Translates()
    {
        var selection = new FakeSelection { Snapshot = Snapshot() };
        var overlay = new FakeOverlay();
        var coordinator = new SelectionCoordinator(
            selection, overlay, new TranslationService(new FakeProvider()),
            new LanguagePolicy(), new FakeWriteBack(), new SettingsModel(), () => "sk-test");

        await coordinator.OnGestureAsync(new ScreenPoint(10, 10), default);
        Assert.Equal("button", overlay.Last);

        await coordinator.OnButtonClickedAsync(default);
        Assert.Equal("result", overlay.Last);
    }

    [Fact]
    public async Task Missing_Snapshot_Is_Captured_On_Click()
    {
        var selection = new FakeSelection { Snapshot = Snapshot() };
        var overlay = new FakeOverlay();
        var coordinator = new SelectionCoordinator(
            selection, overlay, new TranslationService(new FakeProvider()),
            new LanguagePolicy(), new FakeWriteBack(), new SettingsModel(), () => "sk-test",
            captureOnClickOnly: true);

        await coordinator.OnGestureAsync(new ScreenPoint(10, 10), default);
        Assert.Equal("button", overlay.Last);
        await coordinator.OnButtonClickedAsync(default);
        Assert.Equal("result", overlay.Last);
        Assert.Equal(1, selection.PointReads);
    }
}
```

- [ ] **Step 3: Implement coordinator**

`SelectionCoordinator.cs`:
```csharp
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Language;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Settings;
using FastTranslation.Core.Translation;
using FastTranslation.Core.WriteBack;

namespace FastTranslation.Core.Selection;

public sealed class SelectionCoordinator
{
    private readonly ISelectionProvider _selection;
    private readonly IOverlayPresenter _overlay;
    private readonly TranslationService _translation;
    private readonly ILanguagePolicy _policy;
    private readonly IWriteBackService _writeBack;
    private readonly SettingsModel _settings;
    private readonly Func<string?> _apiKey;
    private readonly Action<string> _copyToClipboard;
    private readonly bool _captureOnClickOnly;

    private int _selectionId;
    private SelectionSnapshot? _snapshot;
    private ScreenPoint _anchor;
    private TranslationResult? _result;
    private string? _targetLanguage;
    private CancellationTokenSource? _cts;

    public SelectionCoordinator(
        ISelectionProvider selection,
        IOverlayPresenter overlay,
        TranslationService translation,
        ILanguagePolicy policy,
        IWriteBackService writeBack,
        SettingsModel settings,
        Func<string?> apiKey,
        Action<string>? copyToClipboard = null,
        bool captureOnClickOnly = false)
    {
        _selection = selection;
        _overlay = overlay;
        _translation = translation;
        _policy = policy;
        _writeBack = writeBack;
        _settings = settings;
        _apiKey = apiKey;
        _copyToClipboard = copyToClipboard ?? (_ => { });
        _captureOnClickOnly = captureOnClickOnly;
    }

    public async Task OnGestureAsync(ScreenPoint anchor, CancellationToken ct)
    {
        var id = ++_selectionId;
        CancelCurrent();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _snapshot = null;
        _result = null;
        _anchor = anchor;
        _overlay.HideAll();
        _overlay.ShowButton(anchor);

        if (_captureOnClickOnly) return;

        var snapshot = await _selection.ReadAtPointAsync(anchor, allowClipboard: false, _cts.Token);
        if (id != _selectionId) return;
        if (snapshot is not null && TextNormalizer.IsTranslatable(snapshot.Text))
            _snapshot = snapshot;
    }

    public async Task OnButtonClickedAsync(CancellationToken ct)
    {
        var id = _selectionId;
        _overlay.HideAll();
        _overlay.ShowLoading(_anchor);

        if (_snapshot is null)
        {
            _snapshot = await _selection.ReadAtPointAsync(_anchor, allowClipboard: true, ct);
            if (_snapshot is null || !TextNormalizer.IsTranslatable(_snapshot.Text))
            {
                _overlay.ShowError(TranslationError.Empty, _anchor);
                return;
            }
        }

        if (id != _selectionId) return;
        await TranslateAsync(_snapshot, _anchor, ct);
    }

    public async Task OnHotkeyAsync(CancellationToken ct)
    {
        var id = ++_selectionId;
        CancelCurrent();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var snapshot = await _selection.ReadFromFocusAsync(allowClipboard: true, _cts.Token);
        if (id != _selectionId) return;
        if (snapshot is null || !TextNormalizer.IsTranslatable(snapshot.Text))
        {
            _overlay.ShowStatus("未识别到选中文本");
            return;
        }
        _snapshot = snapshot;
        _anchor = snapshot.BoundingRects.LastOrDefault();
        await TranslateAsync(snapshot, _anchor, ct);
    }

    private async Task TranslateAsync(
        SelectionSnapshot snapshot, ScreenPoint anchor, CancellationToken ct, string? targetOverride = null)
    {
        var text = TextNormalizer.Normalize(snapshot.Text);
        if (text.Length > 5000)
        {
            _overlay.ShowError(TranslationError.TooLong, anchor);
            return;
        }

        var target = targetOverride ?? _policy.ResolveTargetLanguage(text, _settings.Language.TargetLanguage);
        var request = new TranslationRequest(text, "auto", target, _settings.Provider.Model);
        _overlay.ShowLoading(anchor);
        try
        {
            var result = await _translation.TranslateAsync(request, ct);
            _result = result;
            _targetLanguage = target;
            _overlay.ShowResult(new TranslationView(
                snapshot, result, target, _writeBack.CanWriteBack(snapshot)));
        }
        catch (TranslationException ex)
        {
            _overlay.ShowError(ex.Code, anchor);
        }
    }

    public async Task ChangeTargetAsync(string target, CancellationToken ct)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(target)) return;
        _settings.Language.TargetLanguage = target;
        await TranslateAsync(_snapshot, _anchor, ct, target);
    }

    public async Task ReplaceAsync(string translation, CancellationToken ct)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(translation)) return;
        var result = await _writeBack.ReplaceAsync(_snapshot, translation, ct);
        if (result.Status == WriteBackStatus.Success)
        {
            _overlay.ShowStatus("已替换（可用 Ctrl+Z 撤销）");
            return;
        }
        _copyToClipboard(translation);
        _overlay.ShowStatus(result.Message ?? "替换失败，译文已复制");
    }

    public async Task InsertAsync(string translation, CancellationToken ct)
    {
        if (_snapshot is null || string.IsNullOrWhiteSpace(translation)) return;
        var result = await _writeBack.InsertAfterAsync(_snapshot, translation, ct);
        if (result.Status == WriteBackStatus.Success)
        {
            _overlay.ShowStatus("已插入（可用 Ctrl+Z 撤销）");
            return;
        }
        _copyToClipboard(translation);
        _overlay.ShowStatus(result.Message ?? "插入失败，译文已复制");
    }

    public void Dismiss()
    {
        ++_selectionId;
        CancelCurrent();
        _overlay.HideAll();
    }

    private void CancelCurrent()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }
}
```

- [ ] **Step 4: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Core.Tests/FastTranslation.Core.Tests.csproj -v minimal
```
Expected: coordinator tests pass.

- [ ] **Step 5: Checkpoint**

Report: coordinator state transitions, pending capture behavior. No commit unless authorized.

---

## Phase 2 — Platform Windows

### Task 7: Mouse gesture detector

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Input/MouseGestureDetector.cs`
- Create: `tests/FastTranslation.Platform.Windows.Tests/MouseGestureDetectorTests.cs`

**Interfaces:**
- Produces: `MouseGestureDetector` with `OnLeftButtonDown`, `OnMouseMove`, `TryComplete`.

- [ ] **Step 1: Write failing tests**

`MouseGestureDetectorTests.cs`:
```csharp
using FastTranslation.Platform.Windows.Input;
using FastTranslation.Core.Selection;

namespace FastTranslation.Platform.Windows.Tests;

public class MouseGestureDetectorTests
{
    [Fact]
    public void Drag_Over_System_Threshold_Completes()
    {
        var d = new MouseGestureDetector(4, 4, 4, 4, 500);
        d.OnLeftButtonDown(new ScreenPoint(10, 10), Environment.TickCount, true);
        d.OnMouseMove(new ScreenPoint(30, 10), true);
        Assert.True(d.TryComplete(new ScreenPoint(30, 10), true, out _));
    }

    [Fact]
    public void Tiny_Click_Does_Not_Complete()
    {
        var d = new MouseGestureDetector(4, 4, 4, 4, 500);
        d.OnLeftButtonDown(new ScreenPoint(10, 10), Environment.TickCount, true);
        Assert.False(d.TryComplete(new ScreenPoint(10, 10), true, out _));
    }
}
```

- [ ] **Step 2: Implement**

`MouseGestureDetector.cs`:
```csharp
using FastTranslation.Core.Selection;

namespace FastTranslation.Platform.Windows.Input;

public sealed class MouseGestureDetector(
    int dragWidth, int dragHeight, int doubleClickWidth, int doubleClickHeight, int doubleClickTimeMs)
{
    private ScreenPoint _down;
    private int _downTime;
    private bool _tracking;
    private bool _ibeam;
    private int _clickCount;
    private int _lastClickTime;
    private ScreenPoint _lastClickPoint;

    public bool IsTracking => _tracking;

    public void OnLeftButtonDown(ScreenPoint point, int timeMs, bool isIBeam)
    {
        _down = point;
        _downTime = timeMs;
        _tracking = true;
        _ibeam = isIBeam;
    }

    public void OnMouseMove(ScreenPoint point, bool isIBeam) => _ibeam |= isIBeam;

    public bool TryComplete(ScreenPoint point, bool isIBeam, out ScreenPoint completedPoint)
    {
        completedPoint = point;
        if (!_tracking) return false;
        _tracking = false;
        if (!isIBeam) return false;

        var dx = Math.Abs(point.X - _down.X);
        var dy = Math.Abs(point.Y - _down.Y);
        var isDrag = dx > dragWidth || dy > dragHeight;

        var nearLast = Math.Abs(point.X - _lastClickPoint.X) <= doubleClickWidth &&
                       Math.Abs(point.Y - _lastClickPoint.Y) <= doubleClickHeight;
        var withinTime = Math.Abs(Environment.TickCount - _lastClickTime) <= doubleClickTimeMs;
        _clickCount = nearLast && withinTime ? _clickCount + 1 : 1;
        _lastClickTime = Environment.TickCount;
        _lastClickPoint = point;

        return isDrag || _clickCount >= 2;
    }
}
```

- [ ] **Step 3: Run tests**

Run:
```bash
dotnet test tests/FastTranslation.Platform.Windows.Tests/FastTranslation.Platform.Windows.Tests.csproj -v minimal
```
Expected: gesture tests pass.

- [ ] **Step 4: Checkpoint**

Report: thresholds/click behavior. No commit unless authorized.

---

### Task 8: Global mouse monitor

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Input/GlobalMouseMonitor.cs`
- Create: `src/FastTranslation.Platform.Windows/Input/MouseSelectionCompletedEventArgs.cs`

**Interfaces:**
- Produces: events `SelectionStarted(ScreenPoint)`, `SelectionCompleted(MouseSelectionCompletedEventArgs)`; `Start()/Stop()`.

- [ ] **Step 1: Implement dedicated-thread hook**

`MouseSelectionCompletedEventArgs.cs`:
```csharp
using FastTranslation.Core.Selection;
namespace FastTranslation.Platform.Windows.Input;
public sealed class MouseSelectionCompletedEventArgs(ScreenPoint screenPoint) : EventArgs
{
    public ScreenPoint ScreenPoint { get; } = screenPoint;
}
```

`GlobalMouseMonitor.cs`:
```csharp
using System.Runtime.InteropServices;
using FastTranslation.Core.Selection;

namespace FastTranslation.Platform.Windows.Input;

public sealed class GlobalMouseMonitor : IDisposable
{
    private const int WH_MOUSE_LL = 14;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_QUIT = 0x0012;
    private const int IDC_IBEAM = 32513;
    private const int SM_CXDRAG = 68;
    private const int SM_CYDRAG = 69;
    private const int SM_CXDOUBLECLK = 36;
    private const int SM_CYDOUBLECLK = 37;

    private readonly object _gate = new();
    private Task _dispatch = Task.CompletedTask;
    private Thread? _thread;
    private uint _threadId;
    private IntPtr _hook;
    private LowLevelMouseProc? _proc;
    private MouseGestureDetector? _detector;
    private IntPtr _ibeam;

    public event EventHandler<ScreenPoint>? SelectionStarted;
    public event EventHandler<MouseSelectionCompletedEventArgs>? SelectionCompleted;
    public bool IsRunning => _thread is { IsAlive: true } && _hook != IntPtr.Zero;

    public bool Start()
    {
        if (IsRunning) return true;
        var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "FastTranslation.MouseHook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return ready.Wait(2000) && IsRunning;
    }

    private void Run(ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        _detector = new MouseGestureDetector(
            GetSystemMetrics(SM_CXDRAG), GetSystemMetrics(SM_CYDRAG),
            GetSystemMetrics(SM_CXDOUBLECLK), GetSystemMetrics(SM_CYDOUBLECLK),
            GetDoubleClickTime());
        _ibeam = LoadCursor(IntPtr.Zero, IDC_IBEAM);
        _proc = HookProc;
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        ready.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _detector is not null)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            var point = new ScreenPoint(data.pt.X, data.pt.Y);
            var isIBeam = IsIBeam();
            var message = wParam.ToInt32();
            if (message == WM_LBUTTONDOWN)
            {
                _detector.OnLeftButtonDown(point, (int)data.time, isIBeam);
                QueueEvent(() => SelectionStarted?.Invoke(this, point));
            }
            else if (message == WM_MOUSEMOVE && _detector.IsTracking)
            {
                _detector.OnMouseMove(point, isIBeam);
            }
            else if (message == WM_LBUTTONUP &&
                     _detector.TryComplete(point, isIBeam, out var completed))
            {
                QueueEvent(() => SelectionCompleted?.Invoke(
                    this, new MouseSelectionCompletedEventArgs(completed)));
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    private void QueueEvent(Action action)
    {
        lock (_gate)
            _dispatch = _dispatch.ContinueWith(_ => action(), TaskScheduler.Default);
    }

    private bool IsIBeam()
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref info) && info.hCursor == _ibeam;
    }

    public void Stop()
    {
        if (_thread is null) return;
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(2000);
        _thread = null;
        _threadId = 0;
    }

    public void Dispose() => Stop();

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    [DllImport("user32.dll")]
    private static extern int GetDoubleClickTime();
    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);
    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);
    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);
    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
```

- [ ] **Step 2: Manual integration test**

Temporarily wire the monitor into the probe or a console harness:
- Drag-select in Chrome, Word, Notepad.
- Verify `SelectionCompleted` fires once per gesture, not on clicks, not on non-I-beam drags.
- Verify the hook thread exits cleanly.

- [ ] **Step 3: Checkpoint**

Report: event counts per gesture, hook thread lifecycle. No commit unless authorized.

---

### Task 9: Clipboard service and fallback

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Clipboard/ClipboardService.cs`
- Create: `src/FastTranslation.Platform.Windows/Clipboard/ClipboardFallback.cs`

**Interfaces:**
- Produces: `ClipboardService.GetText()`, `SetText(string)`, `GetSequenceNumber()`, `TryBackupText(out string?)`, `RestoreText(string?)`; `ClipboardFallback.TryRead(nint sourceWindow, int timeoutMs, CancellationToken)`.

- [ ] **Step 1: Implement clipboard access**

`ClipboardService.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Text;

namespace FastTranslation.Platform.Windows.Clipboard;

public sealed class ClipboardService
{
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_TEXT = 1;
    private const uint CF_OEMTEXT = 7;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint hWndNewOwner);
    [DllImport("user32.dll")] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern nint GetClipboardData(uint uFormat);
    [DllImport("user32.dll")] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] private static extern uint EnumClipboardFormats(uint format);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll")] private static extern nint GlobalLock(nint hMem);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(nint hMem);
    [DllImport("user32.dll")] private static extern bool EmptyClipboard();
    [DllImport("user32.dll")] private static extern nint SetClipboardData(uint uFormat, nint hMem);
    [DllImport("kernel32.dll")] private static extern nint GlobalAlloc(uint uFlags, nuint dwBytes);
    [DllImport("kernel32.dll")] private static extern nint GlobalFree(nint hMem);
    private const uint GMEM_MOVEABLE = 0x0002;

    public uint GetSequenceNumber() => GetClipboardSequenceNumber();

    public bool HasNonTextFormat()
    {
        if (!TryOpen()) return true;
        try
        {
            uint format = 0;
            while ((format = EnumClipboardFormats(format)) != 0)
            {
                if (format != CF_UNICODETEXT && format != CF_TEXT && format != CF_OEMTEXT)
                    return true;
            }
            return false;
        }
        finally { CloseClipboard(); }
    }

    public string? GetText()
    {
        if (!TryOpen()) return null;
        try
        {
            if (IsClipboardFormatAvailable(CF_UNICODETEXT)) return ReadUnicode();
            if (IsClipboardFormatAvailable(CF_TEXT)) return ReadAnsi(CF_TEXT);
            if (IsClipboardFormatAvailable(CF_OEMTEXT)) return ReadAnsi(CF_OEMTEXT);
            return null;
        }
        finally { CloseClipboard(); }
    }

    public void SetText(string text)
    {
        if (!TryOpen()) throw new InvalidOperationException("Clipboard is busy.");
        try
        {
            EmptyClipboard();
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes.Length);
            if (h == 0) throw new OutOfMemoryException();
            var p = GlobalLock(h);
            Marshal.Copy(bytes, 0, p, bytes.Length);
            GlobalUnlock(h);
            if (SetClipboardData(CF_UNICODETEXT, h) == 0)
            {
                GlobalFree(h);
                throw new InvalidOperationException("SetClipboardData failed.");
            }
        }
        finally { CloseClipboard(); }
    }

    private bool TryOpen()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(0)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private static string? ReadUnicode()
    {
        var h = GetClipboardData(CF_UNICODETEXT);
        if (h == 0) return null;
        var p = GlobalLock(h);
        try { return Marshal.PtrToStringUni(p); }
        finally { GlobalUnlock(h); }
    }

    private static string? ReadAnsi(uint format)
    {
        var h = GetClipboardData(format);
        if (h == 0) return null;
        var p = GlobalLock(h);
        try { return Marshal.PtrToStringAnsi(p); }
        finally { GlobalUnlock(h); }
    }
}
```

- [ ] **Step 2: Implement fallback**

`ClipboardFallback.cs`:
```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows.Clipboard;

public sealed class ClipboardFallback(ClipboardService clipboard)
{
    public async Task<string?> TryReadAsync(nint sourceWindow, int timeoutMs, CancellationToken ct)
    {
        if (sourceWindow == 0 || clipboard.HasNonTextFormat()) return null;
        var original = clipboard.GetText();
        var originalSeq = clipboard.GetSequenceNumber();

        SendCtrlC();
        var start = Environment.TickCount;
        var changed = false;
        while (Environment.TickCount - start < timeoutMs)
        {
            ct.ThrowIfCancellationRequested();
            if (clipboard.GetSequenceNumber() != originalSeq)
            {
                changed = true;
                await Task.Delay(30, ct);
                break;
            }
            await Task.Delay(10, ct);
        }

        if (!changed)
        {
            Restore(original);
            return null;
        }

        var text = clipboard.GetText();
        Restore(original);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private void Restore(string? original)
    {
        try
        {
            clipboard.SetText(original ?? string.Empty);
        }
        catch { /* metadata-only log in product */ }
    }

    [DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_MENU = 0x12;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_RWIN = 0x5C;
    private const ushort VK_C = 0x43;

    private static void SendCtrlC()
    {
        Release(VK_CONTROL);
        Release(VK_MENU);
        Release(VK_SHIFT);
        Release(VK_LWIN);
        Release(VK_RWIN);

        var inputs = new[]
        {
            Key(VK_CONTROL, false),
            Key(VK_C, false),
            Key(VK_C, true),
            Key(VK_CONTROL, true)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void Release(ushort vk) =>
        SendInput(1, [Key(vk, true)], Marshal.SizeOf<INPUT>());

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        data = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 }
        }
    };
}
```
The INPUT/KEYBDINPUT structs above are local to this file; keep them private.

- [ ] **Step 3: Manual tests**

- UIA success path: clipboard must be unchanged.
- UIA failure in Chrome textarea: Ctrl+C captures, clipboard restored.
- Clipboard with image: fallback skipped.
- Terminal process: fallback skipped by caller.
- Timeout: no stale clipboard returned.

- [ ] **Step 4: Checkpoint**

Report: clipboard restore results, timeout behavior. No commit unless authorized.

---

### Task 10: UIA selection reader

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Uia/UiaSelectionReader.cs`
- Create: `tests/FastTranslation.Platform.Windows.Tests/UiaSelectionReaderTests.cs`

**Interfaces:**
- Implements `ISelectionProvider`; produces snapshots from UIA, with clipboard fallback when allowed.

- [ ] **Step 1: Implement reader**

`UiaSelectionReader.cs`:
```csharp
using System.Diagnostics;
using System.Windows.Automation;
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Selection;
using FastTranslation.Platform.Windows.Clipboard;

namespace FastTranslation.Platform.Windows.Uia;

public sealed class UiaSelectionReader(
    ClipboardFallback clipboardFallback,
    Func<string, bool> isClipboardFallbackExcluded,
    Func<int>? timeoutMs = null) : ISelectionProvider
{
    private int TimeoutMs => Math.Clamp(timeoutMs?.Invoke() ?? 300, 50, 5000);
    public Task<SelectionSnapshot?> ReadAtPointAsync(ScreenPoint point, bool allowClipboard, CancellationToken ct)
        => Task.Run(() => ReadAtPoint(point, allowClipboard, ct), ct);

    public Task<SelectionSnapshot?> ReadFromFocusAsync(bool allowClipboard, CancellationToken ct)
        => Task.Run(() => ReadFromFocus(allowClipboard, ct), ct);

    private SelectionSnapshot? ReadAtPoint(ScreenPoint point, bool allowClipboard, CancellationToken ct)
    {
        var element = AutomationElement.FromPoint(new System.Windows.Point(point.X, point.Y));
        var snapshot = ReadFromElement(element, point, SelectionSource.UiaPoint);
        if (snapshot is not null) return snapshot;
        if (!allowClipboard) return null;
        return ReadClipboardFallback(point, ct);
    }

    private SelectionSnapshot? ReadFromFocus(bool allowClipboard, CancellationToken ct)
    {
        var element = AutomationElement.FocusedElement;
        var snapshot = ReadFromElement(element, default, SelectionSource.UiaFocus);
        if (snapshot is not null) return snapshot;
        return allowClipboard ? ReadClipboardFallback(default, ct) : null;
    }

    private static SelectionSnapshot? ReadFromElement(AutomationElement? element, ScreenPoint point, SelectionSource source)
    {
        for (var depth = 0; depth < 10 && element is not null; depth++)
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var patternObj) &&
                patternObj is TextPattern pattern)
            {
                var ranges = pattern.GetSelection();
                if (ranges.Length == 0) return null;
                var text = string.Join("\n", ranges.Select(r => r.GetText(-1)));
                var rects = ranges.SelectMany(r => r.GetBoundingRectangles())
                    .Chunk(4)
                    .Select(a => new ScreenRect((int)a[0], (int)a[1], (int)a[2], (int)a[3]))
                    .ToList();
                var isPassword = element.Current.IsPassword;
                var hwnd = (nint)element.Current.NativeWindowHandle;
                if (hwnd == 0) hwnd = GetForegroundWindow();
                var runtimeId = string.Join('-', element.GetRuntimeId());
                var isWritable = TryGetWritable(element);
                return new SelectionSnapshot(text, rects, ProcessNameOf(hwnd), isPassword, source, hwnd, runtimeId, isWritable);
            }
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return null;
    }

    private SelectionSnapshot? ReadClipboardFallback(ScreenPoint point, CancellationToken ct)
    {
        var hwnd = GetForegroundWindow();
        var process = ProcessNameOf(hwnd);
        if (isClipboardFallbackExcluded(process)) return null;
        var text = clipboardFallback.TryReadAsync(hwnd, TimeoutMs, ct).GetAwaiter().GetResult();
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new SelectionSnapshot(
            text, [], process, false, SelectionSource.ClipboardFallback, hwnd, null, false);
    }

    private static bool TryGetWritable(AutomationElement element)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var obj) || obj is not ValuePattern value)
            return false;
        return !value.Current.IsReadOnly;
    }

    private static string? ProcessNameOf(nint hwnd)
    {
        if (hwnd == 0) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        try { return Process.GetProcessById((int)pid).ProcessName + ".exe"; }
        catch { return null; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
```

- [ ] **Step 2: Add unit tests for pure helpers**

Extract `CombineRectangles(double[])` and `ParseProcessName` as internal static methods; test:
```csharp
[Fact]
public void Converts_Uia_Doubles_To_Rects()
{
    var rects = UiaSelectionReader.ConvertRectangles([1,2,3,4, 5,6,7,8]);
    Assert.Equal(new ScreenRect(1,2,3,4), rects[0]);
    Assert.Equal(new ScreenRect(5,6,7,8), rects[1]);
}
```
If the helper is private, make it `internal` and add `InternalsVisibleTo("FastTranslation.Platform.Windows.Tests")` or use a small `UiaRectConverter` class. Prefer a separate `UiaRectConverter` class for testability.

- [ ] **Step 3: Manual matrix**

- Chrome/Edge selection: text and rects.
- Word: text and rects.
- Edge PDF/Acrobat: text or fallback.
- Input/textarea/contenteditable: writable detection.
- Password field: `IsPassword` true, no button.
- 100% / 150% / 200% DPI: rect coordinates sane.

- [ ] **Step 4: Checkpoint**

Report: per-app UIA results, writable detection. No commit unless authorized.

---

### Task 11: Overlay windows and presenter

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Overlay/ButtonWindow.xaml(.cs)`
- Create: `src/FastTranslation.Platform.Windows/Overlay/ResultWindow.xaml(.cs)`
- Create: `src/FastTranslation.Platform.Windows/Overlay/OverlayPresenter.cs`
- Create: `src/FastTranslation.Platform.Windows/Overlay/NativeWindowStyles.cs`

**Interfaces:**
- Implements `IOverlayPresenter`; produces button/result windows with no-activate behavior.

- [ ] **Step 1: Button window**

`ButtonWindow.xaml`:
```xml
<Window x:Class="FastTranslation.Platform.Windows.Overlay.ButtonWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" ShowActivated="False" Topmost="True"
        ResizeMode="NoResize" SizeToContent="WidthAndHeight">
  <Border Background="{DynamicResource ButtonBackground}" CornerRadius="16"
          Padding="14,6" BorderThickness="1" BorderBrush="{DynamicResource ButtonBorder}">
    <TextBlock Text="翻译" FontSize="13" Foreground="{DynamicResource ButtonForeground}"/>
  </Border>
</Window>
```
`ButtonWindow.xaml.cs`:
```csharp
using System.Windows;
using FastTranslation.Core.Selection;

namespace FastTranslation.Platform.Windows.Overlay;

public partial class ButtonWindow : Window
{
    public event Action? Clicked;

    public ButtonWindow()
    {
        InitializeComponent();
        NativeWindowStyles.Apply(this);
        MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
    }

    public void ShowAt(ScreenPoint anchor) => NativeWindowStyles.ShowAt(this, anchor);
}
```

`NativeWindowStyles.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using FastTranslation.Core.Selection;

namespace FastTranslation.Platform.Windows.Overlay;

public static class NativeWindowStyles
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE = 3;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    public static void Apply(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var ex = GetWindowLong(handle, GWL_EXSTYLE);
            SetWindowLong(handle, GWL_EXSTYLE, ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TOPMOST);
            HwndSource.FromHwnd(handle)?.AddHook((nint _, int msg, nint _, nint _, ref bool handled) =>
            {
                if (msg != WM_MOUSEACTIVATE) return 0;
                handled = true;
                return MA_NOACTIVATE;
            });
        };
    }

    public static void ShowAt(Window window, ScreenPoint anchor)
    {
        var dpi = VisualTreeHelper.GetDpi(window);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        var monitor = MonitorFromPoint(new POINT { X = anchor.X, Y = anchor.Y }, MONITOR_DEFAULTTONEAREST);
        if (!GetMonitorInfo(monitor, ref info)) info.rcWork = new RECT { Right = 1920, Bottom = 1080 };

        var workLeft = info.rcWork.Left / dpi.DpiScaleX;
        var workTop = info.rcWork.Top / dpi.DpiScaleY;
        var workRight = info.rcWork.Right / dpi.DpiScaleX;
        var workBottom = info.rcWork.Bottom / dpi.DpiScaleY;
        var width = double.IsNaN(window.Width) ? 80 : window.Width;
        var height = double.IsNaN(window.Height) ? 32 : window.Height;

        var left = Math.Clamp(anchor.X / dpi.DpiScaleX, workLeft, Math.Max(workLeft, workRight - width));
        var top = Math.Clamp(anchor.Y / dpi.DpiScaleY + 8, workTop, Math.Max(workTop, workBottom - height));
        window.Left = left;
        window.Top = top;
        if (!window.IsVisible) window.Show();
        else window.Topmost = true;
    }
}
```

- [ ] **Step 2: Result window**

`ResultWindow.xaml`:
```xml
<Window x:Class="FastTranslation.Platform.Windows.Overlay.ResultWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Width="380" MaxHeight="600" WindowStyle="None" AllowsTransparency="True"
        Background="Transparent" ShowInTaskbar="False" ShowActivated="False" Topmost="True">
  <Border Background="{DynamicResource CardBackground}" CornerRadius="10" Padding="14"
          BorderThickness="1" BorderBrush="{DynamicResource CardBorder}">
    <Grid>
      <Grid.RowDefinitions>
        <RowDefinition Height="Auto"/>
        <RowDefinition Height="*"/>
        <RowDefinition Height="Auto"/>
      </Grid.RowDefinitions>
      <DockPanel Grid.Row="0">
        <Button x:Name="CloseButton" Content="×" DockPanel.Dock="Right" Click="CloseButton_Click"/>
        <ComboBox x:Name="TargetLanguageBox" Width="160" HorizontalAlignment="Left"/>
      </DockPanel>
      <ScrollViewer Grid.Row="1" VerticalScrollBarVisibility="Auto" Margin="0,10">
        <TextBlock x:Name="TranslationText" TextWrapping="Wrap" FontSize="14"/>
      </ScrollViewer>
      <StackPanel Grid.Row="2" Orientation="Horizontal">
        <Button x:Name="ReplaceButton" Content="替换" Click="ReplaceButton_Click"/>
        <Button x:Name="InsertButton" Content="插入" Click="InsertButton_Click"/>
        <Button x:Name="CopyButton" Content="复制" Click="CopyButton_Click"/>
        <TextBlock x:Name="StatusText" Margin="12,0,0,0" VerticalAlignment="Center"/>
      </StackPanel>
    </Grid>
  </Border>
</Window>
```
`ResultWindow.xaml.cs`:
```csharp
using System.Windows;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Translation;

namespace FastTranslation.Platform.Windows.Overlay;

public partial class ResultWindow : Window
{
    public event Action<string>? ReplaceRequested;
    public event Action<string>? InsertRequested;
    public event Action<string>? CopyRequested;
    public event Action? CloseRequested;
    public event Action<string>? TargetLanguageChanged;

    public ResultWindow()
    {
        InitializeComponent();
        NativeWindowStyles.Apply(this);
        TargetLanguageBox.SelectionChanged += (_, _) =>
        {
            if (TargetLanguageBox.SelectedItem is string value)
                TargetLanguageChanged?.Invoke(value);
        };
    }

    public void ShowLoading(ScreenPoint anchor)
    {
        TranslationText.Text = "翻译中…";
        ReplaceButton.IsEnabled = false;
        InsertButton.IsEnabled = false;
        StatusText.Text = "";
        NativeWindowStyles.ShowAt(this, anchor);
    }

    public void ShowResult(TranslationView view, ScreenPoint anchor)
    {
        TranslationText.Text = view.Result.Text;
        ReplaceButton.IsEnabled = view.CanWriteBack;
        InsertButton.IsEnabled = view.CanWriteBack;
        StatusText.Text = "";
        NativeWindowStyles.ShowAt(this, anchor);
    }

    public void ShowError(TranslationError error, ScreenPoint anchor)
    {
        TranslationText.Text = error switch
        {
            TranslationError.InvalidKey => "API Key 无效或未开通百炼。",
            TranslationError.RateLimited => "请求过多或额度不足，请稍后重试。",
            TranslationError.TooLong => "文本过长（v1.0 最多 5,000 字）。",
            TranslationError.Empty => "未识别到可翻译文本。",
            TranslationError.Network => "网络不可用或超时。",
            TranslationError.Timeout => "翻译超时，请重试。",
            _ => "翻译失败，请重试。"
        };
        ReplaceButton.IsEnabled = false;
        InsertButton.IsEnabled = false;
        NativeWindowStyles.ShowAt(this, anchor);
    }

    public void SetStatus(string message) => StatusText.Text = message;
    public void SetTargetLanguages(IEnumerable<string> languages, string selected)
    {
        TargetLanguageBox.ItemsSource = languages;
        TargetLanguageBox.SelectedItem = selected;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();
    private void ReplaceButton_Click(object sender, RoutedEventArgs e) => ReplaceRequested?.Invoke(TranslationText.Text);
    private void InsertButton_Click(object sender, RoutedEventArgs e) => InsertRequested?.Invoke(TranslationText.Text);
    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopyRequested?.Invoke(TranslationText.Text);
}
```

- [ ] **Step 3: Presenter**

`OverlayPresenter.cs`:
```csharp
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Language;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Translation;

namespace FastTranslation.Platform.Windows.Overlay;

public sealed class OverlayPresenter : IOverlayPresenter
{
    private readonly ButtonWindow _button = new();
    private readonly ResultWindow _result = new();
    private ScreenPoint _anchor;

    public event Action? ButtonClicked;
    public event Action<string>? ReplaceRequested;
    public event Action<string>? InsertRequested;
    public event Action<string>? CopyRequested;
    public event Action? CloseRequested;
    public event Action<string>? TargetLanguageChanged;

    public OverlayPresenter()
    {
        _button.Clicked += () => ButtonClicked?.Invoke();
        _result.ReplaceRequested += text => ReplaceRequested?.Invoke(text);
        _result.InsertRequested += text => InsertRequested?.Invoke(text);
        _result.CopyRequested += text => CopyRequested?.Invoke(text);
        _result.CloseRequested += () => CloseRequested?.Invoke();
        _result.TargetLanguageChanged += value => TargetLanguageChanged?.Invoke(value);
    }

    public void ShowButton(ScreenPoint anchor)
    {
        _anchor = anchor;
        _result.Hide();
        _button.ShowAt(anchor);
    }

    public void ShowLoading(ScreenPoint anchor)
    {
        _anchor = anchor;
        _button.Hide();
        _result.ShowLoading(anchor);
    }

    public void ShowResult(TranslationView view)
    {
        _button.Hide();
        _result.SetTargetLanguages(
            LanguageCatalog.All.Select(x => x.QwenName).Distinct(),
            view.TargetLanguage);
        _result.ShowResult(view, _anchor);
    }

    public void ShowError(TranslationError error, ScreenPoint anchor)
    {
        _button.Hide();
        _result.ShowError(error, anchor);
    }

    public void ShowStatus(string message) => _result.SetStatus(message);
    public void HideAll() { _button.Hide(); _result.Hide(); }
}
```

- [ ] **Step 4: Manual focus tests**

- Button click does not change foreground window.
- Result card initial show does not steal focus.
- Clicking card text activates it.
- Esc/outside click hides.
- 100%/150%/200% DPI and dual monitor positions.

- [ ] **Step 5: Checkpoint**

Report: focus behavior, positioning. No commit unless authorized.

---

### Task 12: Global hotkey and native tray

**Files:**
- Create: `src/FastTranslation.Platform.Windows/Hotkey/GlobalHotkeyManager.cs`
- Create: `src/FastTranslation.Platform.Windows/Hotkey/ForegroundFullscreen.cs`
- Create: `src/FastTranslation.Platform.Windows/Tray/ShellTrayIcon.cs`

**Interfaces:**
- Produces: `GlobalHotkeyManager.Register(string, Action)`, `Unregister()`; `ShellTrayIcon` with menu events.

- [ ] **Step 1: Hotkey manager**

Use `RegisterHotKey` on a hidden `HwndSource`:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows.Hotkey;

public sealed class GlobalHotkeyManager : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private HwndSource? _source;
    private int _id = 0xA001;
    private Action? _callback;

    public bool Register(string hotkey, Action callback)
    {
        Unregister();
        if (!HotkeyParser.TryParse(hotkey, out var modifiers, out var vk)) return false;

        _source = new HwndSource(new HwndSourceParameters("FastTranslation.Hotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80
        });
        _source.AddHook(WndProc);
        if (!RegisterHotKey(_source.Handle, _id, modifiers, vk))
        {
            _source.Dispose();
            _source = null;
            return false;
        }
        _callback = callback;
        return true;
    }

    public void Unregister()
    {
        if (_source is null) return;
        UnregisterHotKey(_source.Handle, _id);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _source = null;
        _callback = null;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            _callback?.Invoke();
            handled = true;
        }
        return 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint hWnd, int id);

    public void Dispose() => Unregister();
}

internal static class HotkeyParser
{
    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": modifiers |= 0x0002; break;
                case "alt": modifiers |= 0x0001; break;
                case "shift": modifiers |= 0x0004; break;
                case "win": modifiers |= 0x0008; break;
                default:
                    if (raw.Length == 1 && char.IsLetterOrDigit(raw[0]))
                        vk = char.ToUpperInvariant(raw[0]);
                    else if (raw.StartsWith('F') && int.TryParse(raw[1..], out var n) && n is >= 1 and <= 12)
                        vk = (uint)(0x70 + n - 1);
                    else
                        return false;
                    break;
            }
        }
        return vk != 0 && !(modifiers == 0x0002 && vk == 0x43); // reject Ctrl+C
    }
}
```
- If `RegisterHotKey` fails, return false; settings UI prompts for another key.
- Fullscreen suppression is handled by the app; the parser only rejects Ctrl+C.

`ForegroundFullscreen.cs`:
```csharp
using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows.Hotkey;

public static class ForegroundFullscreen
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO info);

    public static bool IsFullscreen()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || !GetWindowRect(hwnd, out var window)) return false;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;
        return window.Left <= info.rcMonitor.Left &&
               window.Top <= info.rcMonitor.Top &&
               window.Right >= info.rcMonitor.Right &&
               window.Bottom >= info.rcMonitor.Bottom;
    }
}
```

- [ ] **Step 2: Tray icon**

Use `Shell_NotifyIcon` with `NOTIFYICONDATA`, a hidden top-level window (not message-only), handle `WM_TRAYICON`, `TaskbarCreated`, and `TrackPopupMenu`:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows.Tray;

public sealed class ShellTrayIcon : IDisposable
{
    private const int WM_TRAYICON = 0x8001;
    private const int WM_COMMAND = 0x0111;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 1;
    private const uint NIF_ICON = 2;
    private const uint NIF_TIP = 4;
    private const int ID_SETTINGS = 1001;
    private const int ID_PAUSE = 1002;
    private const int ID_EXIT = 1003;

    private readonly HwndSource _source;
    private readonly uint _taskbarCreated;
    private NOTIFYICONDATA _data;
    private bool _visible;

    public event Action? SettingsRequested;
    public event Action? PauseRequested;
    public event Action? ExitRequested;

    public ShellTrayIcon()
    {
        _source = new HwndSource(new HwndSourceParameters("FastTranslation.Tray")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0x80
        });
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        _source.AddHook(WndProc);
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source.Handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIcon(IntPtr.Zero, 32512),
            szTip = "Fast Translation"
        };
    }

    public void Show()
    {
        if (_visible) return;
        Shell_NotifyIcon(NIM_ADD, ref _data);
        _visible = true;
    }

    public void Dispose()
    {
        if (_visible) Shell_NotifyIcon(NIM_DELETE, ref _data);
        _source.RemoveHook(WndProc);
        _source.Dispose();
        _visible = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == _taskbarCreated && _visible)
        {
            Shell_NotifyIcon(NIM_ADD, ref _data);
            handled = true;
        }
        if (msg == WM_TRAYICON)
        {
            var evt = (int)(lParam.ToInt64() & 0xFFFF);
            if (evt == WM_LBUTTONDBLCLK) SettingsRequested?.Invoke();
            if (evt == WM_RBUTTONUP) ShowMenu();
            handled = true;
        }
        if (msg == WM_COMMAND)
        {
            switch ((int)(wParam.ToInt64() & 0xFFFF))
            {
                case ID_SETTINGS: SettingsRequested?.Invoke(); break;
                case ID_PAUSE: PauseRequested?.Invoke(); break;
                case ID_EXIT: ExitRequested?.Invoke(); break;
            }
            handled = true;
        }
        return 0;
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenu(menu, 0, ID_SETTINGS, "设置");
        AppendMenu(menu, 0, ID_PAUSE, "暂停/恢复划词");
        AppendMenu(menu, 0, ID_EXIT, "退出");
        GetCursorPos(out var pt);
        SetForegroundWindow(_source.Handle);
        TrackPopupMenu(menu, 0, pt.X, pt.Y, 0, _source.Handle, IntPtr.Zero);
        PostMessage(_source.Handle, 0, 0, 0);
        DestroyMenu(menu);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA data);
    [DllImport("user32.dll")] private static extern nint LoadIcon(nint hInstance, nint iconName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] private static extern nint CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(nint menu, uint flags, int id, string text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(nint menu);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")] private static extern int TrackPopupMenu(nint menu, uint flags, int x, int y, int reserved, nint hWnd, nint rect);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);
}
```
- Use the app icon in `hIcon`; the probe uses the default icon. Dispose removes the icon.

- [ ] **Step 3: Manual tests**

- Hotkey toggles pause/resume; conflict error shown.
- Fullscreen app: hotkeys suppressed if enabled.
- Explorer restart: tray icon reappears.
- Right-click menu appears and doesn't leave a stuck menu.
- Exit removes the icon.

- [ ] **Step 4: Checkpoint**

Report: hotkey/tray behavior. No commit unless authorized.

---

## Phase 3 — Write-back and App

### Task 13: Write-back service

**Files:**
- Create: `src/FastTranslation.Core/WriteBack/WriteBackDecision.cs`
- Create: `src/FastTranslation.Platform.Windows/WriteBack/ClipboardWriteBack.cs`
- Create: `src/FastTranslation.Platform.Windows/WriteBack/InputSender.cs`
- Create: `tests/FastTranslation.Core.Tests/WriteBackDecisionTests.cs`

**Interfaces:**
- Consumes: `ISelectionProvider`, `ClipboardService`, `ClipboardFallback`.
- Produces: `IWriteBackService` implementation; `WriteBackDecision` pure policy.

- [ ] **Step 1: Write failing decision tests**

`WriteBackDecisionTests.cs`:
```csharp
using FastTranslation.Core.Selection;
using FastTranslation.Core.WriteBack;

namespace FastTranslation.Core.Tests;

public class WriteBackDecisionTests
{
    private static SelectionSnapshot Snapshot(bool writable = true, string? runtimeId = "r") =>
        new("hello", [], "app", false, SelectionSource.UiaPoint, 1, runtimeId, writable);

    [Fact]
    public void ReadOnly_Is_Unsupported()
        => Assert.Equal(WriteBackStatus.Unsupported, WriteBackDecision.Evaluate(Snapshot(false), true, false));

    [Fact]
    public void Missing_RuntimeId_Is_Unsupported()
        => Assert.Equal(WriteBackStatus.Unsupported, WriteBackDecision.Evaluate(Snapshot(true, null), true, false));

    [Fact]
    public void Terminal_Is_Unsupported()
        => Assert.Equal(WriteBackStatus.Unsupported, WriteBackDecision.Evaluate(Snapshot(), false, false));

    [Fact]
    public void Writable_With_RuntimeId_Is_Allowed()
        => Assert.Null(WriteBackDecision.Evaluate(Snapshot(), true, false));
}
```

- [ ] **Step 2: Implement decision**

`WriteBackDecision.cs`:
```csharp
using FastTranslation.Core.Selection;

namespace FastTranslation.Core.WriteBack;

public static class WriteBackDecision
{
    public static WriteBackStatus? Evaluate(
        SelectionSnapshot snapshot, bool writeBackEnabled, bool isExcludedApp, bool isClipboardBusy = false)
    {
        if (!writeBackEnabled || isExcludedApp) return WriteBackStatus.Unsupported;
        if (snapshot.IsPassword || !snapshot.IsWritable) return WriteBackStatus.Unsupported;
        if (string.IsNullOrWhiteSpace(snapshot.SourceElementRuntimeId)) return WriteBackStatus.Unsupported;
        if (isClipboardBusy) return WriteBackStatus.ClipboardBusy;
        return null;
    }
}
```

- [ ] **Step 3: Implement platform write-back**

`ClipboardWriteBack.cs`:
```csharp
using System.Runtime.InteropServices;
using System.Windows.Automation;
using FastTranslation.Core.Abstractions;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Settings;
using FastTranslation.Core.WriteBack;
using FastTranslation.Platform.Windows.Clipboard;

namespace FastTranslation.Platform.Windows.WriteBack;

public sealed class ClipboardWriteBack(
    ISelectionProvider selection,
    ClipboardService clipboard,
    SettingsModel settings) : IWriteBackService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool CanWriteBack(SelectionSnapshot snapshot)
    {
        var excluded =
            settings.WriteBackExcludedApps.Contains(snapshot.ProcessName, StringComparer.OrdinalIgnoreCase) ||
            settings.ExcludedApps.Contains(snapshot.ProcessName, StringComparer.OrdinalIgnoreCase);
        return WriteBackDecision.Evaluate(snapshot, settings.EnableWriteBack, excluded) is null;
    }

    public Task<WriteBackResult> ReplaceAsync(
        SelectionSnapshot snapshot, string translation, CancellationToken ct)
        => WriteAsync(snapshot, translation, insert: false, ct);

    public Task<WriteBackResult> InsertAfterAsync(
        SelectionSnapshot snapshot, string translation, CancellationToken ct)
        => WriteAsync(snapshot, translation, insert: true, ct);

    private async Task<WriteBackResult> WriteAsync(
        SelectionSnapshot snapshot, string translation, bool insert, CancellationToken ct)
    {
        var excluded =
            settings.WriteBackExcludedApps.Contains(snapshot.ProcessName, StringComparer.OrdinalIgnoreCase) ||
            settings.ExcludedApps.Contains(snapshot.ProcessName, StringComparer.OrdinalIgnoreCase);
        var decision = WriteBackDecision.Evaluate(snapshot, settings.EnableWriteBack, excluded);
        if (decision is not null) return new WriteBackResult(decision.Value, "当前目标不支持写回。");

        await _gate.WaitAsync(ct);
        try
        {
            var foreground = GetForegroundWindow();
            if (foreground != snapshot.SourceWindowHandle)
            {
                GetWindowThreadProcessId(foreground, out var foregroundPid);
                if (foregroundPid != (uint)Environment.ProcessId)
                    return new WriteBackResult(WriteBackStatus.SourceChanged, "原窗口已变化，译文已复制。");
                SetForegroundWindow(snapshot.SourceWindowHandle);
                await Task.Delay(100, ct);
                if (GetForegroundWindow() != snapshot.SourceWindowHandle)
                    return new WriteBackResult(WriteBackStatus.SourceChanged, "无法取回原窗口，译文已复制。");
            }

            var current = await selection.ReadFromFocusAsync(allowClipboard: false, ct);
            if (current is null ||
                current.SourceElementRuntimeId != snapshot.SourceElementRuntimeId ||
                TextNormalizer.Normalize(current.Text) != TextNormalizer.Normalize(snapshot.Text))
                return new WriteBackResult(WriteBackStatus.SelectionChanged, "原选区已变化，译文已复制。");

            if (insert && !TryCollapseToEnd())
            {
                InputSender.SendRightArrow();
                await Task.Delay(50, ct);
            }

            if (!clipboard.HasNonTextFormat())
            {
                var original = clipboard.GetText();
                try
                {
                    clipboard.SetText(translation);
                    InputSender.SendCtrlV();
                    await Task.Delay(250, ct);
                }
                finally
                {
                    if (original is not null) clipboard.SetText(original);
                }
            }
            else
            {
                InputSender.SendUnicode(translation);
                await Task.Delay(150, ct);
            }

            var after = await selection.ReadFromFocusAsync(allowClipboard: false, ct);
            var probe = translation[..Math.Min(20, translation.Length)];
            if (after is null || !after.Text.Contains(probe, StringComparison.Ordinal))
                return new WriteBackResult(WriteBackStatus.Unverified, "已发送，请检查原应用；译文已复制。");

            return new WriteBackResult(WriteBackStatus.Success);
        }
        catch (Exception)
        {
            return new WriteBackResult(WriteBackStatus.Failed, "写回失败，译文已复制。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool TryCollapseToEnd()
    {
        var element = AutomationElement.FocusedElement;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var obj) || obj is not TextPattern pattern)
            return false;
        var ranges = pattern.GetSelection();
        if (ranges.Length == 0) return false;
        var range = ranges[^1].Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End);
        range.Select();
        return true;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
}
```

`InputSender.cs`:
```csharp
using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows.WriteBack;

internal static class InputSender
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 2;
    private const uint KEYEVENTF_UNICODE = 4;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_V = 0x56;
    private const ushort VK_RIGHT = 0x27;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public INPUTUNION data; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION { [FieldOffset(0)] public KEYBDINPUT ki; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    public static void SendCtrlV()
    {
        var inputs = new[]
        {
            Key(VK_CONTROL, 0, 0),
            Key(VK_V, 0, 0),
            Key(VK_V, 0, KEYEVENTF_KEYUP),
            Key(VK_CONTROL, 0, KEYEVENTF_KEYUP)
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendRightArrow()
    {
        var inputs = new[] { Key(VK_RIGHT, 0, 0), Key(VK_RIGHT, 0, KEYEVENTF_KEYUP) };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public static void SendUnicode(string text)
    {
        foreach (var original in text.Replace("\r\n", "\n"))
        {
            var c = original == '\n' ? '\r' : original;
            var inputs = new[]
            {
                Key(0, c, KEYEVENTF_UNICODE),
                Key(0, c, KEYEVENTF_UNICODE | KEYEVENTF_KEYUP)
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }
    }

    private static INPUT Key(ushort vk, char scan, uint flags) => new()
    {
        type = INPUT_KEYBOARD,
        data = new INPUTUNION
        {
            ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags }
        }
    };
}
```

- [ ] **Step 4: Manual tests**

- Notepad/Word/Chrome textarea/contenteditable: replace and insert.
- Read-only page/PDF: buttons disabled.
- Terminal: disabled.
- Clipboard with image: typing fallback, image preserved.
- Switch browser tab: RuntimeId mismatch, abort.
- Card active: source reactivated; selection preserved or abort.

- [ ] **Step 5: Checkpoint**

Report: replace/insert success rates, failure modes. No commit unless authorized.

---

### Task 14: App shell, settings UI, onboarding

**Files:**
- Create: `src/FastTranslation.App/FastTranslation.App.csproj`
- Create: `src/FastTranslation.App/App.xaml(.cs)`
- Create: `src/FastTranslation.App/CompositionRoot.cs`
- Create: `src/FastTranslation.App/app.manifest`
- Create: `src/FastTranslation.App/Settings/SettingsWindow.xaml(.cs)`
- Create: `src/FastTranslation.App/Onboarding/OnboardingWindow.xaml(.cs)`
- Create: `src/FastTranslation.App/ThemeService.cs`
- Create: `src/FastTranslation.App/Themes/Light.xaml`
- Create: `src/FastTranslation.App/Themes/Dark.xaml`
- Create: `src/FastTranslation.Platform.Windows/Startup/StartupRegistration.cs`

**Interfaces:**
- Produces: runnable tray app wiring all services.

- [ ] **Step 1: App project**

`FastTranslation.App.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationManifest>app.manifest</ApplicationManifest>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\FastTranslation.Core\FastTranslation.Core.csproj" />
    <ProjectReference Include="..\FastTranslation.Platform.Windows\FastTranslation.Platform.Windows.csproj" />
    <ProjectReference Include="..\FastTranslation.Providers.QwenMt\FastTranslation.Providers.QwenMt.csproj" />
  </ItemGroup>
</Project>
```

`app.manifest`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<assembly manifestVersion="1.0" xmlns="urn:schemas-microsoft-com:asm.v1">
  <application xmlns="urn:schemas-microsoft-com:asm.v3">
    <windowsSettings>
      <dpiAwareness xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">PerMonitorV2</dpiAwareness>
      <longPathAware xmlns="http://schemas.microsoft.com/SMI/2016/WindowsSettings">true</longPathAware>
    </windowsSettings>
  </application>
</assembly>
```

`App.xaml`:
```xml
<Application x:Class="FastTranslation.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Application.Resources>
    <ResourceDictionary>
      <ResourceDictionary.MergedDictionaries>
        <ResourceDictionary Source="Themes/Light.xaml"/>
      </ResourceDictionary.MergedDictionaries>
    </ResourceDictionary>
  </Application.Resources>
</Application>
```

`Themes/Light.xaml`:
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="ButtonBackground" Color="#FFFFFFFF"/>
  <SolidColorBrush x:Key="ButtonBorder" Color="#FFD0D0D0"/>
  <SolidColorBrush x:Key="ButtonForeground" Color="#FF111111"/>
  <SolidColorBrush x:Key="CardBackground" Color="#FFFFFFFF"/>
  <SolidColorBrush x:Key="CardBorder" Color="#FFD0D0D0"/>
</ResourceDictionary>
```

`Themes/Dark.xaml`:
```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <SolidColorBrush x:Key="ButtonBackground" Color="#FF2B2B2B"/>
  <SolidColorBrush x:Key="ButtonBorder" Color="#FF555555"/>
  <SolidColorBrush x:Key="ButtonForeground" Color="#FFF0F0F0"/>
  <SolidColorBrush x:Key="CardBackground" Color="#FF1E1E1E"/>
  <SolidColorBrush x:Key="CardBorder" Color="#FF555555"/>
</ResourceDictionary>
```

`ThemeService.cs`:
```csharp
using System.Windows;
using Microsoft.Win32;

namespace FastTranslation.App;

public static class ThemeService
{
    public static void Apply(string theme)
    {
        var dark = theme == "dark" || (theme == "system" && IsSystemDark());
        var uri = new Uri(dark ? "Themes/Dark.xaml" : "Themes/Light.xaml", UriKind.Relative);
        Application.Current.Resources.MergedDictionaries.Clear();
        Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = uri });
    }

    private static bool IsSystemDark()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }
}
```

`App.xaml.cs`:
```csharp
using System.Threading;
using System.Windows;
using FastTranslation.App.Onboarding;

namespace FastTranslation.App;

public partial class App : Application
{
    private Mutex? _mutex;
    private CompositionRoot? _root;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "FastTranslation.SingleInstance", out var created);
        if (!created)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        _root = new CompositionRoot();
        ThemeService.Apply(_root.Settings.Theme);

        if (string.IsNullOrWhiteSpace(_root.SecretStore.ReadApiKey()))
        {
            var onboarding = new OnboardingWindow(_root);
            if (onboarding.ShowDialog() != true)
            {
                Shutdown();
                return;
            }
        }

        _root.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _root?.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
```

- [ ] **Step 2: Composition root**

`CompositionRoot.cs`:
```csharp
using System.Windows;
using FastTranslation.App.Settings;
using FastTranslation.Core.Language;
using FastTranslation.Core.Selection;
using FastTranslation.Core.Settings;
using FastTranslation.Core.Translation;
using FastTranslation.Platform.Windows.Clipboard;
using FastTranslation.Platform.Windows.Hotkey;
using FastTranslation.Platform.Windows.Overlay;
using FastTranslation.Platform.Windows.Security;
using FastTranslation.Platform.Windows.Tray;
using FastTranslation.Platform.Windows.Uia;
using FastTranslation.Platform.Windows.WriteBack;
using FastTranslation.Providers.QwenMt;

public sealed class CompositionRoot : IDisposable
{
    public SettingsStore SettingsStore { get; }
    public SettingsModel Settings { get; private set; }
    public DpapiSecretStore SecretStore { get; }
    public ClipboardService Clipboard { get; }
    public ClipboardFallback ClipboardFallback { get; }
    public UiaSelectionReader SelectionReader { get; }
    public QwenMtProvider Provider { get; }
    public TranslationService Translation { get; }
    public ClipboardWriteBack WriteBack { get; }
    public OverlayPresenter Overlay { get; }
    public SelectionCoordinator Coordinator { get; }
    public GlobalMouseMonitor Mouse { get; }
    public GlobalHotkeyManager Hotkey { get; }
    public ShellTrayIcon Tray { get; }

    public CompositionRoot()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();
        SecretStore = new DpapiSecretStore();
        Clipboard = new ClipboardService();
        ClipboardFallback = new ClipboardFallback(Clipboard);
        SelectionReader = new UiaSelectionReader(
            ClipboardFallback,
            process => Settings.ExcludedApps.Contains(process, StringComparer.OrdinalIgnoreCase) ||
                       Settings.ClipboardFallbackExcludedApps.Contains(process, StringComparer.OrdinalIgnoreCase),
            () => Settings.ClipboardFetchTimeoutMs);
        Provider = new QwenMtProvider(
            new HttpClient { Timeout = TimeSpan.FromSeconds(20) },
            SecretStore.ReadApiKey,
            Settings.Provider.BaseUrl);
        Translation = new TranslationService(Provider);
        WriteBack = new ClipboardWriteBack(SelectionReader, Clipboard, Settings);
        Overlay = new OverlayPresenter();
        Coordinator = new SelectionCoordinator(
            SelectionReader, Overlay, Translation, new LanguagePolicy(), WriteBack, Settings,
            SecretStore.ReadApiKey, text => Clipboard.SetText(text), captureOnClickOnly: false);
        Mouse = new GlobalMouseMonitor();
        Hotkey = new GlobalHotkeyManager();
        Tray = new ShellTrayIcon();
    }

    public void Start()
    {
        Mouse.SelectionCompleted += async (_, e) =>
            await Coordinator.OnGestureAsync(e.ScreenPoint, CancellationToken.None);
        Mouse.Start();

        if (!Hotkey.Register(Settings.Hotkey, () =>
        {
            if (!ForegroundFullscreen.IsFullscreen())
                _ = Coordinator.OnHotkeyAsync(CancellationToken.None);
        }))
        {
            MessageBox.Show("热键注册失败，请在设置中换一个组合。");
        }

        Overlay.ButtonClicked += () => _ = Coordinator.OnButtonClickedAsync(CancellationToken.None);
        Overlay.ReplaceRequested += text => _ = Coordinator.ReplaceAsync(text, CancellationToken.None);
        Overlay.InsertRequested += text => _ = Coordinator.InsertAsync(text, CancellationToken.None);
        Overlay.CopyRequested += text => Clipboard.SetText(text);
        Overlay.CloseRequested += Coordinator.Dismiss;
        Overlay.TargetLanguageChanged += target => _ = Coordinator.ChangeTargetAsync(target, CancellationToken.None);

        Tray.SettingsRequested += OpenSettings;
        Tray.PauseRequested += TogglePause;
        Tray.ExitRequested += Dispose;
        Tray.Show();
    }

    private bool _paused;
    private void TogglePause()
    {
        _paused = !_paused;
        if (_paused) Mouse.Stop();
        else Mouse.Start();
    }

    private SettingsWindow? _settingsWindow;
    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    public void Dispose()
    {
        Hotkey.Dispose();
        Mouse.Dispose();
        Tray.Dispose();
    }
}
```
- Add `using System.Windows;` and `using FastTranslation.App.Settings;` at the top of CompositionRoot.

- [ ] **Step 3: Settings window**

`SettingsWindow.xaml`:
```xml
<Window x:Class="FastTranslation.App.Settings.SettingsWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Fast Translation 设置" Width="520" Height="620">
  <StackPanel Margin="16">
    <TextBlock Text="API Key"/>
    <PasswordBox x:Name="ApiKeyBox"/>
    <Button Content="验证并保存" Click="Save_Click" HorizontalAlignment="Left" Margin="0,8,0,0"/>
    <TextBlock Text="模型" Margin="0,12,0,0"/>
    <ComboBox x:Name="ModelBox"/>
    <TextBlock Text="目标语言" Margin="0,12,0,0"/>
    <ComboBox x:Name="TargetBox"/>
    <TextBlock Text="热键" Margin="0,12,0,0"/>
    <TextBox x:Name="HotkeyBox"/>
    <CheckBox x:Name="FallbackBox" Content="UIA 失败时使用剪贴板兜底" Margin="0,12,0,0"/>
    <CheckBox x:Name="WriteBackBox" Content="允许替换/插入" Margin="0,8,0,0"/>
    <CheckBox x:Name="StartupBox" Content="开机启动" Margin="0,8,0,0"/>
    <TextBlock Text="主题" Margin="0,12,0,0"/>
    <ComboBox x:Name="ThemeBox"/>
    <Button Content="关闭" Click="Close_Click" HorizontalAlignment="Left" Margin="0,16,0,0"/>
  </StackPanel>
</Window>
```

`SettingsWindow.xaml.cs`:
```csharp
using System.Windows;
using FastTranslation.Core.Language;
using FastTranslation.Core.Translation;
using FastTranslation.Platform.Windows.Startup;

namespace FastTranslation.App.Settings;

public partial class SettingsWindow : Window
{
    private readonly CompositionRoot _root;

    public SettingsWindow(CompositionRoot root)
    {
        InitializeComponent();
        _root = root;
        ModelBox.ItemsSource = new[] { "qwen-mt-flash", "qwen-mt-plus" };
        ModelBox.SelectedItem = root.Settings.Provider.Model;
        TargetBox.ItemsSource = LanguageCatalog.All.Select(x => x.QwenName).Distinct().ToList();
        TargetBox.SelectedItem = root.Settings.Language.TargetLanguage;
        HotkeyBox.Text = root.Settings.Hotkey;
        FallbackBox.IsChecked = root.Settings.ClipboardFallback;
        WriteBackBox.IsChecked = root.Settings.EnableWriteBack;
        StartupBox.IsChecked = root.Settings.StartWithWindows;
        ThemeBox.ItemsSource = new[] { "system", "light", "dark" };
        ThemeBox.SelectedItem = root.Settings.Theme;
        ApiKeyBox.Password = root.SecretStore.ReadApiKey() ?? "";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("请填写 API Key。");
            return;
        }

        _root.SecretStore.WriteApiKey(key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _root.Provider.TranslateAsync(
                new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"), cts.Token);
        }
        catch (TranslationException ex)
        {
            _root.SecretStore.DeleteApiKey();
            MessageBox.Show($"Key 验证失败：{ex.Code}");
            return;
        }

        var settings = _root.Settings;
        settings.Provider.Model = ModelBox.SelectedItem as string ?? "qwen-mt-flash";
        settings.Language.TargetLanguage = TargetBox.SelectedItem as string;
        settings.Hotkey = HotkeyBox.Text.Trim();
        settings.ClipboardFallback = FallbackBox.IsChecked == true;
        settings.EnableWriteBack = WriteBackBox.IsChecked == true;
        settings.StartWithWindows = StartupBox.IsChecked == true;
        settings.Theme = ThemeBox.SelectedItem as string ?? "system";
        _root.SettingsStore.Save(settings);
        StartupRegistration.Set(settings.StartWithWindows);
        ThemeService.Apply(settings.Theme);
        MessageBox.Show("已保存。热键、模型和写回设置重启后生效。");
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
```

`StartupRegistration.cs`:
```csharp
using Microsoft.Win32;

namespace FastTranslation.Platform.Windows.Startup;

public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "FastTranslation";

    public static void Set(bool enabled, string? exePath = null)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null) return;

        if (!enabled)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            return;
        }

        var path = exePath ?? Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path)) return;
        key.SetValue(ValueName, $"\"{path}\"");
    }
}
```

- [ ] **Step 4: Onboarding**

`OnboardingWindow.xaml`:
```xml
<Window x:Class="FastTranslation.App.Onboarding.OnboardingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Fast Translation 首次设置" Width="560" Height="420">
  <TabControl Margin="16">
    <TabItem Header="1 隐私">
      <StackPanel Margin="16">
        <TextBlock TextWrapping="Wrap"
                   Text="选中文本会发送到阿里云百炼；UIA 失败时可能模拟 Ctrl+C；替换/插入只在可写控件中可用，并会临时使用剪贴板。"/>
        <CheckBox x:Name="AcceptCheck" Content="我已了解并同意" Margin="0,12,0,0"/>
      </StackPanel>
    </TabItem>
    <TabItem Header="2 API Key">
      <StackPanel Margin="16">
        <TextBlock TextWrapping="Wrap"
                   Text="打开 https://bailian.console.aliyun.com/ 创建 API Key，然后粘贴到这里。"/>
        <PasswordBox x:Name="KeyBox" Margin="0,12,0,0"/>
      </StackPanel>
    </TabItem>
    <TabItem Header="3 完成">
      <StackPanel Margin="16">
        <TextBlock Text="全局热键"/>
        <TextBox x:Name="HotkeyBox" Text="Ctrl+Alt+T"/>
        <CheckBox x:Name="StartupBox" Content="开机启动" Margin="0,12,0,0"/>
        <Button Content="完成" Click="Finish_Click" HorizontalAlignment="Left" Margin="0,16,0,0"/>
      </StackPanel>
    </TabItem>
  </TabControl>
</Window>
```

`OnboardingWindow.xaml.cs`:
```csharp
using System.Windows;
using FastTranslation.Core.Translation;
using FastTranslation.Platform.Windows.Startup;

namespace FastTranslation.App.Onboarding;

public partial class OnboardingWindow : Window
{
    private readonly CompositionRoot _root;

    public OnboardingWindow(CompositionRoot root)
    {
        InitializeComponent();
        _root = root;
        HotkeyBox.Text = root.Settings.Hotkey;
    }

    private async void Finish_Click(object sender, RoutedEventArgs e)
    {
        if (AcceptCheck.IsChecked != true)
        {
            MessageBox.Show("请先同意隐私说明。");
            return;
        }

        var key = KeyBox.Password.Trim();
        if (string.IsNullOrWhiteSpace(key))
        {
            MessageBox.Show("请填写 API Key。");
            return;
        }

        _root.SecretStore.WriteApiKey(key);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await _root.Provider.TranslateAsync(
                new TranslationRequest("hello", "auto", "Chinese", "qwen-mt-flash"), cts.Token);
        }
        catch (TranslationException ex)
        {
            _root.SecretStore.DeleteApiKey();
            MessageBox.Show($"Key 验证失败：{ex.Code}");
            return;
        }

        _root.Settings.Hotkey = string.IsNullOrWhiteSpace(HotkeyBox.Text)
            ? "Ctrl+Alt+T"
            : HotkeyBox.Text.Trim();
        _root.Settings.StartWithWindows = StartupBox.IsChecked == true;
        _root.SettingsStore.Save(_root.Settings);
        StartupRegistration.Set(_root.Settings.StartWithWindows);
        DialogResult = true;
    }
}
```

- [ ] **Step 5: Manual tests**

- First run onboarding; skip path.
- Tray menu; settings open; key validation success/failure.
- Single instance: second launch opens settings.
- Pause/resume.
- Startup registry off by default.

- [ ] **Step 6: Checkpoint**

Report: app launches to tray, onboarding/settings work. No commit unless authorized.

---

## Phase 4 — Release

### Task 15: Installer, portable build, docs

**Files:**
- Create: `installer/FastTranslation.iss`
- Create: `README.md`
- Create: `PRIVACY.md`
- Create: `THIRD_PARTY_NOTICES.md`

- [ ] **Step 1: Publish**

Run:
```bash
dotnet publish src/FastTranslation.App/FastTranslation.App.csproj -c Release -r win-x64 --self-contained true -o publish/win-x64
```
Expected: output folder runs from `publish/win-x64/FastTranslation.App.exe`.

- [ ] **Step 2: Inno Setup script**

`installer/FastTranslation.iss`:
```ini
[Setup]
AppName=Fast Translation
AppVersion=1.0.0-beta.1
DefaultDirName={localappdata}\Programs\FastTranslation
DefaultGroupName=Fast Translation
PrivilegesRequired=lowest
OutputBaseFilename=FastTranslation-1.0.0-beta.1-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "..\publish\win-x64\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Fast Translation"; Filename: "{app}\FastTranslation.App.exe"
Name: "{group}\Uninstall Fast Translation"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\FastTranslation.App.exe"; Description: "Launch Fast Translation"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
```

- [ ] **Step 3: Portable zip and checksums**

Run:
```bash
powershell -NoProfile -Command "Compress-Archive -Path publish/win-x64/* -DestinationPath FastTranslation-1.0.0-beta.1-portable.zip -Force"
powershell -NoProfile -Command "Get-FileHash FastTranslation-1.0.0-beta.1-portable.zip -Algorithm SHA256 | Format-List"
```

- [ ] **Step 4: Docs**

README: what it does, support tiers, key setup link, SmartScreen instructions, build/test commands, privacy summary.
PRIVACY: selected text goes to Alibaba Cloud with the user's key; no logging/telemetry; clipboard fallback/write-back behavior.
THIRD_PARTY_NOTICES: STranslate/Text-Grab/Translumo attribution if code is adapted; license texts.

- [ ] **Step 5: Checkpoint**

Report: installer/portable paths, checksums, docs. No commit unless authorized.

---

### Task 16: Full validation and beta release

**Files:**
- Modify: `README.md` release notes
- Create: `docs/superpowers/plans/2026-10-23-fast-translation-v1-results.md`

- [ ] **Step 1: Run automated tests**

Run:
```bash
dotnet test FastTranslation.sln -c Release -v minimal
```
Expected: all pass.

- [ ] **Step 2: Memory measurement**

- Launch published app, wait 5 minutes, read Task Manager “内存（专用工作集）”.
- Perform 100 translations (mock or real key), measure again.
- Confirm ≤50MB cold and ≤60MB warm; no monotonic growth.
- If over, stop and execute the spec's stack-change branch.

- [ ] **Step 3: Manual matrix**

Execute spec §13.3:
- Official apps: Chrome/Edge/Word/Edge PDF; Firefox/Acrobat if M0 passed.
- Selection types, DPI, multi-monitor, hotkey, clipboard fallback, password fields, terminals, elevated windows, write-back, errors.
- Record pass/fail and notes in the results file.

- [ ] **Step 4: Installer/portable test**

- Install per-user without admin; launch; uninstall; keep/delete settings.
- Run portable zip; verify no registry writes except optional startup.
- Verify unsigned SmartScreen instructions and SHA-256.

- [ ] **Step 5: Beta release**

- Tag `1.0.0-beta.1` only if the user authorizes git operations.
- Publish installer, portable zip, SHA-256, release notes.
- Do not claim stable; label as unsigned developer beta.

- [ ] **Step 6: Checkpoint**

Report: test matrix, memory numbers, known limitations, release artifacts. No commit/push unless explicitly authorized.

---

## Self-Review

- **Spec coverage:** M0 (Tasks 0.1–0.3), core capture/language/settings/provider/coordinator (1–6), platform (7–12), write-back (13), app UI/onboarding (14), release (15–16). v1.1 proxy/macOS are explicitly out of scope; spec §16 covers them separately.
- **Placeholder scan:** no `TBD`/`TODO`; language table data is sourced from the official Qwen-MT page; probe is intentionally throwaway.
- **Type consistency:** `ScreenPoint`/`ScreenRect`, `SelectionSnapshot`, `TranslationRequest/Result`, `WriteBackStatus/Result`, `SelectionCoordinator` methods are used consistently across tasks.
- **Known gate:** if M0 fails memory/UIA/write-back, stop and update the spec before Phase 1; do not silently relax 50MB.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-10-23-fast-translation-v1.md`.

Two execution options:

1. **Subagent-Driven (recommended)** — dispatch a fresh subagent per task, review between tasks, fast iteration.
2. **Inline Execution** — execute tasks in this session using executing-plans, batch execution with checkpoints.

Which approach?