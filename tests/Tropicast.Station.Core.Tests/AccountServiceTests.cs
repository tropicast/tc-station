using Tropicast.Station.Core.Account;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Core.Tests;

public sealed class AccountServiceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private readonly FakeDesktopApi _api = new();
    private readonly MemoryAccountStore _store = new();
    private readonly MemorySecrets _secrets = new();
    private readonly FastTime _time = new();

    private AccountService Account() => new(_api, _store, _secrets, _time);

    private async Task<AccountService> SignedInAsync()
    {
        var account = Account();
        await account.SignInAsync("Studio PC", _ => Task.CompletedTask, Token);
        return account;
    }

    [Fact]
    public async Task Sign_in_shows_the_code_polls_until_approved_and_stores_only_the_refresh_token()
    {
        _api.Polls.Enqueue(SignInPollStatus.Pending);
        _api.Polls.Enqueue(SignInPollStatus.SlowDown);
        _api.Polls.Enqueue(SignInPollStatus.Pending);
        DeviceSignIn? shown = null;
        using var account = Account();

        await account.SignInAsync("Studio PC", request =>
        {
            shown = request;
            return Task.CompletedTask;
        }, Token);

        Assert.Equal("BCDF-GHJK", shown!.UserCode);
        Assert.True(account.IsSignedIn);
        Assert.Equal(_api.CurrentRefreshToken, _secrets.Items[AccountService.RefreshTokenKey]);
        Assert.Single(_secrets.Items);
        Assert.Equal([FakeDesktopApi.Radio], account.Stations);
        Assert.Equal(FakeDesktopApi.Radio.StationId, account.SelectedStationId);
        Assert.DoesNotContain("access-", System.Text.Json.JsonSerializer.Serialize(_store.Data), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInPollStatus.Denied, "refused")]
    [InlineData(SignInPollStatus.Expired, "expired")]
    public async Task A_refused_or_expired_sign_in_stays_signed_out(SignInPollStatus status, string message)
    {
        _api.Polls.Enqueue(SignInPollStatus.Pending);
        _api.Polls.Enqueue(status);
        using var account = Account();

        var error = await Assert.ThrowsAsync<DesktopApiException>(() => account.SignInAsync("Studio PC", _ => Task.CompletedTask, Token));

        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.False(account.IsSignedIn);
        Assert.Empty(_secrets.Items);
    }

    [Fact]
    public async Task The_target_password_is_stored_and_reused_until_renewed()
    {
        using var account = await SignedInAsync();
        var id = FakeDesktopApi.Radio.StationId;
        Assert.True(account.IsStation(id));

        var first = await account.GetTargetAsync(id, renew: false, Token);
        var again = await account.GetTargetAsync(id, renew: false, Token);
        Assert.Equal(first.Password, again.Password);
        Assert.Equal(1, _api.TargetRequests);
        Assert.Equal(first.Password, _secrets.Items[id]);
        Assert.DoesNotContain(first.Password, System.Text.Json.JsonSerializer.Serialize(_store.Data), StringComparison.Ordinal);

        var renewed = await account.GetTargetAsync(id, renew: true, Token);
        Assert.NotEqual(first.Password, renewed.Password);
        Assert.Equal(renewed.Password, _secrets.Items[id]);
    }

    [Fact]
    public async Task The_access_token_is_refreshed_before_it_expires_and_the_refresh_token_rotates()
    {
        using var account = await SignedInAsync();
        var firstRefresh = _secrets.Items[AccountService.RefreshTokenKey];
        await account.RefreshStationsAsync(Token);
        Assert.Equal(0, _api.Refreshes);

        _time.Advance(TimeSpan.FromMinutes(14.5));
        await account.RefreshStationsAsync(Token);

        Assert.Equal(1, _api.Refreshes);
        Assert.NotEqual(firstRefresh, _secrets.Items[AccountService.RefreshTokenKey]);
        Assert.Equal(_api.CurrentRefreshToken, _secrets.Items[AccountService.RefreshTokenKey]);
    }

    [Fact]
    public async Task A_device_signed_out_on_the_web_forgets_its_tokens_and_passwords()
    {
        using var account = await SignedInAsync();
        var id = FakeDesktopApi.Radio.StationId;
        await account.GetTargetAsync(id, renew: false, Token);
        _api.SignedOutOnWeb = true;

        var error = await Assert.ThrowsAsync<DesktopApiException>(() => account.GetTargetAsync(id, renew: true, Token));

        Assert.Equal(DesktopApiError.Unauthorized, error.Error);
        Assert.False(account.IsSignedIn);
        Assert.Empty(_secrets.Items);
        Assert.Equal(AccountData.Empty, _store.Data);
    }

    [Fact]
    public async Task A_refused_access_token_is_refreshed_and_a_signed_out_device_is_forgotten()
    {
        using var account = await SignedInAsync();
        await account.GetTargetAsync(FakeDesktopApi.Radio.StationId, renew: false, Token);

        // Refused, but the refresh still works: one retry with the new token.
        _api.RejectAccessTokens = true;
        var before = _api.Refreshes;
        await Assert.ThrowsAsync<DesktopApiException>(() => account.RefreshStationsAsync(Token));
        Assert.Equal(before + 1, _api.Refreshes);
        Assert.True(account.IsSignedIn);

        // Signed out on the web while the cached access token is still unexpired: the refresh fails, the account is forgotten.
        _api.SignedOutOnWeb = true;
        var error = await Assert.ThrowsAsync<DesktopApiException>(() => account.RefreshStationsAsync(Token));
        Assert.Equal(DesktopApiError.Unauthorized, error.Error);
        Assert.False(account.IsSignedIn);
        Assert.Empty(_secrets.Items);
    }

    [Fact]
    public async Task Sign_out_revokes_the_device_and_removes_every_secret()
    {
        using var account = await SignedInAsync();
        await account.GetTargetAsync(FakeDesktopApi.Radio.StationId, renew: false, Token);
        var refreshToken = _secrets.Items[AccountService.RefreshTokenKey];

        await account.SignOutAsync(Token);

        Assert.Equal([refreshToken], _api.Revoked);
        Assert.Empty(_secrets.Items);
        Assert.False(account.IsSignedIn);
        Assert.False(account.IsStation(FakeDesktopApi.Radio.StationId));
    }

    [Fact]
    public async Task Offline_sign_out_still_forgets_the_device_locally()
    {
        using var account = await SignedInAsync();
        _api.Offline = true;

        await account.SignOutAsync(Token);

        Assert.Empty(_secrets.Items);
        Assert.False(account.IsSignedIn);
    }

    [Fact]
    public async Task A_new_run_restores_the_account_and_a_station_removed_on_the_web_loses_its_password()
    {
        using (var first = await SignedInAsync())
        {
            await first.GetTargetAsync(FakeDesktopApi.Radio.StationId, renew: false, Token);
        }
        using var account = Account();
        await account.LoadAsync(Token);
        Assert.True(account.IsSignedIn);
        Assert.True(account.IsStation(FakeDesktopApi.Radio.StationId));

        _api.Stations.Clear();
        await account.RefreshStationsAsync(Token);

        Assert.False(account.IsStation(FakeDesktopApi.Radio.StationId));
        Assert.Null(account.SelectedStationId);
        Assert.False(_secrets.Items.ContainsKey(FakeDesktopApi.Radio.StationId));
    }

    [Fact]
    public async Task The_station_profile_publishes_mp3_and_opus_within_the_plan()
    {
        using var account = await SignedInAsync();
        _api.MaxBitrateKbps = 64;
        var provider = new AccountBroadcastTargetProvider(account, new Broadcasting.ManualBroadcastTargetProvider(new MemoryProfiles(), _secrets));

        var target = await provider.GetAsync(FakeDesktopApi.Radio.StationId, Token);

        var profile = target.Profile;
        Assert.Equal("ingest.tropicastradio.com", profile.Host);
        Assert.Equal(443, profile.Port);
        Assert.True(profile.UseTls);
        Assert.Equal("/stations/k3m9x2p7qa/live.mp3", profile.Mount);
        Assert.Equal("k3m9x2p7qa", profile.Username);
        Assert.Equal(64, profile.BitrateKbps);
        Assert.True(profile.PublishOpus);
        Assert.Equal(64, profile.OpusBitrateKbps);
        Assert.Equal("Radio Mada", profile.StreamName);
        Assert.Equal(["/stations/k3m9x2p7qa/live.mp3", "/stations/k3m9x2p7qa/live.opus"], profile.Outputs().Select(o => o.Mount));
        Assert.Equal("station-password-1", target.Password);
        Assert.DoesNotContain("station-password", target.ToString(), StringComparison.Ordinal);

        var renewed = await provider.RenewAsync(FakeDesktopApi.Radio.StationId, Token);
        Assert.Equal("station-password-2", renewed!.Password);
    }

    [Fact]
    public async Task Manual_profiles_still_work_and_cannot_be_renewed()
    {
        using var account = Account();
        var profile = TestProfiles.Valid();
        _secrets.Items[profile.Id] = "manual-password";
        var provider = new AccountBroadcastTargetProvider(account,
            new Broadcasting.ManualBroadcastTargetProvider(new MemoryProfiles { Items = [profile] }, _secrets));

        Assert.Equal("manual-password", (await provider.GetAsync(profile.Id, Token)).Password);
        Assert.Null(await provider.RenewAsync(profile.Id, Token));
    }
}
