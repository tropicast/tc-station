using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Infrastructure;

/// <summary>Probes the shared source handshake without sending audio; closes the temporary reservation.</summary>
public sealed class IcecastConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(BroadcastTarget target, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await IcecastSourceConnection.ConnectAsync(target, cancellationToken).ConfigureAwait(false);
            return new(ConnectionTestStatus.Accepted, "Credentials accepted; the mount was free at test time.");
        }
        catch (IcecastSourceException ex)
        {
            return new(ex.Status, ex.Message);
        }
    }
}
