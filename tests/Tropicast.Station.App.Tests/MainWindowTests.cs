using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;

namespace Tropicast.Station.App.Tests;

public sealed class MainWindowTests
{
    [AvaloniaFact]
    public void Host_resolves_and_shows_main_window()
    {
        using var host = AppHost.Create([]);

        var window = host.Services.GetRequiredService<MainWindow>();
        window.Show();

        Assert.True(window.IsVisible);
        Assert.IsType<MainViewModel>(window.DataContext);
        Assert.Equal("Tropicast Station", window.Title);
        Assert.Equal("v0.1.0", window.FindControl<TextBlock>("VersionText")?.Text);

        window.Close();
    }

    [Fact]
    public void Host_container_is_valid()
    {
        using var host = AppHost.Create([]);

        Assert.Same(
            host.Services.GetRequiredService<MainViewModel>(),
            host.Services.GetRequiredService<MainViewModel>());
    }
}
