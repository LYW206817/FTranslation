namespace FastTranslation.App;

internal static class SoftwareRender
{
    // M0 D1: the D3D path costs ~77MB private WS; SoftwareOnly set in App.OnStartup is
    // TOO LATE (D3D is initialized before that). ModuleInitializer is the earliest hook.
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
