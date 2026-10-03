using Microsoft.Extensions.DependencyInjection;

namespace Tropicast.Station.Audio;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers audio capture providers. Platform adapters are added by later MVP issues.</summary>
    public static IServiceCollection AddStationAudio(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
