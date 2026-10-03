using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Core;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.App;

/// <summary>Composition root: configuration, logging and dependency injection.</summary>
internal static class AppHost
{
    public static IHost Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddStationCore()
            .AddStationAudio()
            .AddStationEncoding()
            .AddStationInfrastructure();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<ProfileEditorViewModel>();
        builder.Services.AddTransient(sp => new MainWindow
        {
            DataContext = sp.GetRequiredService<MainViewModel>(),
        });
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }
}
