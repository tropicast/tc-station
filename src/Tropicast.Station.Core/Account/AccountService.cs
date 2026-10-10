using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Core.Account;

/// <summary>
/// The Tropicast account of this device: device-code sign-in, tokens, the user's stations and their broadcast
/// targets. The refresh token and the station passwords live only in the OS secret store; the access token only in
/// memory; stations and targets (no secrets) in <see cref="IAccountStore"/>.
/// </summary>
public sealed class AccountService(IDesktopApi api, IAccountStore store, ISecretStore secrets, TimeProvider time) : IDisposable
{
    /// <summary>Secret-store key of this device's refresh token. Station passwords are keyed by station ID.</summary>
    public static readonly Guid RefreshTokenKey = new("5e0c7d1e-2b7a-4f0e-9a51-3d7c4b8a9f10");

    /// <summary>Refresh the access token this long before it expires.</summary>
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(1);

    private readonly SemaphoreSlim _tokenGate = new(1, 1);
    private AccountData _data = AccountData.Empty;
    private string? _accessToken;
    private DateTimeOffset _accessExpiresAt;

    public bool IsSignedIn { get; private set; }

    public IReadOnlyList<AccountStation> Stations => _data.Stations;

    public Guid? SelectedStationId => _data.SelectedStationId;

    public event EventHandler? Changed;

    /// <summary>Restores the state of the last run. Signed in when a refresh token is stored.</summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsSignedIn = await secrets.GetAsync(RefreshTokenKey, cancellationToken).ConfigureAwait(false) is not null;
        _data = IsSignedIn ? await store.LoadAsync(cancellationToken).ConfigureAwait(false) : AccountData.Empty;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Whether this ID is one of the account's stations (and not a manual profile).</summary>
    public bool IsStation(Guid id) => IsSignedIn && _data.Stations.Any(s => s.StationId == id);

    /// <summary>
    /// Signs this device in: <paramref name="show"/> displays the user code and opens the page; then polls until the
    /// user approves or denies it in the browser, or the code expires.
    /// </summary>
    public async Task SignInAsync(string deviceName, Func<DeviceSignIn, Task> show, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(show);
        var request = await api.StartSignInAsync(deviceName, cancellationToken).ConfigureAwait(false);
        await show(request).ConfigureAwait(false);
        var deadline = time.GetUtcNow() + request.ExpiresIn;
        var interval = request.Interval;
        while (true)
        {
            await Task.Delay(interval, time, cancellationToken).ConfigureAwait(false);
            if (time.GetUtcNow() >= deadline)
            {
                throw new DesktopApiException(DesktopApiError.Unauthorized, "The code expired. Sign in again.");
            }
            SignInPoll poll;
            try
            {
                poll = await api.PollSignInAsync(request.DeviceSecret, cancellationToken).ConfigureAwait(false);
            }
            catch (DesktopApiException ex) when (ex.Error == DesktopApiError.Unavailable)
            {
                continue;
            }
            switch (poll.Status)
            {
                case SignInPollStatus.Approved:
                    await StoreTokensAsync(poll.Tokens!, cancellationToken).ConfigureAwait(false);
                    IsSignedIn = true;
                    await RefreshStationsAsync(cancellationToken).ConfigureAwait(false);
                    return;
                case SignInPollStatus.Pending:
                    break;
                case SignInPollStatus.SlowDown:
                    interval += TimeSpan.FromSeconds(5);
                    break;
                case SignInPollStatus.Denied:
                    throw new DesktopApiException(DesktopApiError.Forbidden, "The sign-in was refused in the browser.");
                default:
                    throw new DesktopApiException(DesktopApiError.Unauthorized, "The code expired. Sign in again.");
            }
        }
    }

    /// <summary>Reloads the stations from the API, keeping the selection when the station is still there.</summary>
    public async Task RefreshStationsAsync(CancellationToken cancellationToken = default)
    {
        var stations = await WithAccessTokenAsync(token => api.GetStationsAsync(token, cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var ids = stations.Select(s => s.StationId).ToHashSet();
        foreach (var gone in _data.Targets.Where(t => !ids.Contains(t.StationId)))
        {
            await secrets.DeleteAsync(gone.StationId, cancellationToken).ConfigureAwait(false);
        }
        Guid? selected = _data.SelectedStationId is { } id && ids.Contains(id) ? id : stations.Count > 0 ? stations[0].StationId : (Guid?)null;
        await SaveAsync(new AccountData(stations, [.. _data.Targets.Where(t => ids.Contains(t.StationId))], selected), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task SelectStationAsync(Guid? stationId, CancellationToken cancellationToken = default)
    {
        if (stationId is { } id && !_data.Stations.Any(s => s.StationId == id))
        {
            throw new ArgumentException("Not a station of this account.", nameof(stationId));
        }
        await SaveAsync(_data with { SelectedStationId = stationId }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The station's broadcast target. Uses the stored one unless <paramref name="renew"/> (e.g. Icecast refused the
    /// password); otherwise asks the API, which replaces this device's previous password for the station.
    /// </summary>
    public async Task<IssuedStationTarget> GetTargetAsync(Guid stationId, bool renew, CancellationToken cancellationToken = default)
    {
        var station = _data.Stations.FirstOrDefault(s => s.StationId == stationId)
            ?? throw new DesktopApiException(DesktopApiError.NotFound, "This station is not in your account. Refresh the station list.");
        if (!renew && _data.Targets.FirstOrDefault(t => t.StationId == stationId) is { } cached
            && await secrets.GetAsync(stationId, cancellationToken).ConfigureAwait(false) is { } password)
        {
            return new IssuedStationTarget(cached, password);
        }

        IssuedStationTarget issued;
        try
        {
            issued = await WithAccessTokenAsync(
                token => api.GetBroadcastTargetAsync(token, station.TenantId, station.StationId, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DesktopApiException ex) when (ex.Error == DesktopApiError.Forbidden)
        {
            // Forbidden may mean this device was signed out on the web: a refresh tells (and forgets the account if so).
            _accessToken = null;
            await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        await secrets.SetAsync(stationId, issued.Password, cancellationToken).ConfigureAwait(false);
        await SaveAsync(_data with { Targets = [.. _data.Targets.Where(t => t.StationId != stationId), issued.Target] }, cancellationToken)
            .ConfigureAwait(false);
        return issued;
    }

    /// <summary>Signs the device out on the server (best effort) and forgets every token and station password.</summary>
    public async Task SignOutAsync(CancellationToken cancellationToken = default)
    {
        if (await secrets.GetAsync(RefreshTokenKey, cancellationToken).ConfigureAwait(false) is { } refreshToken)
        {
            try
            {
                await api.RevokeAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (DesktopApiException)
            {
                // Offline: the session expires on the server; this device forgets it now.
            }
        }
        await ForgetAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Calls the API with the access token. A 401 means the token predates a sign-out on the web (or a server restart):
    /// refresh and retry once, so a signed-out device is forgotten by the refresh.
    /// </summary>
    private async Task<T> WithAccessTokenAsync<T>(Func<string, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
        catch (DesktopApiException ex) when (ex.Error == DesktopApiError.Unauthorized)
        {
            _accessToken = null;
            return await call(await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
        }
    }

    /// <summary>A valid access token, refreshed (and the refresh token rotated) when it is about to expire.</summary>
    private async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _tokenGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_accessToken is { } token && time.GetUtcNow() < _accessExpiresAt - RefreshMargin)
            {
                return token;
            }
            var refreshToken = await secrets.GetAsync(RefreshTokenKey, cancellationToken).ConfigureAwait(false)
                ?? throw new DesktopApiException(DesktopApiError.Unauthorized, "Sign in with your Tropicast account first.");
            TokenPair tokens;
            try
            {
                tokens = await api.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
            }
            catch (DesktopApiException ex) when (ex.Error == DesktopApiError.Unauthorized)
            {
                await ForgetAsync(cancellationToken).ConfigureAwait(false);
                throw new DesktopApiException(DesktopApiError.Unauthorized, "This device was signed out. Sign in again.", ex);
            }
            await StoreTokensAsync(tokens, cancellationToken).ConfigureAwait(false);
            return tokens.AccessToken;
        }
        finally
        {
            _tokenGate.Release();
        }
    }

    private async Task StoreTokensAsync(TokenPair tokens, CancellationToken cancellationToken)
    {
        await secrets.SetAsync(RefreshTokenKey, tokens.RefreshToken, cancellationToken).ConfigureAwait(false);
        _accessToken = tokens.AccessToken;
        _accessExpiresAt = time.GetUtcNow() + tokens.ExpiresIn;
    }

    private async Task ForgetAsync(CancellationToken cancellationToken)
    {
        _accessToken = null;
        IsSignedIn = false;
        await secrets.DeleteAsync(RefreshTokenKey, cancellationToken).ConfigureAwait(false);
        foreach (var id in _data.Stations.Select(s => s.StationId).Concat(_data.Targets.Select(t => t.StationId)).Distinct())
        {
            await secrets.DeleteAsync(id, cancellationToken).ConfigureAwait(false);
        }
        await SaveAsync(AccountData.Empty, cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveAsync(AccountData data, CancellationToken cancellationToken)
    {
        _data = data;
        await store.SaveAsync(data, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => _tokenGate.Dispose();
}
