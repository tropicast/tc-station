using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Core;
using Tropicast.Station.Encoding;

namespace Tropicast.Station.App;

/// <summary>Composition root: configuration, logging and dependency injection.</summary>
internal static class AppHost
{
    public static IHost Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services
            .AddStationCore()
            .AddStationAudio()
            .AddStationEncoding();

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddTransient(sp => new MainWindow
        {
            DataContext = sp.GetRequiredService<MainViewModel>(),
        });

        return builder.Build();
    }
}
