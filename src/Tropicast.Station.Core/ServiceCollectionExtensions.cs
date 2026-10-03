using Microsoft.Extensions.DependencyInjection;

namespace Tropicast.Station.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers domain services shared by every layer.</summary>
    public static IServiceCollection AddStationCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAppInfo, AppInfo>();
        return services;
    }
}
