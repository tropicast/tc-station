using Microsoft.Extensions.DependencyInjection;

namespace Tropicast.Station.Encoding;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers the encoder pipeline. FFmpeg supervision is added by a later MVP issue.</summary>
    public static IServiceCollection AddStationEncoding(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services;
    }
}
