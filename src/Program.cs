using Avalonia;

namespace PosturePotato;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Build helper: write the app icon from the drawn potato.
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            File.WriteAllBytes(args[1], IconFactory.BuildIco(IconFactory.AppSizes));
            return 0;
        }

        AppDomain.CurrentDomain.UnhandledException += (_, a) => Log.Write($"crash: {a.ExceptionObject}");
        TaskScheduler.UnobservedTaskException += (_, a) => Log.Write($"unobserved task error: {a.Exception}");

        Log.Trim();
        var version = typeof(Program).Assembly.GetName().Version?.ToString(3);
        Log.Write($"start  v{version} on {Platform.Describe()}  args=[{string.Join(' ', args)}]  " +
                  $"cwd={Environment.CurrentDirectory}  since boot={TimeSpan.FromMilliseconds(Environment.TickCount64):h\\:mm\\:ss}");

        // Single instance: a second launch just opens Settings in the running one.
        using var instance = SingleInstance.TryClaim();
        if (instance == null)
        {
            Log.Write("already running: asked that instance to open Settings, exiting");
            return 0;
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    // Also used by the Avalonia previewer.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Two small windows at 12 fps don't need the GPU. CPU rendering sidesteps OpenGL
            // driver trouble on Linux (stalled frames, black transparent windows).
            .With(new X11PlatformOptions { RenderingMode = new[] { X11RenderingMode.Software } })
            .WithInterFont()
            .LogToTrace();
}
