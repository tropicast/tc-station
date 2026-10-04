using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tropicast.Station.E2E.Tests;

/// <summary>
/// Environment gate for the suite. Without <c>TC_E2E_IMAGE</c> the tests skip locally, but with
/// <c>TC_E2E_REQUIRED=1</c> (CI) a missing image or FFmpeg bundle fails the run instead of hiding it.
/// </summary>
internal static class E2EEnvironment
{
    internal static string? Image => Environment.GetEnvironmentVariable("TC_E2E_IMAGE");

    internal static void Require(bool needsContainer)
    {
        var required = Environment.GetEnvironmentVariable("TC_E2E_REQUIRED") == "1";
        var missing = needsContainer && string.IsNullOrWhiteSpace(Image)
            ? "TC_E2E_IMAGE is not set. Run scripts/e2e.sh or build tests/Tropicast.Station.E2E.Tests/docker and set TC_E2E_IMAGE."
            : !File.Exists(Media.FfmpegPath)
                ? "The FFmpeg bundle is missing. Run scripts/build-ffmpeg.sh for this platform and rebuild."
                : null;
        if (missing is null)
        {
            return;
        }
        Assert.False(required, $"E2E prerequisites are missing: {missing}");
        Assert.Skip(missing);
    }
}

/// <summary>A disposable Tropicast Icecast container with per-run generated credentials.</summary>
internal sealed class TropicastContainer : IAsyncDisposable
{
    private readonly string _name = $"tc-e2e-{Guid.NewGuid():N}";
    private static readonly Lock PortLock = new();
    private static readonly HashSet<int> HandedOutPorts = [];

    internal int Port { get; private set; }
    internal string SourcePassword { get; } = RandomNumberGenerator.GetHexString(32);
    private string AdminPassword { get; } = RandomNumberGenerator.GetHexString(32);

    internal static async Task<TropicastContainer> StartAsync(CancellationToken token)
    {
        var container = new TropicastContainer();
        try
        {
            // Another process can still take the port between FreePort and docker run; retry on a bind failure.
            for (var attempt = 1; ; attempt++)
            {
                container.Port = FreePort();
                try
                {
                    await container.RunAsync(token);
                    break;
                }
                catch (IOException ex) when (attempt < 3 && ex.Message.Contains("port", StringComparison.OrdinalIgnoreCase))
                {
                    await container.RemoveAsync();
                }
            }
            await container.WaitReadyAsync(token);
            return container;
        }
        catch
        {
            await container.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Returns a free loopback port never handed out before in this process, so tests running in
    /// parallel cannot receive the same port (including the one reserved for the unreachable-host case).
    /// </summary>
    internal static int FreePort()
    {
        lock (PortLock)
        {
            while (true)
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                if (HandedOutPorts.Add(port))
                {
                    return port;
                }
            }
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        await DockerAsync(token, "run", "-d", "--name", _name,
            "-p", $"127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}:8000",
            "-e", $"ICECAST_SOURCE_PASSWORD={SourcePassword}",
            "-e", $"ICECAST_ADMIN_PASSWORD={AdminPassword}",
            E2EEnvironment.Image!);
    }

    /// <summary>Stops and starts the same container, keeping its published port, like a server restart.</summary>
    internal async Task RestartAsync(CancellationToken token)
    {
        await DockerAsync(token, "restart", "-t", "2", _name);
        await WaitReadyAsync(token);
    }

    internal async Task<string> LogsAsync() => await DockerAsync(CancellationToken.None, "logs", "--tail", "50", _name);

    private async Task WaitReadyAsync(CancellationToken token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
        for (var attempt = 0; attempt < 150; attempt++)
        {
            try
            {
                using var response = await http.GetAsync(StatusUrl, token);
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !token.IsCancellationRequested)
            {
                // The server may take a moment to bind its socket.
            }
            await Task.Delay(100, token);
        }
        throw new IOException($"The Tropicast container did not become ready within 15 seconds. Logs:\n{await LogsAsync()}");
    }

    internal string StatusUrl => $"http://127.0.0.1:{Port.ToString(CultureInfo.InvariantCulture)}/status-json.xsl";

    /// <summary>Returns the mount points that currently have an active source, as seen by listeners' status page.</summary>
    internal async Task<IReadOnlyList<string>> ActiveMountsAsync(CancellationToken token)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(StatusUrl, token);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: token);
        if (!document.RootElement.GetProperty("icestats").TryGetProperty("source", out var source))
        {
            return [];
        }
        // Icecast publishes one source as an object and several as an array.
        var items = source.ValueKind == JsonValueKind.Array ? source.EnumerateArray().ToArray() : [source];
        return items
            .Select(item => item.GetProperty("listenurl").GetString()!)
            .Select(url => new Uri(url).AbsolutePath)
            .ToArray();
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
        using var process = Process.Start(start) ?? throw new IOException("Docker could not be started. Is it installed?");
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(60), token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
        if (process.ExitCode != 0)
        {
            throw new IOException($"docker {args[0]} failed: {await error}");
        }
        // Docker writes container stdout/stderr of `logs` to both streams.
        return await output + await error;
    }

    /// <summary>Removes the container if it exists; a missing container must not hide the original failure.</summary>
    private async Task RemoveAsync()
    {
        try
        {
            await DockerAsync(CancellationToken.None, "rm", "-f", _name);
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            // Nothing to clean up, or Docker is unavailable and the original error is more useful.
        }
    }

    public ValueTask DisposeAsync() => new(RemoveAsync());
}
