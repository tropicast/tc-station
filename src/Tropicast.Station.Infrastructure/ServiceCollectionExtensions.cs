using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStationInfrastructure(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IProfileStore>(_ => new JsonProfileStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Tropicast", "Station")));
        services.AddSingleton<ISecretStore, OsSecretStore>();
        services.AddSingleton<IConnectionTester, TropicastConnectionTester>();
        return services;
    }
}
