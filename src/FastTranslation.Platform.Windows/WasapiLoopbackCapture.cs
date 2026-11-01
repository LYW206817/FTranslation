using System.Runtime.InteropServices;

namespace FastTranslation.Platform.Windows;

/// <summary>
/// 设备级回环采集（v76，设计 §5.1）：把默认渲染设备正在播放的混音抓成 PCM 块。
/// <para>
/// 只产出设备混音格式的原始块（通常是 48kHz float32 立体声），16kHz PCM16 由重采样器转换——
/// WASAPI 的 loopback 只给混音格式，不能要求 16k。每个实例只跑一次会话：设备失效后由调用方
/// 重建实例（IAudioClient 绑死了失效的端点，对旧实例重试只会一直拿到 DEVICE_INVALIDATED）。
/// </para>
/// </summary>
public sealed class WasapiLoopbackCapture : IDisposable
{
    /// <summary>设备混音格式。IsFloat 对应 IEEE float 采样（共享模式混音的常态）。</summary>
    public sealed record CaptureFormat(int SampleRate, int Channels, int BitsPerSample, bool IsFloat);

    /// <summary>采集线程拿到混音格式并开始推流。在采集线程上触发。</summary>
    public event Action<CaptureFormat>? Started;

    /// <summary>一批原始 PCM（混音格式）。在采集线程上触发；数组是独立副本，调用方可直接持有。</summary>
    public event Action<byte[]>? DataAvailable;

    /// <summary>渲染设备失效（拔掉耳机、切默认设备、驱动重置）。触发后本实例不再产出数据。</summary>
    public event Action? DeviceInvalidated;

    /// <summary>初始化失败（无渲染设备、被策略禁用等）。通过事件而不是异常报告：调用方在 UI 线程上触发它，跨线程异常会直接崩进程。</summary>
    public event Action<Exception>? Failed;

    /// <summary>混音格式，<see cref="Started"/> 触发后有效。</summary>
    public CaptureFormat? Format { get; private set; }

    private readonly ManualResetEvent _quit = new(false);
    private Thread? _thread;

    /// <summary>启动采集。重复调用是空操作。</summary>
    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }
        _thread = new Thread(Run) { IsBackground = true, Name = "FT.WasapiLoopback" };
        _thread.Start();
    }

    /// <summary>请求停止并等待采集线程退出（最多 2 秒）。Dispose 也会走到这里。</summary>
    public void Stop()
    {
        _quit.Set();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public void Dispose()
    {
        Stop();
        _quit.Dispose();
    }

    private void Run()
    {
        // v76: 整条链路（CoCreateInstance → GetMixFormat → Initialize → 循环取包）都在这条线程上做。
        // COM 对象按单元亲和，跨线程使用 RCW 会退化成跨单元调度，音频这种毫秒级回调等不起。
        _ = NativeMethodsAudio.CoInitializeEx(IntPtr.Zero, NativeMethodsAudio.COINIT_MULTITHREADED);
        try
        {
            RunCore();
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex);
        }
        finally
        {
            NativeMethodsAudio.CoUninitialize();
        }
    }

    /// <summary>初始化链路的 HRESULT 检查：失败即抛，消息带上 API 名与原始码，探针与 UI 才有可读的原因。</summary>
    private static void Check(int hr, string api)
    {
        if (hr != 0)
        {
            throw new InvalidOperationException($"{api} failed: 0x{hr:X8}");
        }
    }

    private void RunCore()
    {
        var enumerator = (NativeMethodsAudio.IMMDeviceEnumerator)new NativeMethodsAudio.MMDeviceEnumeratorComObject();


        Check(enumerator.GetDefaultAudioEndpoint(0, 0, out IntPtr rawDevice), "GetDefaultAudioEndpoint");
        var device = (NativeMethodsAudio.IMMDevice)Marshal.GetObjectForIUnknown(rawDevice);
        Marshal.Release(rawDevice);

        Guid audioClientIid = NativeMethodsAudio.IID_IAudioClient;
        Check(device.Activate(ref audioClientIid, NativeMethodsAudio.CLSCTX_ALL, IntPtr.Zero, out object clientObj),
            "IMMDevice.Activate");
        var client = (NativeMethodsAudio.IAudioClient)clientObj;

        // v76: 混音格式按字节偏移读而不是结构体——WAVEFORMATEX 天然 18 字节，但 WAVEFORMATEXTENSIBLE
        // 在 C 里因 4 字节对齐要在 18 后垫 2 字节（SubFormat 落在偏移 24、总长 40，cbSize=22 说的就是它）。
        // .NET 结构体无论 Pack=2 还是默认都复现不出这套嵌套对齐，显式偏移是唯一不踩坑的读法。
        Check(client.GetMixFormat(out IntPtr mixFormat), "GetMixFormat");
        short tag = Marshal.ReadInt16(mixFormat, 0);
        short channels = Marshal.ReadInt16(mixFormat, 2);
        int sampleRate = Marshal.ReadInt32(mixFormat, 4);
        short blockAlign = Marshal.ReadInt16(mixFormat, 12);
        short bits = Marshal.ReadInt16(mixFormat, 14);
        bool isFloat = tag == NativeMethodsAudio.WAVE_FORMAT_IEEE_FLOAT;
        if (tag == NativeMethodsAudio.WAVE_FORMAT_EXTENSIBLE)
        {
            var subFormat = Marshal.PtrToStructure<Guid>(mixFormat + 24);
            isFloat = subFormat == NativeMethodsAudio.KSDATAFORMAT_SUBTYPE_IEEE_FLOAT;
        }
        Format = new CaptureFormat(sampleRate, channels, bits, isFloat);

        // v76: 事件驱动共享模式要求 bufferDuration 与 periodicity 都是 0——periodicity 传正值
        // 在共享模式必拿 AUDCLNT_E_INVALID_DEVICE_PERIOD，时长 0 让 WASAPI 自己挑设备周期两倍的缓冲。
        Check(client.Initialize(
            NativeMethodsAudio.AUDCLNT_SHAREMODE_SHARED,
            NativeMethodsAudio.AUDCLNT_STREAMFLAGS_LOOPBACK | NativeMethodsAudio.AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
            0, 0, mixFormat, IntPtr.Zero), "IAudioClient.Initialize");
        Marshal.FreeCoTaskMem(mixFormat);

        using var bufferReady = new AutoResetEvent(false);
        Check(client.SetEventHandle(bufferReady.SafeWaitHandle.DangerousGetHandle()), "SetEventHandle");
        Check(client.Start(), "IAudioClient.Start");
        Started?.Invoke(Format);

        Guid captureIid = NativeMethodsAudio.IID_IAudioCaptureClient;
        Check(client.GetService(ref captureIid, out object captureObj), "IAudioClient.GetService");
        var capture = (NativeMethodsAudio.IAudioCaptureClient)captureObj;

        bool invalidated = CaptureLoop(capture, bufferReady, blockAlign);
        _ = client.Stop();
        if (invalidated)
        {
            DeviceInvalidated?.Invoke();
        }
    }

    /// <summary>取包循环，返回是否因设备失效退出（false = 正常停止）。</summary>
    private bool CaptureLoop(NativeMethodsAudio.IAudioCaptureClient capture, AutoResetEvent bufferReady, int blockAlign)
    {
        var waits = new WaitHandle[] { _quit, bufferReady };
        while (true)
        {
            // v76: 等待带 200ms 超时，超时后照样尝试取包。Win8 之前 loopback + 事件句柄几乎不发事件，
            // Win10 起正常，但个别驱动在完全静音时仍不发（事件只随真实渲染脉冲来）。超时兜底让静音期
            // 也能按包推进，代价只是每 200ms 一次空轮询，可以忽略。
            int signaled = WaitHandle.WaitAny(waits, 200);
            if (signaled == 0)
            {
                return false;
            }

            while (true)
            {
                int hr = capture.GetNextPacketSize(out int frames);
                if (hr != 0)
                {
                    return hr == NativeMethodsAudio.AUDCLNT_E_DEVICE_INVALIDATED;
                }
                if (frames == 0)
                {
                    break;
                }
                hr = capture.GetBuffer(out IntPtr data, out frames, out _, out _, out _);
                if (hr != 0)
                {
                    return hr == NativeMethodsAudio.AUDCLNT_E_DEVICE_INVALIDATED;
                }
                var block = new byte[frames * blockAlign];
                Marshal.Copy(data, block, 0, block.Length);
                _ = capture.ReleaseBuffer(frames);
                DataAvailable?.Invoke(block);
            }
        }
    }
}

/// <summary>WASAPI 的 COM 声明（v76）。只声明用到的最小 vtable 槽位；未用的方法也必须按真实顺序占位，槽位错一个全盘皆错。</summary>
internal static class NativeMethodsAudio
{
    public const int ERender = 0;
    public const int EConsole = 0;
    public const int CLSCTX_ALL = 0x17;
    public const int AUDCLNT_SHAREMODE_SHARED = 0;
    public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const short WAVE_FORMAT_IEEE_FLOAT = 3;
    public const short WAVE_FORMAT_EXTENSIBLE = unchecked((short)0xFFFE);
    public const int COINIT_MULTITHREADED = 0;

    public static readonly Guid IID_IMMDeviceEnumerator = new("A95664D2-9614-4F35-A746-DE8DB63617E6");
    public static readonly Guid IID_IMMDevice = new("D666063F-1587-4E43-81F1-B948E807363F");
    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid KSDATAFORMAT_SUBTYPE_IEEE_FLOAT = new("00000003-0000-0010-8000-00AA00389B71");

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();

    /// <summary>裸 vtable 对照用：GetDefaultAudioEndpoint 的原生签名（TEMP-DIAG，诊断完删除）。</summary>
    /// <summary>CoClass 壳：CLSID_MMDeviceEnumerator。new 它拿到 RCW 再转接口。</summary>
    // v76: 故意不 sealed——C# 的显式引用转换规则禁止 sealed 类到接口的转换（编译期 CS0030），
    // COM 对象的 QI 转换要求壳类可被视为"可能有其它接口"，运行期 castclass 会按接口 IID 查询。
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    public class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        // 槽位 3：EnumAudioEndpoints，本方案用不到但必须按真实顺序占位
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        // TEMP-DIAG: out 参数临时是 IntPtr（原为 IMMDevice），绕开封送层的自动 QI，手动查身份
        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IntPtr device);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);

        [PreserveSig]
        int RegisterEndpointNotificationCallback(IntPtr callback);

        [PreserveSig]
        int UnregisterEndpointNotificationCallback(IntPtr callback);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsContext, IntPtr activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object iface);

        [PreserveSig]
        int OpenPropertyStore(int access, out IntPtr store);

        [PreserveSig]
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);

        [PreserveSig]
        int GetState(out int state);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig]
        int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);

        [PreserveSig]
        int GetBufferSize(out int bufferFrames);

        [PreserveSig]
        int GetStreamLatency(out long latency);

        [PreserveSig]
        int GetCurrentPadding(out int padding);

        [PreserveSig]
        int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

        [PreserveSig]
        int GetMixFormat(out IntPtr format);

        [PreserveSig]
        int GetDevicePeriod(out long defaultPeriod, out long minPeriod);

        [PreserveSig]
        int Start();

        [PreserveSig]
        int Stop();

        [PreserveSig]
        int Reset();

        [PreserveSig]
        int SetEventHandle(IntPtr handle);

        [PreserveSig]
        int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig]
        int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);

        [PreserveSig]
        int ReleaseBuffer(int frames);

        [PreserveSig]
        int GetNextPacketSize(out int frames);
    }
}
