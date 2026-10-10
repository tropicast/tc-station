using Tropicast.Station.Core;

namespace Tropicast.Station.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    public MainViewModel(IAppInfo appInfo, AccountViewModel account, ProfileEditorViewModel profiles, AudioDevicesViewModel audio,
        BroadcastViewModel broadcast)
    {
        ArgumentNullException.ThrowIfNull(appInfo);
        Title = appInfo.ProductName;
        Version = $"v{appInfo.Version}";
        Account = account;
        Profiles = profiles;
        Audio = audio;
        Broadcast = broadcast;
    }

    public string Title { get; }

    public string Version { get; }

    public AccountViewModel Account { get; }
    public ProfileEditorViewModel Profiles { get; }
    public AudioDevicesViewModel Audio { get; }
    public BroadcastViewModel Broadcast { get; }
}
