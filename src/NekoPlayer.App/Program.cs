using Avalonia;
using Avalonia.Controls;
using NekoPlayer.App.Services;
using NekoPlayer.Infrastructure.Configuration;

namespace NekoPlayer.App;

public static class Program
{
    public static SingleInstanceCoordinator? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var exitForUpdate = args.Contains("--exit-for-update", StringComparer.Ordinal);
        if (exitForUpdate && args.Length != 1) return (int)InstanceExitCode.InvalidArguments;
        try
        {
            using var instance = new SingleInstanceCoordinator(new UserDataPaths().Root);
            if (exitForUpdate)
                return (int)instance.ExitExistingForUpdateAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
            if (!instance.IsPrimary)
                return (int)instance.ActivateExistingAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            Instance = instance;
            try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown); }
            finally { Instance = null; }
        }
        catch (Exception) { return (int)InstanceExitCode.IpcError; }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
