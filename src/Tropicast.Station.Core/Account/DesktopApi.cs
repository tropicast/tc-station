namespace Tropicast.Station.Core.Account;

/// <summary>A started sign-in (tc-dashboard docs/desktop-api.md, section 1). <see cref="DeviceSecret"/> never leaves the app.</summary>
public sealed record DeviceSignIn(string DeviceSecret, string UserCode, Uri VerificationUri, Uri VerificationUriComplete,
    TimeSpan ExpiresIn, TimeSpan Interval)
{
    public override string ToString() => $"DeviceSignIn({UserCode}; device code=[redacted])";
}

/// <summary>Tokens of this device. The refresh token is one-time: each refresh returns a new one.</summary>
public sealed record TokenPair(string AccessToken, TimeSpan ExpiresIn, string RefreshToken)
{
    public override string ToString() => $"TokenPair(expires in {ExpiresIn}; tokens=[redacted])";
}

/// <summary>Why a poll gave no tokens yet (RFC 8628 <c>error</c> values).</summary>
public enum SignInPollStatus
{
    Approved,
    Pending,
    SlowDown,
    Denied,
    Expired,
}

public sealed record SignInPoll(SignInPollStatus Status, TokenPair? Tokens = null);

/// <summary>A station the signed-in user can broadcast to.</summary>
public sealed record AccountStation(Guid TenantId, string TenantName, Guid StationId, string PublicId, string Name, string Role)
{
    public string DisplayName => $"{Name} ({TenantName})";
}

/// <summary>One stream to publish: format, content type and URLs.</summary>
public sealed record StationOutput(string Format, string ContentType, Uri IngestUrl, Uri ListenerUrl);

/// <summary>Where and how to publish a station, without its password (safe to store in a plain file).</summary>
public sealed record StationTarget(Guid StationId, string PublicId, string StationName, string Username, int MaxBitrateKbps,
    IReadOnlyList<StationOutput> Outputs);

/// <summary>A broadcast target with this device's password, which the API shows only once.</summary>
public sealed record IssuedStationTarget(StationTarget Target, string Password)
{
    public override string ToString() => $"IssuedStationTarget({Target.PublicId}; password=[redacted])";
}

public enum DesktopApiError
{
    /// <summary>The token or refresh token is no longer valid: sign in again.</summary>
    Unauthorized,
    /// <summary>Not allowed: not a member, the device was signed out, or the tenant is suspended.</summary>
    Forbidden,
    NotFound,
    /// <summary>Network failure, timeout or server error: try again later.</summary>
    Unavailable,
    /// <summary>The API answered something this version does not understand.</summary>
    Invalid,
}

/// <summary>A failed API call. <see cref="Exception.Message"/> is safe to show and log: it never contains a token.</summary>
public sealed class DesktopApiException : Exception
{
    public DesktopApiException() : this(DesktopApiError.Invalid, "The Tropicast API call failed.") { }

    public DesktopApiException(string message) : this(DesktopApiError.Invalid, message) { }

    public DesktopApiException(string message, Exception innerException) : base(message, innerException) => Error = DesktopApiError.Invalid;

    public DesktopApiException(DesktopApiError error, string message, Exception? innerException = null) : base(message, innerException)
        => Error = error;

    public DesktopApiError Error { get; }
}

/// <summary>The tc-dashboard desktop API, contract version 1.</summary>
public interface IDesktopApi
{
    Task<DeviceSignIn> StartSignInAsync(string deviceName, CancellationToken cancellationToken = default);

    Task<SignInPoll> PollSignInAsync(string deviceSecret, CancellationToken cancellationToken = default);

    Task<TokenPair> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AccountStation>> GetStationsAsync(string accessToken, CancellationToken cancellationToken = default);

    Task<IssuedStationTarget> GetBroadcastTargetAsync(string accessToken, Guid tenantId, Guid stationId,
        CancellationToken cancellationToken = default);
}

/// <summary>Non-secret account state kept between runs: the stations and their last broadcast targets.</summary>
public sealed record AccountData(IReadOnlyList<AccountStation> Stations, IReadOnlyList<StationTarget> Targets, Guid? SelectedStationId)
{
    public static AccountData Empty { get; } = new([], [], null);
}

public interface IAccountStore
{
    Task<AccountData> LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AccountData data, CancellationToken cancellationToken = default);
}
