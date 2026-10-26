using System.IO;
using System.Windows;

namespace Probe;

public partial class App : System.Windows.Application
{
    public static readonly string LogPath = Path.Combine(AppContext.BaseDirectory, "probe.log");

    public static void FileLog(string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}"); }
        catch { /* probe-only */ }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += (_, args) =>
        {
            FileLog($"DISPATCHER UNHANDLED: {args.Exception}");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            FileLog($"APPDOMAIN UNHANDLED: {args.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            FileLog($"UNOBSERVED TASK: {args.Exception}");
            args.SetObserved();
        };
        base.OnStartup(e);
    }
}
