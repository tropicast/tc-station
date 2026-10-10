using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Tropicast.Station.Core.Account;
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
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<AccountService>();
        services.AddSingleton<ManualBroadcastTargetProvider>();
        services.AddSingleton<IBroadcastTargetProvider, AccountBroadcastTargetProvider>();
        return services;
    }
}
