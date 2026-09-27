using System.Reflection;

namespace AiGoofish.Launcher.App;

internal static class LauncherVersion
{
    public static string Current { get; } = typeof(LauncherVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown";
}
