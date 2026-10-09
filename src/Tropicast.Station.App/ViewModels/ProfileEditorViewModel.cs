using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.Sockets;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.App.ViewModels;

public sealed partial class ProfileEditorViewModel(
    ProfileService service, IConnectionTester tester, ILogger<ProfileEditorViewModel> logger) : ViewModelBase
{
    private Guid _id = Guid.NewGuid();
    private bool _hasStoredPassword;
    private static readonly HashSet<string> InputProperties =
    [
        nameof(Name), nameof(Host), nameof(Port), nameof(Mount), nameof(Username), nameof(UseTls), nameof(ContentType), nameof(Password),
        nameof(BitrateKbps), nameof(SampleRate), nameof(Channels), nameof(StreamName), nameof(StreamDescription), nameof(StreamGenre), nameof(StreamUrl),
        nameof(PublishOpus), nameof(OpusBitrateKbps),
    ];

    public ObservableCollection<ConnectionProfile> Profiles { get; } = [];
    public IReadOnlyList<string> ContentTypes { get; } = ["audio/mpeg", "audio/aac", "audio/ogg"];
    public IReadOnlyList<int> Bitrates { get; } = [64, 96, 128, 192, 320];
    public IReadOnlyList<int> OpusBitrates { get; } = [48, 64, 96];
    public IReadOnlyList<int> SampleRates { get; } = [44100, 48000];
    public IReadOnlyList<int> ChannelCounts { get; } = [1, 2];

    [ObservableProperty] public partial ConnectionProfile? SelectedProfile { get; set; }
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Host { get; set; } = "localhost";
    [ObservableProperty] public partial string Port { get; set; } = "8000";
    [ObservableProperty] public partial string Mount { get; set; } = "/live.mp3";
    [ObservableProperty] public partial string Username { get; set; } = "source";
    [ObservableProperty] public partial bool UseTls { get; set; }
    [ObservableProperty] public partial string ContentType { get; set; } = "audio/mpeg";
    [ObservableProperty] public partial string Password { get; set; } = "";
    [ObservableProperty] public partial int BitrateKbps { get; set; } = 128;
    [ObservableProperty] public partial int SampleRate { get; set; } = 44100;
    [ObservableProperty] public partial int Channels { get; set; } = 2;
    [ObservableProperty] public partial string StreamName { get; set; } = "";
    [ObservableProperty] public partial string StreamDescription { get; set; } = "";
    [ObservableProperty] public partial string StreamGenre { get; set; } = "";
    [ObservableProperty] public partial string StreamUrl { get; set; } = "";
    [ObservableProperty] public partial bool PublishOpus { get; set; }
    [ObservableProperty] public partial int OpusBitrateKbps { get; set; } = 64;
    [ObservableProperty] public partial string ValidationMessage { get; set; } = "Enter a profile name and source password.";
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsBroadcastLocked { get; set; }

    public bool CanManage => !IsBusy && !IsBroadcastLocked;
    public string PasswordHint => _hasStoredPassword ? "Password stored securely. Leave blank to keep it." : "Source password is required.";
    public string TransportWarning => UseTls ? "" : "TLS is off: source credentials and audio will cross the network unencrypted.";
    private bool CanSave => CanManage && ValidationMessage.Length == 0;
    private bool CanSelect => CanManage && SelectedProfile is not null;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not null && InputProperties.Contains(e.PropertyName))
        {
            Validate();
            OnPropertyChanged(nameof(TransportWarning));
        }

        if (e.PropertyName is nameof(IsBusy) or nameof(IsBroadcastLocked) or nameof(SelectedProfile) or nameof(ValidationMessage))
        {
            SaveCommand.NotifyCanExecuteChanged();
            TestCommand.NotifyCanExecuteChanged();
            EditCommand.NotifyCanExecuteChanged();
            DeleteCommand.NotifyCanExecuteChanged();
            NewCommand.NotifyCanExecuteChanged();
            LoadCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanManage));
        }
    }

    private ConnectionProfile Draft() => new(_id, Name.Trim(), Host.Trim().Trim('[', ']'),
        int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0,
        Mount.Trim(), Username.Trim(), UseTls, ContentType, BitrateKbps, SampleRate, Channels,
        StreamName.Trim(), StreamDescription.Trim(), StreamGenre.Trim(), StreamUrl.Trim(), PublishOpus, OpusBitrateKbps);

    private void Validate()
    {
        var errors = ProfileValidator.Validate(Draft()).ToList();
        if (Password.Length > 0 ? !ProfileValidator.IsValidPassword(Password) : !_hasStoredPassword)
        {
            errors.Add("Enter a password of 1–256 characters without control characters.");
        }

        if (Profiles.Any(p => p.Id != _id && p.Name.Equals(Name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Another profile already uses this name.");
        }

        ValidationMessage = string.Join(Environment.NewLine, errors);
    }

    [RelayCommand(CanExecute = nameof(CanManage))]
    private async Task LoadAsync() => await RunAsync(async () =>
    {
        await RefreshAsync();
        Status = Profiles.Count == 0 ? "Create your first connection profile." : "Select a profile and press Edit.";
    });

    [RelayCommand(CanExecute = nameof(CanManage))]
    private void New()
    {
        _id = Guid.NewGuid();
        _hasStoredPassword = false;
        SelectedProfile = null;
        Name = "";
        Host = "localhost";
        Port = "8000";
        Mount = "/live.mp3";
        Username = "source";
        UseTls = false;
        ContentType = "audio/mpeg";
        BitrateKbps = 128;
        SampleRate = 44100;
        Channels = 2;
        StreamName = "";
        StreamDescription = "";
        StreamGenre = "";
        StreamUrl = "";
        PublishOpus = false;
        OpusBitrateKbps = 64;
        Password = "";
        Status = "New profile.";
        OnPropertyChanged(nameof(PasswordHint));
        Validate();
    }

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private async Task EditAsync() => await RunAsync(async () =>
    {
        var profile = SelectedProfile!;
        var hasPassword = await service.HasPasswordAsync(profile.Id);
        _id = profile.Id;
        _hasStoredPassword = hasPassword;
        Name = profile.Name;
        Host = profile.Host;
        Port = profile.Port.ToString(CultureInfo.InvariantCulture);
        Mount = profile.Mount;
        Username = profile.Username;
        UseTls = profile.UseTls;
        ContentType = profile.ContentType;
        BitrateKbps = profile.BitrateKbps;
        SampleRate = profile.SampleRate;
        Channels = profile.Channels;
        StreamName = profile.StreamName;
        StreamDescription = profile.StreamDescription;
        StreamGenre = profile.StreamGenre;
        StreamUrl = profile.StreamUrl;
        PublishOpus = profile.PublishOpus;
        OpusBitrateKbps = profile.OpusBitrateKbps;
        Password = "";
        Status = "Editing saved profile.";
        OnPropertyChanged(nameof(PasswordHint));
        Validate();
    });

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() => await RunAsync(async () =>
    {
        await service.SaveAsync(Draft(), Password.Length == 0 ? null : Password);
        Password = "";
        _hasStoredPassword = true;
        await RefreshAsync();
        SelectedProfile = Profiles.First(p => p.Id == _id);
        OnPropertyChanged(nameof(PasswordHint));
        Validate();
        Status = "Profile saved. Password is in the OS credential store.";
    });

    [RelayCommand(CanExecute = nameof(CanSave), IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken cancellationToken) => await RunAsync(async () =>
    {
        var profile = Draft();
        string? password = Password.Length == 0 ? null : Password;
        if (password is null)
        {
            // Never load a stored password into an observable/UI property.
            password = await service.GetPasswordAsync(profile.Id, cancellationToken);
            if (!ProfileValidator.IsValidPassword(password))
            {
                throw new InvalidOperationException("The stored password is missing. Enter a new password.");
            }
        }

        var result = await tester.TestAsync(new BroadcastTarget(profile, password!), cancellationToken);
        Status = result.Message;
    });

    [RelayCommand(CanExecute = nameof(CanSelect))]
    private async Task DeleteAsync() => await RunAsync(async () =>
    {
        await service.DeleteAsync(SelectedProfile!.Id);
        await RefreshAsync();
        New();
        Status = "Profile and password deleted.";
    });

    private async Task RefreshAsync()
    {
        var loaded = await service.LoadAsync();
        Profiles.Clear();
        foreach (var profile in loaded.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            Profiles.Add(profile);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        Status = "Working…";
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            Status = "Operation cancelled.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecretStoreException or ArgumentException
            or InvalidOperationException or AggregateException or SocketException)
        {
            // Do not log exception text: it can contain native diagnostics or secret values.
            LogOperationFailed(logger, ex.GetType().Name);
            Status = ex is SecretStoreException ? ex.Message
                : ex is ArgumentException ? "The profile is invalid or uses a duplicate name. Check its fields and password."
                : ex is InvalidOperationException ? ex.Message
                : "Could not complete the operation. Check profiles.json for corruption or write-permission problems and check the OS credential store.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Profile operation failed ({ErrorType}); sensitive diagnostics omitted")]
    private static partial void LogOperationFailed(ILogger logger, string errorType);

}
