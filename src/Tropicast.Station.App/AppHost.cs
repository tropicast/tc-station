using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Audio.Linux;
using Tropicast.Station.Audio.MacOS;
using Tropicast.Station.Audio.Windows;
using Tropicast.Station.Core;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.App;

/// <summary>Composition root: configuration, logging and dependency injection.</summary>
internal static class AppHost
{
    public static IHost Create(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var demoAudio = args.Contains("--demo-audio", StringComparer.Ordinal);
        var builder = Host.CreateApplicationBuilder(args.Where(a => a != "--demo-audio").ToArray());
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new SafeLogProvider(SafeLogProvider.DefaultDirectory, Console.Error));
        builder.Services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<SafeLogProvider>());
        if (OperatingSystem.IsWindows() && !demoAudio)
        {
            builder.Services.AddWindowsAudioCapture();
        }
        else if (OperatingSystem.IsLinux() && !demoAudio)
        {
            builder.Services.AddLinuxAudioCapture();
        }
        else if (OperatingSystem.IsMacOS() && !demoAudio)
        {
            builder.Services.AddMacAudioCapture();
        }

        builder.Services
            .AddStationCore()
            .AddStationAudio(demoAudio)
            .AddStationEncoding()
            .AddStationInfrastructure(ApiBaseUrl(builder.Configuration["Tropicast:ApiBaseUrl"]));

        builder.Services.AddSingleton<MainViewModel>();
        builder.Services.AddSingleton<AccountViewModel>();
        builder.Services.AddSingleton<IExternalBrowser, ExternalBrowser>();
        builder.Services.AddSingleton<ProfileEditorViewModel>();
        builder.Services.AddSingleton<AudioDevicesViewModel>();
        builder.Services.AddSingleton<AudioLevelsViewModel>();
        builder.Services.AddSingleton<BroadcastViewModel>();
        builder.Services.AddSingleton<IBroadcastConfirmation, BroadcastConfirmation>();
        builder.Services.AddSingleton<Diagnostics>();
        builder.Services.AddSingleton<UnhandledErrors>();
        builder.Services.AddTransient(sp =>
        {
            var window = new MainWindow
            {
                DataContext = sp.GetRequiredService<MainViewModel>(),
                Diagnostics = sp.GetRequiredService<Diagnostics>(),
            };
            if (sp.GetRequiredService<IBroadcastConfirmation>() is BroadcastConfirmation confirmation)
            {
                confirmation.Owner = window;
            }
            return window;
        });
        configureServices?.Invoke(builder.Services);

        return builder.Build();
    }

    /// <summary>
    /// The Tropicast API address: production unless the <c>Tropicast:ApiBaseUrl</c> setting (e.g. the environment
    /// variable <c>Tropicast__ApiBaseUrl=http://localhost:8080</c>) names another one.
    /// </summary>
    private static Uri? ApiBaseUrl(string? setting)
        => string.IsNullOrWhiteSpace(setting) ? null
            : Uri.TryCreate(setting, UriKind.Absolute, out var url) && Infrastructure.HttpDesktopApi.IsAllowed(url) ? url
            : throw new InvalidOperationException("Tropicast:ApiBaseUrl must be an HTTPS address (or HTTP on this computer).");
}
