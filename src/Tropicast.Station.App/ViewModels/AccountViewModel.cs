using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tropicast.Station.Core.Account;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.App.ViewModels;

/// <summary>Sign-in with the Tropicast account (device code) and the station to broadcast to.</summary>
public sealed partial class AccountViewModel : ViewModelBase, IDisposable
{
    private readonly AccountService _account;
    private readonly IExternalBrowser _browser;
    private Uri? _signInPage;
    private bool _syncing;

    public AccountViewModel(AccountService account, IExternalBrowser browser)
    {
        _account = account;
        _browser = browser;
        account.Changed += OnAccountChanged;
    }

    public ObservableCollection<AccountStation> Stations { get; } = [];

    [ObservableProperty] public partial AccountStation? SelectedStation { get; set; }
    [ObservableProperty] public partial bool IsSignedIn { get; set; }
    [ObservableProperty] public partial bool IsSigningIn { get; set; }
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsBroadcastLocked { get; set; }
    [ObservableProperty] public partial string UserCode { get; set; } = "";
    [ObservableProperty] public partial string Status { get; set; } = "Sign in to broadcast to your Tropicast stations.";

    public bool IsSignedOut => !IsSignedIn && !IsSigningIn;
    public bool HasNoStations => IsSignedIn && Stations.Count == 0;
    public bool CanChooseStation => IsSignedIn && !IsBusy && !IsBroadcastLocked;
    private bool CanSignIn => !IsSignedIn && !IsSigningIn && !IsBusy;
    private bool CanUseAccount => IsSignedIn && !IsBusy && !IsBroadcastLocked;

    [RelayCommand]
    private async Task LoadAsync()
    {
        await RunAsync(async () =>
        {
            await _account.LoadAsync();
            if (_account.IsSignedIn)
            {
                await RefreshQuietlyAsync();
            }
        });
    }

    [RelayCommand(CanExecute = nameof(CanSignIn), IncludeCancelCommand = true)]
    private async Task SignInAsync(CancellationToken cancellationToken)
    {
        IsSigningIn = true;
        Status = "Starting sign-in…";
        try
        {
            await _account.SignInAsync(DeviceName(), request =>
            {
                UserCode = request.UserCode;
                _signInPage = request.VerificationUriComplete;
                Status = $"Approve the code {request.UserCode} in your browser at {request.VerificationUri}.";
                OpenSignInPage();
                return Task.CompletedTask;
            }, cancellationToken);
            Status = Stations.Count == 0 ? "Signed in. This account has no station yet: create one on the web." : "Signed in.";
        }
        catch (OperationCanceledException)
        {
            Status = "Sign-in cancelled.";
        }
        catch (Exception ex) when (ex is DesktopApiException or SecretStoreException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
            UserCode = "";
            _signInPage = null;
        }
    }

    [RelayCommand]
    private void OpenSignInPage()
    {
        if (_signInPage is { } page)
        {
            try
            {
                _browser.Open(page);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
            {
                Status = $"Open {page} in your browser and approve the code {UserCode}.";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseAccount))]
    private async Task RefreshStationsAsync()
        => await RunAsync(async () =>
        {
            await _account.RefreshStationsAsync();
            Status = Stations.Count == 0 ? "This account has no station yet: create one on the web." : "Station list updated.";
        });

    [RelayCommand(CanExecute = nameof(CanUseAccount))]
    private async Task SignOutAsync()
        => await RunAsync(async () =>
        {
            await _account.SignOutAsync();
            Status = "Signed out. Station passwords were removed from this computer.";
        });

    partial void OnSelectedStationChanged(AccountStation? value)
    {
        if (!_syncing && value is not null && value.StationId != _account.SelectedStationId)
        {
            _ = RunAsync(() => _account.SelectStationAsync(value.StationId));
        }
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsSignedIn) or nameof(IsSigningIn) or nameof(IsBusy) or nameof(IsBroadcastLocked))
        {
            OnPropertyChanged(nameof(IsSignedOut));
            OnPropertyChanged(nameof(HasNoStations));
            OnPropertyChanged(nameof(CanChooseStation));
            SignInCommand.NotifyCanExecuteChanged();
            RefreshStationsCommand.NotifyCanExecuteChanged();
            SignOutCommand.NotifyCanExecuteChanged();
        }
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await _account.RefreshStationsAsync();
        }
        catch (DesktopApiException ex) when (ex.Error == DesktopApiError.Unavailable)
        {
            Status = "Offline: showing the stations from the last session.";
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is DesktopApiException or SecretStoreException)
        {
            Status = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void OnAccountChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            Sync();
        }
        else
        {
            Dispatcher.UIThread.Post(Sync);
        }
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            IsSignedIn = _account.IsSignedIn;
            Stations.Clear();
            foreach (var station in _account.Stations)
            {
                Stations.Add(station);
            }
            SelectedStation = Stations.FirstOrDefault(s => s.StationId == _account.SelectedStationId);
            OnPropertyChanged(nameof(HasNoStations));
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The device's name in the account and on its broadcast credentials.</summary>
    private static string DeviceName()
    {
        var name = Environment.MachineName.Trim();
        return name.Length == 0 ? "Tropicast Station" : name.Length > 64 ? name[..64] : name;
    }

    public void Dispose()
    {
        _account.Changed -= OnAccountChanged;
        GC.SuppressFinalize(this);
    }
}
