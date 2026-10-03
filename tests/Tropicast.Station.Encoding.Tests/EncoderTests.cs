using System.Diagnostics;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.Encoding.Tests;

public sealed class EncoderTests
{
    [Fact]
    public void New_encoder_options_match_profile_defaults()
    {
        var options = new EncoderOptions();
        var profile = new ConnectionProfile(Guid.NewGuid(), "Defaults", "localhost", 8000, "/default.mp3");
        Assert.Equal(128, options.BitrateKbps);
        Assert.Equal(new AudioFormat(44100, 2), options.Format);
        Assert.Equal(options, EncoderOptions.FromProfile(profile));
    }

    [Theory]
    [InlineData(32000, 2, 128)]
    [InlineData(48000, 3, 128)]
    [InlineData(48000, 2, 31)]
    [InlineData(48000, 2, 321)]
    [InlineData(48000, 2, 160)]
    public void Invalid_encoder_settings_are_rejected(int rate, int channels, int bitrate)
        => Assert.ThrowsAny<ArgumentException>(() => new EncoderOptions(new(rate, channels), bitrate));

    [Fact]
    public void Signed16_is_rejected_and_arguments_contain_only_pipe_endpoints()
    {
        Assert.Throws<ArgumentException>(() => new EncoderOptions(new(48000, 2, PcmEncoding.Signed16)));
        var args = FfmpegExecutable.Arguments(new());
        Assert.Contains("libmp3lame", args);
        Assert.Contains("pipe:0", args);
        Assert.Contains("pipe:1", args);
        Assert.DoesNotContain(args, a => a.Contains("icecast", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_bundle_is_an_explicit_error_without_PATH_fallback()
    {
        var executable = new FfmpegExecutable(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}"));
        var error = Assert.Throws<EncoderException>(() => executable.Start(new()));
        Assert.Contains("Bundled FFmpeg is missing", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, ConnectionTestStatus.AuthenticationFailed)]
    [InlineData(409, ConnectionTestStatus.MountInUse)]
    [InlineData(403, ConnectionTestStatus.Rejected)]
    [InlineData(302, ConnectionTestStatus.Rejected)]
    [InlineData(503, ConnectionTestStatus.Unreachable)]
    public async Task Publishing_rejections_are_classified_without_starting_encoder(int code, ConnectionTestStatus status)
    {
        await using var server = new FakeIcecast(code);
        using var encoder = CreateEncoder();
        var error = await Assert.ThrowsAsync<IcecastSourceException>(() =>
            encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(status, error.Status);
        Assert.DoesNotContain(server.Target.Password, error.Message, StringComparison.Ordinal);
        await server.Done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MP3_requires_matching_profile_content_type()
    {
        using var encoder = CreateEncoder();
        var profile = new ConnectionProfile(Guid.NewGuid(), "Test", "127.0.0.1", 8000, "/test.mp3", ContentType: "audio/ogg");
        await Assert.ThrowsAsync<ArgumentException>(() => encoder.StartAsync(new(profile, "test-only"),
            cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(64, 44100, 1)]
    [InlineData(96, 48000, 2)]
    [InlineData(128, 44100, 2)]
    [InlineData(192, 48000, 1)]
    [InlineData(320, 44100, 2)]
    public async Task Saved_quality_and_metadata_reach_handshake_and_actual_MP3(int bitrate, int rate, int channels)
    {
        RequireBundle();
        await using var server = new FakeIcecast();
        var profile = server.Target.Profile with
        {
            BitrateKbps = bitrate, SampleRate = rate, Channels = channels,
            StreamName = "Studio radio", StreamDescription = "Local programming",
            StreamGenre = "Talk", StreamUrl = "https://example.com/studio",
        };
        var options = EncoderOptions.FromProfile(profile);
        Assert.Equal(bitrate, options.BitrateKbps);
        Assert.Equal(rate, options.Format.SampleRate);
        Assert.Equal(channels, options.Format.Channels);
        using var encoder = CreateEncoder();
        await using var session = await encoder.StartAsync(new(profile, server.Target.Password),
            cancellationToken: TestContext.Current.CancellationToken);
        var frame = TonePacket(rate, channels);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < 50; i++)
        {
            await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
            session.Submit(frame);
        }
        await session.StopAsync(TestContext.Current.CancellationToken);
        await server.Done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var header = await server.Header;
        Assert.Contains($"Ice-Bitrate: {bitrate}\r\n", header, StringComparison.Ordinal);
        Assert.Contains($"Ice-Audio-Info: bitrate={bitrate};samplerate={rate};channels={channels}\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Name: Studio radio\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Description: Local programming\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-Genre: Talk\r\n", header, StringComparison.Ordinal);
        Assert.Contains("Ice-URL: https://example.com/studio\r\n", header, StringComparison.Ordinal);
        await AssertDecodableAsync(server.Audio.ToArray(), 0.95, rate, channels);
        Assert.InRange(server.Audio.Length, bitrate * 1000 / 8 * 0.95, bitrate * 1000 / 8 * 1.2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bundled_encoder_produces_decodable_MP3_and_graceful_stop_reaps_child(bool finalAcknowledgement)
    {
        RequireBundle();
        await using var server = new FakeIcecast(finalAcknowledgement: finalAcknowledgement);
        using var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        await FeedToneAsync(session, 4);
        Assert.Equal(EncoderState.Streaming, session.Snapshot.State);
        Assert.True(session.Snapshot.EncodedBytes > 1000);
        await Task.WhenAll(session.StopAsync(TestContext.Current.CancellationToken), session.StopAsync(TestContext.Current.CancellationToken));
        await session.DisposeAsync();
        Assert.Equal(EncoderState.Stopped, session.Snapshot.State);
        Assert.True(session.Completion.IsCompletedSuccessfully);
        await server.Done.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var header = await server.Header;
        Assert.Contains("Content-Type: audio/mpeg", header, StringComparison.Ordinal);
        Assert.Contains("Authorization: Basic ", header, StringComparison.Ordinal);
        await AssertDecodableAsync(server.Audio.ToArray(), 3.9);
    }

    [Fact]
    public async Task Host_owner_disposal_stops_active_stream_even_without_session_disposal()
    {
        RequireBundle();
        await using var server = new FakeIcecast();
        var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        await FeedToneAsync(session, 2);
        encoder.Dispose();
        encoder.Dispose();
        await session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(EncoderState.Stopped, session.Snapshot.State);
        await server.Done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Server_disconnect_is_failure_and_no_new_audio_is_accepted()
    {
        RequireBundle();
        await using var server = new FakeIcecast(disconnectAfterBytes: 1024);
        using var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        try
        {
            await FeedToneAsync(session, 3);
        }
        catch (IOException) { }
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.DoesNotContain(server.Target.Password, error.Message, StringComparison.Ordinal);
        Assert.Equal(EncoderState.Failed, session.Snapshot.State);
        Assert.Throws<IOException>(() => session.Submit(TonePacket()));
        await Assert.ThrowsAnyAsync<IOException>(() => session.DisposeAsync().AsTask());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Format_change_and_two_second_queue_overrun_fail_explicitly(bool formatChange)
    {
        RequireBundle();
        await using var server = new FakeIcecast();
        using var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        var frame = formatChange ? new PcmFrame(new(44100, 1), new byte[4])
            : new PcmFrame(new(48000, 2), new byte[48000 * 8 * 2 + 8]);
        var error = Assert.ThrowsAny<IOException>(() => session.Submit(frame));
        Assert.Contains(formatChange ? "format" : "overrun", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await Assert.ThrowsAnyAsync<IOException>(() => session.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Nonfinite_PCM_is_rejected_before_native_encoding()
    {
        RequireBundle();
        await using var server = new FakeIcecast();
        using var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        var bytes = new byte[8];
        BitConverter.GetBytes(float.NaN).CopyTo(bytes, 0);
        var error = Assert.ThrowsAny<IOException>(() => session.Submit(new(new(48000, 2), bytes)));
        Assert.Contains("non-finite", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<IOException>(() => session.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task A_second_active_encoder_is_rejected_without_reserving_another_mount()
    {
        RequireBundle();
        await using var server = new FakeIcecast();
        using var encoder = CreateEncoder();
        var session = await encoder.StartAsync(server.Target, cancellationToken: TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<InvalidOperationException>(() => encoder.StartAsync(server.Target,
            cancellationToken: TestContext.Current.CancellationToken));
        await FeedToneAsync(session, 1);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task A_stuck_child_is_killed_after_graceful_stop_timeout()
    {
        await using var server = new FakeIcecast();
        var connection = await IcecastSourceConnection.ConnectAsync(server.Target, TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("ping -n 30 127.0.0.1 >nul");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("sleep 30");
        }
        var process = Process.Start(start)!;
        var pid = process.Id;
        var session = new FfmpegSession(process, connection, new());
        var stopwatch = Stopwatch.StartNew();
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.StopAsync(TestContext.Current.CancellationToken));
        Assert.Contains("five seconds", error.Message, StringComparison.Ordinal);
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 4.5, 12);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        await Assert.ThrowsAnyAsync<IOException>(() => session.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task Unexpected_child_exit_is_a_failure_and_releases_the_mount()
    {
        await using var server = new FakeIcecast();
        var connection = await IcecastSourceConnection.ConnectAsync(server.Target, TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh")
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        if (OperatingSystem.IsWindows())
        {
            start.ArgumentList.Add("/d");
            start.ArgumentList.Add("/c");
            start.ArgumentList.Add("exit 2");
        }
        else
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("exit 2");
        }
        var session = new FfmpegSession(Process.Start(start)!, connection, new());
        var error = await Assert.ThrowsAnyAsync<IOException>(() => session.Completion.WaitAsync(TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken));
        Assert.Contains("exited unexpectedly", error.Message, StringComparison.Ordinal);
        Assert.True(BroadcastController.CanRetry(error));
        await Assert.ThrowsAnyAsync<IOException>(() => session.DisposeAsync().AsTask());
        await server.Done.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Abrupt_parent_termination_closes_pipes_and_FFmpeg_exits_without_an_orphan()
    {
        RequireBundle();
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "probe", "EncoderParentProbe.dll"));
        start.ArgumentList.Add(new FfmpegExecutable().Path);
        using var parent = Process.Start(start)!;
        Process? child = null;
        try
        {
            var line = await parent.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(int.TryParse(line, out var pid), $"Probe did not return a child PID: {line}");
            child = Process.GetProcessById(pid);
            await Task.Delay(500, TestContext.Current.CancellationToken);
            Assert.False(child.HasExited);
            parent.Kill(); // Deliberately kill only the parent, not its child tree.
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            await child.WaitForExitAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.True(child.HasExited);
        }
        finally
        {
            if (!parent.HasExited)
            {
                parent.Kill(entireProcessTree: true);
                await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
            if (child is not null)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                    await child.WaitForExitAsync(TestContext.Current.CancellationToken);
                }
                child.Dispose();
            }
        }
    }

    internal static FfmpegBroadcastEncoder CreateEncoder() => new(new FfmpegExecutable(),
        NullLogger<FfmpegBroadcastEncoder>.Instance);

    internal static void RequireBundle()
    {
        if (!File.Exists(new FfmpegExecutable().Path))
        {
            Assert.NotEqual("1", Environment.GetEnvironmentVariable("TC_REQUIRE_FFMPEG"));
            Assert.Skip("Build the FFmpeg bundle for this platform before native encoder tests.");
        }
    }

    internal static PcmFrame TonePacket(int rate = 48000, int channels = 2)
    {
        var frames = rate / 50;
        var samples = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            for (var channel = 0; channel < channels; channel++)
            {
                samples[i * channels + channel] = (float)(0.2 * Math.Sin(2 * Math.PI * 600 * i / rate));
            }
        }
        return new(new(rate, channels), MemoryMarshal.AsBytes(samples.AsSpan()).ToArray());
    }

    internal static async Task FeedToneAsync(IEncoderSession session, int seconds)
    {
        var frame = TonePacket();
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        for (var i = 0; i < seconds * 50; i++)
        {
            await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
            session.Submit(frame);
        }
    }

    internal static async Task AssertDecodableAsync(byte[] mp3, double minimumSeconds, int sampleRate = 48000, int channels = 2)
    {
        var start = new ProcessStartInfo(new FfmpegExecutable().Path)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in new[] { "-hide_banner", "-loglevel", "error", "-f", "mp3", "-i", "pipe:0",
            "-c:a", "pcm_s16le", "-f", "wav", "pipe:1" })
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        using var decoded = new MemoryStream();
        var read = process.StandardOutput.BaseStream.CopyToAsync(decoded, TestContext.Current.CancellationToken);
        var diagnostics = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(mp3, TestContext.Current.CancellationToken);
            process.StandardInput.Close();
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            await read;
            Assert.Equal(0, process.ExitCode);
            Assert.Equal("", await diagnostics);
            var bytes = decoded.ToArray();
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
            var offset = 12;
            var foundFormat = false;
            var foundData = false;
            while (offset + 8 <= bytes.Length)
            {
                var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 4));
                var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                if (id == "fmt ")
                {
                    Assert.Equal(channels, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset + 10)));
                    Assert.Equal(sampleRate, (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + 12)));
                    foundFormat = true;
                }
                if (id == "data")
                {
                    Assert.True(bytes.Length - offset - 8 > minimumSeconds * sampleRate * channels * 2);
                    Assert.Contains(bytes.AsSpan(offset + 8).ToArray(), b => b != 0);
                    foundData = true;
                    break;
                }
                offset += checked(8 + (int)size + (int)(size % 2));
            }
            Assert.True(foundFormat);
            Assert.True(foundData);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            }
        }
    }
}

internal sealed class FakeIcecast : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(30));
    private readonly TaskCompletionSource<string> _header = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal BroadcastTarget Target { get; }
    internal MemoryStream Audio { get; } = new();
    internal Task Done { get; }
    internal Task<string> Header => _header.Task;

    internal FakeIcecast(int status = 100, int? disconnectAfterBytes = null, bool finalAcknowledgement = false)
    {
        _listener.Start();
        Target = new(new(Guid.NewGuid(), "Test source", "127.0.0.1",
            ((IPEndPoint)_listener.LocalEndpoint).Port, "/test.mp3", SampleRate: 48000), "unique-test-password");
        Done = ServeAsync(status, disconnectAfterBytes, finalAcknowledgement);
    }

    private async Task ServeAsync(int status, int? disconnectAfterBytes, bool finalAcknowledgement)
    {
        using var client = await _listener.AcceptTcpClientAsync(_shutdown.Token);
        await using var stream = client.GetStream();
        var text = new StringBuilder();
        var singleByte = new byte[1];
        while (!text.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            Assert.NotEqual(0, await stream.ReadAsync(singleByte, _shutdown.Token));
            text.Append((char)singleByte[0]);
        }
        _header.SetResult(text.ToString());
        await stream.WriteAsync(System.Text.Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Test\r\n\r\n"), _shutdown.Token);
        if (finalAcknowledgement)
        {
            await stream.WriteAsync("HTTP/1.0 200 OK\r\n\r\n"u8.ToArray(), _shutdown.Token);
        }
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, _shutdown.Token)) != 0)
        {
            Audio.Write(buffer, 0, count);
            if (disconnectAfterBytes is { } limit && Audio.Length >= limit)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        try
        {
            await Done;
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        Audio.Dispose();
        _shutdown.Dispose();
    }
}
