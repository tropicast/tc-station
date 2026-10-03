using Avalonia;
using Avalonia.Headless;
using Tropicast.Station.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestAppBuilder))]

namespace Tropicast.Station.App.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
