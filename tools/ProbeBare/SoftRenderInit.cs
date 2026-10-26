namespace ProbeBare;
internal static class SoftRenderInit
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
