using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Audio.Linux;
using Tropicast.Station.Audio.MacOS;
using Tropicast.Station.Audio.Windows;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Tests;

namespace Tropicast.Station.App.Tests;

public sealed class AudioDevicesTests
{
    [AvaloniaFact]
    public async Task Picker_groups_sources_and_active_removal_clears_selection_and_shows_error()
    {
        var provider = new ToneAudioCaptureProvider();
        using var host = AppHost.Create([], services =>
        {
            services.AddSingleton<IAudioCaptureProvider>(provider);
            services.AddSingleton<IProfileStore, MemoryProfiles>();
            services.AddSingleton<ISecretStore, MemorySecrets>();
        });
        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();
        var model = host.Services.GetRequiredService<AudioDevicesViewModel>();
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Single(model.Inputs);
        Assert.Single(model.Outputs);
        var view = window.FindControl<AudioDevicesView>("BroadcastAudioDevices")!;
        Assert.Single(view.FindControl<ComboBox>("InputPicker")!.Items);
        Assert.Single(view.FindControl<ComboBox>("OutputPicker")!.Items);
        Assert.False(model.StartCommand.CanExecute(null));
        model.SelectedInput = model.Inputs[0];
        model.SelectedOutput = model.Outputs[0];
        Assert.Null(model.SelectedInput);
        model.SelectedInput = model.Inputs[0];
        Assert.Null(model.SelectedOutput);
        await model.StartCommand.ExecuteAsync(null);
        Assert.True(model.IsCapturing);
        Assert.False(model.CanChoose);
        provider.SetDevices([model.Outputs[0]]);
        await model.RefreshCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.IsCapturing);
        Assert.Null(model.SelectedInput);
        Assert.Null(model.SelectedOutput);
        Assert.Contains("disconnect", model.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.StartCommand.CanExecute(null));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Renamed_device_preserves_selection_by_stable_id()
    {
        var provider = new ToneAudioCaptureProvider();
        using var host = AppHost.Create([], s => s.AddSingleton<IAudioCaptureProvider>(provider));
        var model = host.Services.GetRequiredService<AudioDevicesViewModel>();
        await model.RefreshCommand.ExecuteAsync(null);
        model.SelectedInput = model.Inputs[0];
        var id = model.SelectedInput.Id;
        provider.SetDevices([model.SelectedInput with { DisplayName = "Renamed mixer", IsDefault = false }]);
        await model.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(id, model.SelectedInput?.Id);
        Assert.Equal("Renamed mixer", model.SelectedInput?.DisplayName);
    }

    [Fact]
    public void Demo_is_explicit_and_does_not_replace_an_injected_platform_adapter()
    {
        using var normal = AppHost.Create([]);
        if (OperatingSystem.IsWindows())
        {
            Assert.IsType<WindowsAudioCaptureProvider>(normal.Services.GetRequiredService<IAudioCaptureProvider>());
        }
        else if (OperatingSystem.IsLinux())
        {
            Assert.IsType<LinuxAudioCaptureProvider>(normal.Services.GetRequiredService<IAudioCaptureProvider>());
        }
        else if (OperatingSystem.IsMacOS())
        {
            Assert.IsType<MacAudioCaptureProvider>(normal.Services.GetRequiredService<IAudioCaptureProvider>());
        }
        else
        {
            Assert.IsType<UnavailableAudioCaptureProvider>(normal.Services.GetRequiredService<IAudioCaptureProvider>());
        }
        using var demo = AppHost.Create(["--demo-audio"]);
        Assert.IsType<ToneAudioCaptureProvider>(demo.Services.GetRequiredService<IAudioCaptureProvider>());
    }
}
