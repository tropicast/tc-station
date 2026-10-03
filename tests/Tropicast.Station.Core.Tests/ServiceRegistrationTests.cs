using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.Audio;
using Tropicast.Station.Encoding;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Core.Tests;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void All_layers_register_and_validate()
    {
        var services = new ServiceCollection()
            .AddStationCore()
            .AddStationAudio()
            .AddStationEncoding();
        services.AddSingleton<IProfileStore, MemoryProfiles>();
        services.AddSingleton<ISecretStore, MemorySecrets>();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

        Assert.IsType<AppInfo>(provider.GetRequiredService<IAppInfo>());
        Assert.Same(provider.GetRequiredService<IAppInfo>(), provider.GetRequiredService<IAppInfo>());
    }

    [Fact]
    public void Registration_rejects_null_collection()
    {
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddStationCore());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddStationAudio());
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddStationEncoding());
    }
}
