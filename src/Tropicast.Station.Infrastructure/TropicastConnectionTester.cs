using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Infrastructure;

/// <summary>Probes the shared source handshake without sending audio; closes the temporary reservation.</summary>
public sealed class TropicastConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(BroadcastTarget target, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await TropicastSourceConnection.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            return new(ConnectionTestStatus.Accepted, "Credentials accepted; the mount was free at test time.");
        }
        catch (TropicastSourceException ex)
        {
            return new(ex.Status, ex.Message);
        }
    }
}
