using System.Reflection;

namespace NekoPlayer.App;

public static class AppVersionInfo
{
    // The project Version property supplies the generated assembly metadata.
    public static string Version { get; } = typeof(AppVersionInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppVersionInfo).Assembly.GetName().Version?.ToString(3)
        ?? "unknown";

    public static string DisplayVersion => $"v{Version}";
}
