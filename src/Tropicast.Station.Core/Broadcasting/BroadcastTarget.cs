using Tropicast.Station.Core.Profiles;
using System.Text.Json.Serialization;

namespace Tropicast.Station.Core.Broadcasting;

/// <summary>Ephemeral target. Never serialize it or put credentials in the endpoint URI.</summary>
public sealed class BroadcastTarget(ConnectionProfile profile, string password)
{
    public ConnectionProfile Profile { get; } = profile;

    [JsonIgnore]
    public string Password { get; } = password;

    public override string ToString() => $"BroadcastTarget({Profile.Id}; password=[redacted])";
}

public interface IBroadcastTargetProvider
{
    Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default);

    /// <summary>
    /// A new target after the server refused the password, or null when the password cannot be renewed (a manual
    /// profile: the user edits it).
    /// </summary>
    Task<BroadcastTarget?> RenewAsync(Guid profileId, CancellationToken cancellationToken = default)
        => Task.FromResult<BroadcastTarget?>(null);
}

public sealed class ManualBroadcastTargetProvider(IProfileStore profiles, ISecretStore secrets) : IBroadcastTargetProvider
{
    public async Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var profile = (await profiles.LoadAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == profileId) ?? throw new InvalidOperationException("Select a saved profile.");
        if (ProfileValidator.Validate(profile).Count > 0)
        {
            throw new InvalidOperationException("The saved profile is invalid. Edit it before broadcasting.");
        }

        var password = await secrets.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        if (!ProfileValidator.IsValidPassword(password))
        {
            throw new InvalidOperationException("This profile has no valid stored password. Enter one in the editor.");
        }

        return new BroadcastTarget(profile, password!);
    }
}

public interface IConnectionTester
{
    Task<ConnectionTestResult> TestAsync(BroadcastTarget target, CancellationToken cancellationToken = default);
}

public enum ConnectionTestStatus
{
    Accepted,
    AuthenticationFailed,
    MountInUse,
    Unreachable,
    TlsFailed,
    Rejected,
    TimedOut,
}

public sealed record ConnectionTestResult(ConnectionTestStatus Status, string Message);
