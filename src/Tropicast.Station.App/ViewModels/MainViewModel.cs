using Tropicast.Station.Core;

namespace Tropicast.Station.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public MainViewModel(IAppInfo appInfo)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        Title = appInfo.ProductName;
        Version = $"v{appInfo.Version}";
    }

    public string Title { get; }

    public string Version { get; }
}
