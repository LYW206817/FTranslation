using System.IO;
using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows;

/// <summary>交给 Windows 验证安装包签名、完整性和证书链。</summary>
public static class UpdateAuthenticode
{
    private static readonly Guid VerifyAuthenticode = new("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
    private const uint NoUi = 2;
    private const uint VerifyWholeChain = 1;
    private const uint FileChoice = 1;

    public static bool IsTrusted(string path)
    {
        var filePath = Marshal.StringToCoTaskMemUni(Path.GetFullPath(path));
        var file = IntPtr.Zero;
        try
        {
            file = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(new WinTrustFileInfo
            {
                Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                Path = filePath,
            }, file, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = NoUi,
                RevocationChecks = VerifyWholeChain,
                UnionChoice = FileChoice,
                File = file,
            };
            // WinVerifyTrust 返回 LONG，只有恰好为 0 代表签名与证书链都受信。
            var action = VerifyAuthenticode;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        finally
        {
            Marshal.FreeHGlobal(file);
            Marshal.FreeCoTaskMem(filePath);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr FileHandle;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallback;
        public IntPtr ClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr State;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
