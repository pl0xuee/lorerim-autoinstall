using Avalonia;
using System;
using Lorerim.Gui.Services.Nexus;

namespace Lorerim.Gui;

sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // OAuth callback: the browser launches a second instance with the jackify:// URL.
        // Forward it to the running instance over the unix socket and exit.
        var callbackUrl = Array.Find(
            args,
            a => a.StartsWith($"{ProtocolHandlerRegistrar.Scheme}://", StringComparison.OrdinalIgnoreCase)
        );
        if (callbackUrl is not null && OAuthCallbackListener.TrySend(callbackUrl))
        {
            return;
        }

        // Last-resort backstop: log unhandled async/background exceptions to the persistent
        // log instead of dying silently. Individual commands still handle their own errors;
        // this only catches paths that slip through.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            TryLogCrash((e.ExceptionObject as Exception)?.ToString() ?? "unknown");
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            TryLogCrash(e.Exception.ToString());
            e.SetObserved();
        };
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static void TryLogCrash(string detail)
    {
        try
        {
            var dir = System.IO.Path.Join(Models.AppSettings.AppDataPath, "logs");
            System.IO.Directory.CreateDirectory(dir);
            System.IO.File.AppendAllText(
                System.IO.Path.Join(dir, "lorerim-autoinstall.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] UNHANDLED: {detail}\n"
            );
        }
        catch
        {
            // nothing more we can do
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}
