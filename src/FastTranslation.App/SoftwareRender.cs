namespace FastTranslation.App;

internal static class SoftwareRender
{
    // M0 D1：D3D 渲染路径的私有工作集约 77MB；在 App.OnStartup 里设 SoftwareOnly 太晚
    //（D3D 在那之前就已初始化）。ModuleInitializer 是最早的挂载点。
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Init()
    {
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
