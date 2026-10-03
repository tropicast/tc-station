using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Encoding.Tests;

public sealed class ReconnectIntegrationTests
{
    private static readonly string[] MetadataFields = ["server_name", "server_description", "genre", "server_url", "bitrate"];
    [Fact]
    public async Task Real_Tropicast_status_exposes_saved_name_description_genre_url_and_bitrate()
    {
        if (Environment.GetEnvironmentVariable("TC_TEST_ICECAST_EXECUTABLE") is null
            && Environment.GetEnvironmentVariable("TC_TEST_ICECAST_DOCKER_IMAGE") is null)
        {
            Assert.Skip("Set TC_TEST_ICECAST_EXECUTABLE or TC_TEST_ICECAST_DOCKER_IMAGE for isolated metadata verification.");
            return;
        }
        EncoderTests.RequireBundle();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        await using var server = new RestartServer();
        await server.CreateAsync(deadline.Token);
        var profile = new ConnectionProfile(Guid.NewGuid(), "Metadata POC", "127.0.0.1", server.Port, "/metadata.mp3",
            BitrateKbps: 192, SampleRate: 48000, Channels: 1,
            StreamName: "Tropicast radio", StreamDescription: "Local independent programming",
            StreamGenre: "Talk", StreamUrl: "https://example.com/station");
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        using var encoder = EncoderTests.CreateEncoder();
        using var controller = new BroadcastController(capture, new Target(new(profile, "tc-test-source")),
            encoder, NullLogger<BroadcastController>.Instance);
        await controller.StartAsync(profile.Id, "demo-input", cancellationToken: deadline.Token);
        await UntilLiveAsync(controller, deadline.Token);
        using var http = new HttpClient();
        var source = await ReadSourceStatusAsync(http, server.Port, deadline.Token);
        Assert.Equal(profile.StreamName, source.GetProperty("server_name").GetString());
        Assert.Equal(profile.StreamDescription, source.GetProperty("server_description").GetString());
        Assert.Equal(profile.StreamGenre, source.GetProperty("genre").GetString());
        Assert.Equal(profile.StreamUrl, source.GetProperty("server_url").GetString());
        Assert.Equal(profile.BitrateKbps.ToString(CultureInfo.InvariantCulture), source.GetProperty("bitrate").ToString());
        await ReadListenerAsync(profile, deadline.Token);
        await controller.StopAsync(deadline.Token);
    }

    private static async Task<JsonElement> ReadSourceStatusAsync(HttpClient http, int port, CancellationToken token)
    {
        // Icecast stats are published asynchronously, after the source begins sending audio.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var response = await http.GetAsync($"http://127.0.0.1:{port}/status-json.xsl", token);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(token);
            using var status = await JsonDocument.ParseAsync(body, cancellationToken: token);
            if (status.RootElement.GetProperty("icestats").TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.Object
                && MetadataFields.All(field => source.TryGetProperty(field, out _)))
            {
                return source.Clone();
            }
            await Task.Delay(100, token);
        }
        throw new IOException("Tropicast status did not publish all configured metadata fields within 10 seconds.");
    }

    [Fact]
    public async Task Real_Tropicast_restart_recovers_without_user_action_and_bad_credentials_stop_retrying()
    {
        if (Environment.GetEnvironmentVariable("TC_TEST_ICECAST_EXECUTABLE") is null
            && Environment.GetEnvironmentVariable("TC_TEST_ICECAST_DOCKER_IMAGE") is null)
        {
            Assert.Skip("Set TC_TEST_ICECAST_EXECUTABLE or TC_TEST_ICECAST_DOCKER_IMAGE for an isolated restart test.");
            return;
        }
        EncoderTests.RequireBundle();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await using var server = new RestartServer();
        await server.CreateAsync(deadline.Token);
        var profile = new ConnectionProfile(Guid.NewGuid(), "Reconnect POC", "127.0.0.1", server.Port, "/reconnect.mp3");
        await using var capture = new AudioCaptureService(new ToneAudioCaptureProvider(), NullLogger<AudioCaptureService>.Instance);
        using var encoder = EncoderTests.CreateEncoder();
        using var controller = new BroadcastController(capture, new Target(new(profile, "tc-test-source")),
            encoder, NullLogger<BroadcastController>.Instance);
        await controller.StartAsync(profile.Id, "demo-input", cancellationToken: deadline.Token);
        await UntilLiveAsync(controller, deadline.Token);
        await ReadListenerAsync(profile, deadline.Token);
        await server.StopAsync(deadline.Token);
        await UntilAsync(() => controller.Snapshot.State == BroadcastState.Reconnecting, deadline.Token);
        Assert.True(capture.Snapshot.IsCapturing);
        Assert.True(capture.Levels.Read().IsActive);
        await Task.Delay(1500, deadline.Token);
        Assert.Equal(BroadcastState.Reconnecting, controller.Snapshot.State);
        await server.StartAsync(deadline.Token);
        // Do not press Retry now: this proves unattended automatic recovery after a real restart.
        await UntilLiveAsync(controller, deadline.Token);
        Assert.Equal(1, controller.Snapshot.ReconnectCount);
        Assert.True(controller.Snapshot.Downtime.TotalSeconds >= 1);
        await ReadListenerAsync(profile, deadline.Token);
        await controller.StopAsync(deadline.Token);
        Assert.False(capture.Snapshot.IsCapturing);

        using var wrongPassword = new BroadcastController(capture, new Target(new(profile, "wrong-test-password")),
            encoder, NullLogger<BroadcastController>.Instance);
        await wrongPassword.StartAsync(profile.Id, "demo-input", cancellationToken: deadline.Token);
        Assert.Equal(BroadcastState.Error, wrongPassword.Snapshot.State);
        Assert.Contains("Authentication failed", wrongPassword.Snapshot.Message, StringComparison.Ordinal);
        Assert.False(capture.Snapshot.IsCapturing);
        await Task.Delay(1500, deadline.Token);
        Assert.Equal(BroadcastState.Error, wrongPassword.Snapshot.State);
        Assert.Equal(0, wrongPassword.Snapshot.RetryAttempt);
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(50, token);
        }
    }

    private static Task UntilLiveAsync(BroadcastController controller, CancellationToken token)
        => UntilAsync(() =>
        {
            Assert.True(controller.Snapshot.State != BroadcastState.Error, controller.Snapshot.Message);
            return controller.Snapshot.State == BroadcastState.Live;
        }, token);

    private static async Task ReadListenerAsync(ConnectionProfile profile, CancellationToken token)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(profile.Endpoint, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        await using var listener = await response.Content.ReadAsStreamAsync(token);
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        while (captured.Length < profile.BitrateKbps * 1000 / 8 * 2)
        {
            var count = await listener.ReadAsync(buffer, token);
            Assert.True(count > 0);
            captured.Write(buffer, 0, count);
        }
        await EncoderTests.AssertDecodableAsync(captured.ToArray(), 1.8, profile.SampleRate, profile.Channels);
    }

    private sealed class Target(BroadcastTarget target) : IBroadcastTargetProvider
    {
        public Task<BroadcastTarget> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
            => Task.FromResult(target);
    }

    private sealed class RestartServer : IAsyncDisposable
    {
        private readonly string? _image = Environment.GetEnvironmentVariable("TC_TEST_ICECAST_DOCKER_IMAGE");
        private readonly string? _executable = Environment.GetEnvironmentVariable("TC_TEST_ICECAST_EXECUTABLE");
        private readonly string _name = $"tc-reconnect-{Guid.NewGuid():N}";
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"tc-reconnect-{Guid.NewGuid():N}");
        private Process? _process;
        private Task<string>? _output;
        private Task<string>? _error;
        private bool _containerCreated;
        internal int Port { get; private set; }

        internal async Task CreateAsync(CancellationToken token)
        {
            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            Port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            if (_image is not null)
            {
                await DockerAsync(token, "create", "--name", _name, "-p", $"127.0.0.1:{Port}:8000",
                    "-e", "ICECAST_SOURCE_PASSWORD=tc-test-source", "-e", "ICECAST_RELAY_PASSWORD=tc-test-relay",
                    "-e", "ICECAST_ADMIN_PASSWORD=tc-test-admin", _image);
                _containerCreated = true;
            }
            else
            {
                Directory.CreateDirectory(_directory);
                var config = new XElement("icecast",
                    new XElement("location", "Isolated reconnect test"), new XElement("admin", "test@localhost"),
                    new XElement("hostname", "localhost"),
                    new XElement("limits", new XElement("clients", 10), new XElement("sources", 5)),
                    new XElement("authentication", new XElement("source-password", "tc-test-source"),
                        new XElement("relay-password", "tc-test-relay"), new XElement("admin-user", "admin"),
                        new XElement("admin-password", "tc-test-admin")),
                    new XElement("listen-socket", new XElement("port", Port), new XElement("bind-address", "127.0.0.1")),
                    new XElement("paths", new XElement("logdir", _directory),
                        new XElement("webroot", "/usr/share/icecast2/web"), new XElement("adminroot", "/usr/share/icecast2/admin")),
                    new XElement("logging", new XElement("accesslog", "access.log"), new XElement("errorlog", "error.log"),
                        new XElement("loglevel", 2)));
                await File.WriteAllTextAsync(Path.Combine(_directory, "icecast.xml"), config.ToString(), token);
            }
            await StartAsync(token);
        }

        internal async Task StartAsync(CancellationToken token)
        {
            if (_image is not null)
            {
                await DockerAsync(token, "start", _name);
                var endpoint = await DockerAsync(token, "port", _name, "8000/tcp");
                Port = int.Parse(endpoint.Trim().Split(':')[^1], CultureInfo.InvariantCulture);
            }
            else
            {
                var start = new ProcessStartInfo(_executable!)
                {
                    UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                };
                start.ArgumentList.Add("-c");
                start.ArgumentList.Add(Path.Combine(_directory, "icecast.xml"));
                _process = Process.Start(start)!;
                _output = _process.StandardOutput.ReadToEndAsync(CancellationToken.None);
                _error = _process.StandardError.ReadToEndAsync(CancellationToken.None);
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            for (var i = 0; i < 100; i++)
            {
                if (_process?.HasExited == true)
                {
                    throw new IOException($"Test Tropicast exited: {await _error!}");
                }
                try
                {
                    using var response = await http.GetAsync($"http://127.0.0.1:{Port}/status-json.xsl", token);
                    if (response.IsSuccessStatusCode)
                    {
                        return;
                    }
                }
                catch (HttpRequestException)
                {
                    // An isolated server may take a moment to bind its socket.
                }
                await Task.Delay(100, token);
            }
            throw new IOException("The isolated Tropicast test server did not become ready.");
        }

        internal async Task StopAsync(CancellationToken token)
        {
            if (_image is not null)
            {
                await DockerAsync(token, "stop", "-t", "2", _name);
            }
            else if (_process is { } process)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
                await process.WaitForExitAsync(token);
                await Task.WhenAll(_output!, _error!);
                process.Dispose();
                _process = null;
            }
        }

        private static async Task<string> DockerAsync(CancellationToken token, params string[] args)
        {
            var start = new ProcessStartInfo("docker")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(token);
            var error = process.StandardError.ReadToEndAsync(token);
            try
            {
                await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(20), token);
                if (process.ExitCode != 0)
                {
                    throw new IOException($"Test Docker command failed: {await error}");
                }
                return await output;
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (_containerCreated)
            {
                await DockerAsync(CancellationToken.None, "rm", "-f", _name);
            }
            else
            {
                await StopAsync(CancellationToken.None);
            }
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }
}
