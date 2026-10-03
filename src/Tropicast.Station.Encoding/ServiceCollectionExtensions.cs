using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tropicast.Station.Encoding;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the MP3 encoder; credentials are handled by the managed Icecast publisher.</summary>
    public static IServiceCollection AddStationEncoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IBroadcastEncoder, FfmpegBroadcastEncoder>();
        return services;
    }
}
