using System.Reflection;

namespace Tropicast.Station.Core;

internal sealed class AppInfo : IAppInfo
{
    public AppInfo()
        : this(Assembly.GetEntryAssembly() ?? typeof(AppInfo).Assembly)
    {
    }

    internal AppInfo(Assembly assembly)
    {
        ProductName = assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product ?? "Tropicast Station";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        // Drop SourceLink commit metadata ("1.2.3+sha") for display.
        Version = informational?.Split('+')[0] ?? assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    public string ProductName { get; }

    public string Version { get; }
}
