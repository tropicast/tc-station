using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tropicast.Station.Audio;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers audio capture providers. Platform adapters are added by later MVP issues.</summary>
    public static IServiceCollection AddStationAudio(this IServiceCollection services, bool demo = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (demo)
        {
            services.TryAddSingleton<IAudioCaptureProvider, ToneAudioCaptureProvider>();
        }
        else
        {
            services.TryAddSingleton<IAudioCaptureProvider, UnavailableAudioCaptureProvider>();
        }

        services.TryAddSingleton<AudioCaptureService>();
        return services;
    }
}
