namespace Probe;

internal static class SoftRenderInit
{
    // M0 memory gate: the D3D render path costs ~77MB private WS on this machine.
    // Must be set via ModuleInitializer — App.OnStartup is too late (D3D already initialized).
    // NOTE: RenderMode lives in System.Windows.Interop, not System.Windows.Media.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
