using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Tests;

namespace Tropicast.Station.Infrastructure.Tests;

public sealed class ConnectionTesterTests
{
    [Theory]
    [InlineData("Mountpoint in use", ConnectionTestStatus.MountInUse)]
    [InlineData("too many sources connected", ConnectionTestStatus.Rejected)]
    [InlineData("Denied", ConnectionTestStatus.Rejected)]
    public async Task Legacy_Tropicast_403_body_is_classified_without_exposing_server_text(string body, ConnectionTestStatus expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = client.GetStream();
            var request = new StringBuilder();
            var bytes = new byte[1];
            while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.NotEqual(0, await stream.ReadAsync(bytes, deadline.Token));
                request.Append((char)bytes[0]);
            }
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.0 403 Forbidden\r\nContent-Type: text/plain; charset=utf-8\r\n\r\n{body}"), deadline.Token);
        }, deadline.Token);
        var target = new BroadcastTarget(TestProfiles.Valid() with
        {
            Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port,
        }, "test-only");
        var result = await new TropicastConnectionTester().TestAsync(target, deadline.Token);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain(body, result.Message, StringComparison.Ordinal);
        await serve;
    }

    [Theory]
    [InlineData(100, ConnectionTestStatus.Accepted)]
    [InlineData(401, ConnectionTestStatus.AuthenticationFailed)]
    [InlineData(409, ConnectionTestStatus.MountInUse)]
    [InlineData(403, ConnectionTestStatus.Rejected)]
    [InlineData(302, ConnectionTestStatus.Rejected)]
    [InlineData(503, ConnectionTestStatus.Unreachable)]
    public async Task Status_mapping_and_authenticated_source_handshake(int status, ConnectionTestStatus expected)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var serve = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var stream = connection.GetStream();
            var header = new StringBuilder();
            var buffer = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                Assert.NotEqual(0, await stream.ReadAsync(buffer, deadline.Token));
                header.Append((char)buffer[0]);
            }

            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n\r\n"), deadline.Token);
            // No audio body should be sent; the probe closes immediately after the status.
            try
            {
                Assert.Equal(0, await stream.ReadAsync(buffer, deadline.Token));
            }
            catch (IOException ex) when (ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionReset })
            {
                // Windows can report RST instead of EOF when the probe closes with unread response headers.
            }
            return header.ToString();
        }, deadline.Token);
        var profile = TestProfiles.Valid() with
        {
            Host = "127.0.0.1", Port = port, BitrateKbps = 192, SampleRate = 48000, Channels = 1,
            StreamName = "Station", StreamDescription = "Local programming", StreamGenre = "Talk",
            StreamUrl = "https://example.com/radio",
        };
        var result = await new TropicastConnectionTester().TestAsync(new(profile, "secret"), deadline.Token);
        Assert.Equal(expected, result.Status);
        Assert.DoesNotContain("secret", result.Message);
        var header = await serve;
        Assert.StartsWith("PUT /live.mp3 HTTP/1.1\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Expect: 100-continue", header, StringComparison.Ordinal);
        Assert.Contains("Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("source:secret")), header, StringComparison.Ordinal);
        Assert.Contains("Ice-Name: Station\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Description: Local programming\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Genre: Talk\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-URL: https://example.com/radio\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Bitrate: 192\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Audio-Info: bitrate=192;samplerate=48000;channels=1\r\n", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unreachable_host_is_explicit_and_cancellation_is_preserved()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var target = new BroadcastTarget(TestProfiles.Valid() with { Port = port, Host = "127.0.0.1" }, "secret");
        Assert.Equal(ConnectionTestStatus.Unreachable, (await new TropicastConnectionTester().TestAsync(target, TestContext.Current.CancellationToken)).Status);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TropicastConnectionTester().TestAsync(target, cancelled.Token));
    }

    [Fact]
    public async Task Untrusted_tls_certificate_is_rejected_before_sending_credentials()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Importing the key makes it usable by Windows Schannel as well as OpenSSL.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serve = Task.Run(async () =>
        {
            using var client = await listener.AcceptTcpClientAsync(deadline.Token);
            await using var tls = new SslStream(client.GetStream());
            try
            {
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, deadline.Token);
                var buffer = new byte[1024];
                Assert.Equal(0, await tls.ReadAsync(buffer, deadline.Token));
            }
            catch (Exception ex) when (ex is AuthenticationException or IOException)
            {
                // The client's certificate rejection may terminate the server handshake itself.
            }
        }, deadline.Token);
        var profile = TestProfiles.Valid() with { Port = ((IPEndPoint)listener.LocalEndpoint).Port, UseTls = true };
        Assert.Equal(ConnectionTestStatus.TlsFailed, (await new TropicastConnectionTester().TestAsync(new(profile, "secret"), deadline.Token)).Status);
        await serve;
    }

    [Fact]
    public async Task Real_tropicast_authentication_busy_mount_and_release()
    {
        if (Environment.GetEnvironmentVariable("TC_TEST_ICECAST_PORT") is not { } portText)
        {
            Assert.Skip("Set TC_TEST_ICECAST_PORT for a local Tropicast configured with source password tc-test-source.");
            return;
        }

        var profile = TestProfiles.Valid() with
        {
            Host = "127.0.0.1", Port = int.Parse(portText, System.Globalization.CultureInfo.InvariantCulture),
            Mount = $"/test-{Guid.NewGuid():N}.mp3",
        };
        var tester = new TropicastConnectionTester();
        Assert.Equal(ConnectionTestStatus.AuthenticationFailed, (await tester.TestAsync(new(profile, "wrong-password"), TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ConnectionTestStatus.Accepted, (await tester.TestAsync(new(profile, "tc-test-source"), TestContext.Current.CancellationToken)).Status);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(profile.Host, profile.Port, TestContext.Current.CancellationToken);
            await using var stream = client.GetStream();
            var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("source:tc-test-source"));
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"PUT {profile.Mount} HTTP/1.1\r\nHost: localhost\r\nAuthorization: Basic {auth}\r\nContent-Type: audio/mpeg\r\nExpect: 100-continue\r\n\r\n"), TestContext.Current.CancellationToken);
            var response = new byte[4096];
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var count = await stream.ReadAsync(response, timeout.Token);
            Assert.StartsWith("HTTP/1.1 100", Encoding.ASCII.GetString(response, 0, count), StringComparison.Ordinal);
            Assert.Equal(ConnectionTestStatus.MountInUse, (await tester.TestAsync(new(profile, "tc-test-source"), TestContext.Current.CancellationToken)).Status);
        }

        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(ConnectionTestStatus.Accepted, (await tester.TestAsync(new(profile, "tc-test-source"), TestContext.Current.CancellationToken)).Status);
    }
}
