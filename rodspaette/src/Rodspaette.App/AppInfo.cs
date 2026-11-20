using System.Reflection;

namespace Rodspaette.App;

public static class AppInfo
{
    /// <summary>The version as shown to the user, e.g. "1.01" (from InformationalVersion in the project file).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString() ?? "?";
}
