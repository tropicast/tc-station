using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Infrastructure;

/// <summary>
/// Probes source authentication with PUT + Expect: 100-continue, without sending audio.
/// A successful probe briefly reserves the mount, then releases it by closing the socket.
/// No redirects, certificate bypasses, credentials in URLs, or response-body logging.
/// </summary>
public sealed class IcecastConnectionTester : IConnectionTester
{
    public async Task<ConnectionTestResult> TestAsync(BroadcastTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ProfileValidator.Validate(target.Profile).Count > 0 || !ProfileValidator.IsValidPassword(target.Password))
        {
            throw new ArgumentException("Invalid broadcast target.", nameof(target));
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(target.Profile.Host, target.Profile.Port, deadline.Token).ConfigureAwait(false);
            await using var network = client.GetStream();
            await using var tls = target.Profile.UseTls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
            Stream stream = network;
            if (tls is not null)
            {
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.Profile.Host,
                }, deadline.Token).ConfigureAwait(false);
                stream = tls;
            }

            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.Profile.Username}:{target.Password}"));
            var request = $"PUT {target.Profile.Mount} HTTP/1.1\r\nHost: {target.Profile.Endpoint.Authority}\r\n"
                + $"Authorization: Basic {credentials}\r\nContent-Type: {target.Profile.ContentType}\r\n"
                + "Expect: 100-continue\r\nIce-Public: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), deadline.Token).ConfigureAwait(false);
            // Read only the bounded status line; never copy server responses into UI/logs.
            var buffer = new byte[1];
            var statusLine = new StringBuilder();
            while (statusLine.Length < 1024)
            {
                if (await stream.ReadAsync(buffer, deadline.Token).ConfigureAwait(false) == 0 || buffer[0] == '\n')
                {
                    break;
                }

                statusLine.Append((char)buffer[0]);
            }

            var parts = statusLine.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
            {
                return new(ConnectionTestStatus.Rejected, "The server did not return a valid Icecast HTTP response.");
            }

            return code switch
            {
                100 or 200 or 201 => new(ConnectionTestStatus.Accepted, "Credentials accepted; the mount was free at test time."),
                401 => new(ConnectionTestStatus.AuthenticationFailed, "Authentication failed. Check the source username and password."),
                409 => new(ConnectionTestStatus.MountInUse, "The mount is already in use. Stop its current source or choose another mount."),
                403 => new(ConnectionTestStatus.Rejected, "Publishing was denied (mount permissions or source capacity)."),
                _ => new(ConnectionTestStatus.Rejected, $"The server rejected the source handshake (HTTP {code})."),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(ConnectionTestStatus.TimedOut, "Connection timed out after 10 seconds.");
        }
        catch (AuthenticationException)
        {
            return new(ConnectionTestStatus.TlsFailed, "TLS failed. Check the server certificate and TLS port.");
        }
        catch (SocketException)
        {
            return new(ConnectionTestStatus.Unreachable, "Cannot reach the server. Check the host, port, network and firewall.");
        }
        catch (IOException)
        {
            return new(ConnectionTestStatus.Unreachable, "The server closed the connection before completing the source handshake.");
        }
    }
}
