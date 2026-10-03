using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Infrastructure;

public sealed class IcecastSourceException(ConnectionTestStatus status, string message) : IOException(message)
{
    public ConnectionTestStatus Status { get; } = status;
}

/// <summary>Credential-safe PUT source stream; no redirects or certificate bypasses.</summary>
public sealed class IcecastSourceConnection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly Stream _stream;
    private readonly bool _expectFinalResponse;

    private IcecastSourceConnection(TcpClient client, Stream stream, bool expectFinalResponse)
    {
        _client = client;
        _stream = stream;
        _expectFinalResponse = expectFinalResponse;
    }

    public Stream AudioStream => _stream;

    public static async Task<IcecastSourceConnection> ConnectAsync(BroadcastTarget target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (ProfileValidator.Validate(target.Profile).Count > 0 || !ProfileValidator.IsValidPassword(target.Password))
        {
            throw new ArgumentException("Invalid broadcast target.", nameof(target));
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var client = new TcpClient { NoDelay = true };
        Stream? stream = null;
        var negotiatingTls = false;
        var accepted = false;
        try
        {
            await client.ConnectAsync(target.Profile.Host, target.Profile.Port, deadline.Token).ConfigureAwait(false);
            stream = client.GetStream();
            if (target.Profile.UseTls)
            {
                stream = new SslStream(stream);
                negotiatingTls = true;
                await ((SslStream)stream).AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.Profile.Host,
                }, deadline.Token).ConfigureAwait(false);
                negotiatingTls = false;
            }
            var credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{target.Profile.Username}:{target.Password}"));
            var request = $"PUT {target.Profile.Mount} HTTP/1.1\r\nHost: {target.Profile.Endpoint.Authority}\r\n"
                + $"Authorization: Basic {credentials}\r\nContent-Type: {target.Profile.ContentType}\r\n"
                + "Expect: 100-continue\r\nIce-Public: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.UTF8.GetBytes(request), deadline.Token).ConfigureAwait(false);
            var code = await ReadResponseAsync(stream, deadline.Token).ConfigureAwait(false);
            if (code is not (100 or 200 or 201))
            {
                throw Rejected(code);
            }
            accepted = true;
            return new(client, stream, code == 100);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IcecastSourceException(ConnectionTestStatus.TimedOut, "Connection timed out after 10 seconds.");
        }
        catch (AuthenticationException)
        {
            throw new IcecastSourceException(ConnectionTestStatus.TlsFailed, "TLS failed. Check the server certificate and TLS port.");
        }
        catch (SocketException)
        {
            throw new IcecastSourceException(ConnectionTestStatus.Unreachable, "Cannot reach the server. Check the host, port, network and firewall.");
        }
        catch (IOException ex) when (ex is not IcecastSourceException)
        {
            throw new IcecastSourceException(negotiatingTls ? ConnectionTestStatus.TlsFailed : ConnectionTestStatus.Unreachable,
                negotiatingTls ? "TLS negotiation failed. Check the certificate, TLS port and server configuration."
                    : "The server closed the connection before completing the source handshake.");
        }
        finally
        {
            if (!accepted)
            {
                if (stream is not null)
                {
                    await stream.DisposeAsync().ConfigureAwait(false);
                }
                client.Dispose();
            }
        }
    }

    public async Task MonitorAsync(CancellationToken cancellationToken = default)
    {
        if (_expectFinalResponse)
        {
            var code = await ReadResponseAsync(_stream, cancellationToken).ConfigureAwait(false);
            throw Rejected(code);
        }
        var byteBuffer = new byte[1];
        await _stream.ReadAsync(byteBuffer, cancellationToken).ConfigureAwait(false);
        throw new IOException("Icecast closed the source connection.");
    }

    private static IcecastSourceException Rejected(int code) => code switch
    {
        401 => new(ConnectionTestStatus.AuthenticationFailed, "Authentication failed. Check the source username and password."),
        409 => new(ConnectionTestStatus.MountInUse, "The mount is already in use. Stop its current source or choose another mount."),
        403 => new(ConnectionTestStatus.Rejected, "Publishing was denied (mount permissions or source capacity)."),
        _ => new(ConnectionTestStatus.Rejected, $"The server rejected or ended the source stream (HTTP {code})."),
    };

    private static async Task<int> ReadResponseAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new byte[1];
        var header = new StringBuilder();
        while (header.Length < 16384)
        {
            if (await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false) == 0)
            {
                throw new IOException("Icecast closed the source connection.");
            }
            header.Append((char)bytes[0]);
            if (header.Length >= 4 && header[^4] == '\r' && header[^3] == '\n'
                && header[^2] == '\r' && header[^1] == '\n')
            {
                var firstLine = header.ToString().Split("\r\n", 2, StringSplitOptions.None)[0];
                var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("HTTP/1.", StringComparison.Ordinal)
                    && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var code))
                {
                    return code;
                }
                break;
            }
        }
        throw new IcecastSourceException(ConnectionTestStatus.Rejected, "The server did not return a valid Icecast HTTP response.");
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
    }
}
