using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Core.Account;

/// <summary>
/// Broadcast targets of the account's stations from the Tropicast API; any other ID is a manual connection profile
/// (advanced: self-hosted Icecast).
/// </summary>
public sealed class AccountBroadcastTargetProvider(AccountService account, ManualBroadcastTargetProvider manual) : IBroadcastTargetProvider
{
    /// <summary>Default encoder bitrates; the plan's maximum lowers them.</summary>
    internal const int Mp3BitrateKbps = 128;
    internal const int OpusBitrateKbps = 64;

    public async Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
        => account.IsStation(profileId)
            ? ToBroadcastTarget(await account.GetTargetAsync(profileId, renew: false, cancellationToken).ConfigureAwait(false))
            : await manual.GetAsync(profileId, cancellationToken).ConfigureAwait(false);

    public async Task<BroadcastTarget?> RenewAsync(Guid profileId, CancellationToken cancellationToken = default)
        => account.IsStation(profileId)
            ? ToBroadcastTarget(await account.GetTargetAsync(profileId, renew: true, cancellationToken).ConfigureAwait(false))
            : null;

    internal static BroadcastTarget ToBroadcastTarget(IssuedStationTarget issued)
    {
        var profile = ToProfile(issued.Target);
        if (ProfileValidator.Validate(profile).Count > 0 || !ProfileValidator.IsValidPassword(issued.Password))
        {
            throw new InvalidOperationException("The Tropicast API returned a broadcast target this version cannot use. Update the app.");
        }
        return new BroadcastTarget(profile, issued.Password);
    }

    /// <summary>
    /// The connection profile of a station: MP3 first, plus Opus from the same capture when the plan allows it, at
    /// the default bitrates capped by the plan.
    /// </summary>
    public static ConnectionProfile ToProfile(StationTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var mp3 = target.Outputs.FirstOrDefault(o => o.ContentType == "audio/mpeg");
        var opus = target.Outputs.FirstOrDefault(o => o.ContentType == "audio/ogg");
        var primary = mp3 ?? opus
            ?? throw new InvalidOperationException("The station has no stream format this version can publish. Update the app.");
        var url = primary.IngestUrl;
        var isOpus = primary == opus;
        var profile = new ConnectionProfile(target.StationId, Truncate(target.StationName), url.Host, url.Port, url.AbsolutePath,
            target.Username, UseTls: url.Scheme == Uri.UriSchemeHttps, ContentType: primary.ContentType,
            BitrateKbps: Math.Min(isOpus ? OpusBitrateKbps : Mp3BitrateKbps, target.MaxBitrateKbps),
            SampleRate: isOpus ? 48000 : 44100, StreamName: Truncate(target.StationName),
            OpusBitrateKbps: Math.Min(OpusBitrateKbps, target.MaxBitrateKbps));
        // Opus rides along only on the same ingest host, at the mount the encoder derives from the MP3 one.
        return mp3 is not null && opus is not null && opus.IngestUrl.Authority == url.Authority
            && opus.IngestUrl.AbsolutePath == profile.OpusMount
            ? profile with { PublishOpus = true }
            : profile;
    }

    private static string Truncate(string name) => name.Length <= 64 ? name : name[..64];
}
