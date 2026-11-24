using System;
using System.Windows;

namespace WellenEditor;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // Absturzursache protokollieren statt still zu sterben: in Datei + Debug-Ausgabe.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("AppDomain.UnhandledException", (Exception)args.ExceptionObject);
        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // verhindert sofortigen Absturz, damit der Dialog lesbar bleibt
        };
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            var dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaveBenchPro");
            System.IO.Directory.CreateDirectory(dir);
            var path = System.IO.Path.Combine(dir, "crash.log");
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss} {kind} ===");
            var cur = ex;
            while (cur != null)
            {
                sb.AppendLine($"Typ:       {cur.GetType().FullName}");
                sb.AppendLine($"Nachricht: {cur.Message}");
                sb.AppendLine($"Stack:\n{cur.StackTrace}");
                cur = cur.InnerException;
                if (cur != null) sb.AppendLine("--- inner ---");
            }
            sb.AppendLine();
            System.IO.File.AppendAllText(path, sb.ToString());
            System.Diagnostics.Debug.WriteLine(sb.ToString());
        }
        catch { }
    }
}
