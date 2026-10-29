using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace FastTranslation.Platform.Windows;

/// <summary>T12：通过 RegisterHotKey 在消息专用窗口上注册全局热键。</summary>
public sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const int HotkeyId = 0x5446; // "TF"——由两个 ASCII 字符组成的标识

    private readonly HwndSource _source;
    private uint _modifiers;
    private uint _vk;
    private int _registeredId = -1;

    public event Action? Pressed;

    private sealed class MessageWindow
    {
        public HwndSource Source { get; }
        public MessageWindow()
        {
            var parameters = new HwndSourceParameters("FT.HotkeySink") { ParentWindow = new IntPtr(-3) }; // HWND_MESSAGE(消息专用窗口)
            Source = new HwndSource(parameters);
        }
    }

    public HotkeyService()
    {
        var sink = new MessageWindow();
        _source = sink.Source;
        _source.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && (int)wParam == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public bool Register(string hotkey)
    {
        var previousVk = _vk;
        var previousModifiers = _modifiers;
        var wasRegistered = _registeredId >= 0;
        Unregister();
        if (!TryParse(hotkey, out _modifiers, out _vk) ||
            !NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, _modifiers, _vk))
        {
            // 重新注册失败时不能让应用一个热键都不剩
            // (bugbot P1：设置失败时提示「保留旧值」，但旧值其实从未重新注册)
            if (wasRegistered && NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, previousModifiers, previousVk))
            {
                _vk = previousVk;
                _modifiers = previousModifiers;
                _registeredId = HotkeyId;
            }
            return false;
        }
        _registeredId = HotkeyId;
        return true;
    }

    public void Unregister()
    {
        if (_registeredId < 0) return;
        NativeMethods.UnregisterHotKey(_source.Handle, _registeredId);
        _registeredId = -1;
    }

    public static bool TryParse(string text, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var part = rawPart.ToLowerInvariant();
            switch (part)
            {
                case "ctrl": case "control": modifiers |= 0x0002; break;
                case "alt": modifiers |= 0x0001; break;
                case "shift": modifiers |= 0x0004; break;
                case "win": modifiers |= 0x0008; break;
                default:
                    vk = part.Length == 1
                        ? char.ToUpperInvariant(part[0])
                        : part switch
                        {
                            "space" => 0x20,
                            "tab" => 0x09,
                            "enter" => 0x0D,
                            _ => ParseNamedKey(part),
                        };
                    if (vk == 0) return false;
                    break;
            }
        }
        return vk != 0 && modifiers != 0;
    }

    private static uint ParseNamedKey(string part)
    {
        // f1..f24、"d1".."d0" 以及纯数字——"f5" 以前会被解析成 F 字母键
        if (part.Length >= 2 && (part[0] == 'f' || part[0] == 'F') &&
            uint.TryParse(part[1..], out var f) && f is >= 1 and <= 24)
        {
            return 0x70 + f - 1; // VK_F1 = 0x70，后续依次加一
        }
        if (part.Length == 2 && (part[0] == 'd' || part[0] == 'D') && char.IsDigit(part[1]))
        {
            return part[1]; // VK_D0..VK_D9 就是 ASCII 数字键本身
        }
        if (part.Length == 1 && char.IsDigit(part[0])) return part[0];
        return 0;
    }

    public void Dispose()
    {
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
