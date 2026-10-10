using Tropicast.Station.Core.Account;

namespace Tropicast.Station.Tests;

internal sealed class MemoryAccountStore : IAccountStore
{
    public AccountData Data { get; set; } = AccountData.Empty;

    public Task<AccountData> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Data);

    public Task SaveAsync(AccountData data, CancellationToken cancellationToken = default)
    {
        Data = data;
        return Task.CompletedTask;
    }
}

/// <summary>The desktop API of a tc-dashboard with one user, as the contract describes it.</summary>
internal sealed class FakeDesktopApi : IDesktopApi
{
    private int _issued;
    private int _refreshes;

    public static readonly Guid TenantId = Guid.NewGuid();
    public static readonly AccountStation Radio = new(TenantId, "My radios", Guid.NewGuid(), "k3m9x2p7qa", "Radio Mada", "Owner");

    public Queue<SignInPollStatus> Polls { get; } = new();
    public List<AccountStation> Stations { get; } = [Radio];
    public int MaxBitrateKbps { get; set; } = 128;
    public bool OpusAllowed { get; set; } = true;
    /// <summary>The device was signed out on the web: refreshes and targets are refused.</summary>
    public bool SignedOutOnWeb { get; set; }
    public bool Offline { get; set; }
    /// <summary>Access tokens issued so far are refused with 401 (e.g. after a password change on the web).</summary>
    public bool RejectAccessTokens { get; set; }
    public List<string> Revoked { get; } = [];
    public List<string> AccessTokensSeen { get; } = [];
    public string CurrentRefreshToken { get; private set; } = "";
    public int TargetRequests => _issued;
    public int Refreshes => _refreshes;
    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(15);

    public Task<DeviceSignIn> StartSignInAsync(string deviceName, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        return Task.FromResult(new DeviceSignIn("device-secret", "BCDF-GHJK", new Uri("https://app.test/device"),
            new Uri("https://app.test/device?code=BCDF-GHJK"), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(5)));
    }

    public Task<SignInPoll> PollSignInAsync(string deviceSecret, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        var status = Polls.Count > 0 ? Polls.Dequeue() : SignInPollStatus.Approved;
        return Task.FromResult(status == SignInPollStatus.Approved ? new SignInPoll(status, Issue()) : new SignInPoll(status));
    }

    public Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        Interlocked.Increment(ref _refreshes);
        if (SignedOutOnWeb || refreshToken != CurrentRefreshToken)
        {
            throw new DesktopApiException(DesktopApiError.Unauthorized, "Sign in again on this device.");
        }
        return Task.FromResult(Issue());
    }

    public Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        Revoked.Add(refreshToken);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AccountStation>> GetStationsAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        AccessTokensSeen.Add(accessToken);
        if (RejectAccessTokens)
        {
            throw new DesktopApiException(DesktopApiError.Unauthorized, "Sign in again on this device.");
        }
        return Task.FromResult<IReadOnlyList<AccountStation>>([.. Stations]);
    }

    public Task<IssuedStationTarget> GetBroadcastTargetAsync(string accessToken, Guid tenantId, Guid stationId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOffline();
        AccessTokensSeen.Add(accessToken);
        if (SignedOutOnWeb)
        {
            throw new DesktopApiException(DesktopApiError.Forbidden, "Only a signed-in desktop device gets a broadcast target.");
        }
        var station = Stations.Single(s => s.StationId == stationId && s.TenantId == tenantId);
        var n = Interlocked.Increment(ref _issued);
        List<StationOutput> outputs = [Output(station, "mp3", "audio/mpeg")];
        if (OpusAllowed)
        {
            outputs.Add(Output(station, "opus", "audio/ogg"));
        }
        return Task.FromResult(new IssuedStationTarget(
            new StationTarget(station.StationId, station.PublicId, station.Name, station.PublicId, MaxBitrateKbps, outputs),
            $"station-password-{n}"));
    }

    private static StationOutput Output(AccountStation station, string extension, string contentType)
        => new(extension == "mp3" ? "Mp3" : "Opus", contentType,
            new Uri($"https://ingest.tropicastradio.com/stations/{station.PublicId}/live.{extension}"),
            new Uri($"https://listen.tropicastradio.com/stations/{station.PublicId}/live.{extension}"));

    private TokenPair Issue()
    {
        CurrentRefreshToken = $"refresh-{Guid.NewGuid():N}";
        return new TokenPair($"access-{Guid.NewGuid():N}", AccessTokenLifetime, CurrentRefreshToken);
    }

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new DesktopApiException(DesktopApiError.Unavailable, "Cannot reach the Tropicast API.");
        }
    }
}

/// <summary>A clock whose timers fire at once, moving time forward by their delay: polling loops run instantly.</summary>
internal sealed class FastTime : TimeProvider
{
    private long _ticks = new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero).UtcTicks;

    public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
