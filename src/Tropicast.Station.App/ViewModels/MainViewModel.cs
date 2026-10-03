using Tropicast.Station.Core;

namespace Tropicast.Station.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public MainViewModel(IAppInfo appInfo, ProfileEditorViewModel profiles, AudioDevicesViewModel audio)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        Title = appInfo.ProductName;
        Version = $"v{appInfo.Version}";
        Profiles = profiles;
        Audio = audio;
    }

    public string Title { get; }

    public string Version { get; }

    public ProfileEditorViewModel Profiles { get; }
    public AudioDevicesViewModel Audio { get; }
}
