using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers domain services shared by every layer.</summary>
    public static IServiceCollection AddStationCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IAppInfo, AppInfo>();
        services.AddSingleton<ProfileService>();
        services.AddSingleton<IBroadcastTargetProvider, ManualBroadcastTargetProvider>();
        return services;
    }
}
