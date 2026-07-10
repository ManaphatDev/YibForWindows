using System.Threading;

namespace Yib;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, "Yib.SingleInstance.Mutex", out bool createdNew);
        if (!createdNew)
        {
            return;
        }

        // Without these, any unhandled exception (on the UI thread or a background thread)
        // tears the process down and the tray icon just vanishes - the "app หลุด" the user
        // reported. Catch them, log, and keep running where the runtime lets us.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => LogError(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogError(e.ExceptionObject as Exception);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }

    private static string LogFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Yib", "error.log");

    // Append-only crash log. Best-effort: logging must never itself throw and take down
    // the handler, so its own failures are swallowed.
    internal static void LogError(Exception? ex)
    {
        if (ex is null)
        {
            return;
        }

        try
        {
            string path = LogFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Nothing we can do if logging fails; keep the app alive regardless.
        }
    }
}
