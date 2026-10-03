using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tropicast.Station.Audio.Linux.Tests;

public sealed class LinuxIntegrationTests
{
    [Fact]
    public async Task Synthetic_input_and_monitor_capture_restart_and_hotplug()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("TC_TEST_PULSE") != "1")
        {
            Assert.Skip("Opt-in PulseAudio/pipewire-pulse test: TC_TEST_PULSE=1 (pactl, parec and pacat required).");
            return;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var token = deadline.Token;
        var name = $"tc_test_{Guid.NewGuid():N}";
        var modules = new List<string>();
        string? previousSink = null, previousSource = null;
        using var provider = new LinuxAudioCaptureProvider();
        await using var service = new AudioCaptureService(provider, NullLogger<AudioCaptureService>.Instance);
        var notifications = 0;
        provider.DevicesChanged += (_, _) => Interlocked.Increment(ref notifications);
        await service.RefreshAsync(token);
        try
        {
            modules.Add((await PulseProcess.CommandAsync(token, "load-module", "module-null-sink",
                $"sink_name={name}", "rate=48000", "channels=2")).Trim());
            modules.Add((await PulseProcess.CommandAsync(token, "load-module", "module-remap-source",
                $"master={name}.monitor", $"source_name={name}_input")).Trim());
            await UntilAsync(() => service.Snapshot.Devices.Any(d => d.Id == $"{name}_input"), token);
            var monitor = Assert.Single(service.Snapshot.Devices, d => d.Id == $"{name}.monitor");
            var input = Assert.Single(service.Snapshot.Devices, d => d.Id == $"{name}_input");
            Assert.Equal(AudioDeviceKind.Loopback, monitor.Kind);
            Assert.Equal(AudioDeviceKind.Input, input.Kind);
            Assert.True(Volatile.Read(ref notifications) > 0, "No topology notifications received.");

            if (Environment.GetEnvironmentVariable("TC_TEST_PULSE_DEFAULTS") == "1")
            {
                // Only enable on an isolated test server: never change desktop defaults in ordinary tests.
                using var info = JsonDocument.Parse(await PulseProcess.CommandAsync(token, "--format=json", "info"));
                previousSink = info.RootElement.GetProperty("default_sink_name").GetString();
                previousSource = info.RootElement.GetProperty("default_source_name").GetString();
                Assert.NotNull(previousSink);
                Assert.NotNull(previousSource);
                await PulseProcess.CommandAsync(token, "set-default-sink", name);
                await PulseProcess.CommandAsync(token, "set-default-source", input.Id);
                await UntilAsync(() => service.Snapshot.Devices.Any(d => d.Id == monitor.Id && d.IsDefault)
                    && service.Snapshot.Devices.Any(d => d.Id == input.Id && d.IsDefault), token);
                await PulseProcess.CommandAsync(token, "set-default-sink", previousSink);
                await PulseProcess.CommandAsync(token, "set-default-source", previousSource);
                await UntilAsync(() => service.Snapshot.Devices.Any(d => d.Id == monitor.Id && !d.IsDefault)
                    && service.Snapshot.Devices.Any(d => d.Id == input.Id && !d.IsDefault), token);
            }

            using var playback = StartPlayback(name);
            using var playbackCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            var writer = PlayToneAsync(playback.StandardInput.BaseStream, playbackCancellation.Token);
            try
            {
                foreach (var device in new[] { monitor, input })
                {
                    for (var cycle = 0; cycle < 2; cycle++)
                    {
                        await CheckToneAsync(provider, device, token);
                    }
                }

                await service.StartAsync(monitor.Id, cancellationToken: token);
                Assert.True(service.Snapshot.IsCapturing, service.Snapshot.Message);
                // Removing only our null sink must stop its pinned monitor, not switch to a personal source.
                await PulseProcess.CommandAsync(token, "unload-module", modules[1]);
                modules.RemoveAt(1);
                await PulseProcess.CommandAsync(token, "unload-module", modules[0]);
                modules.Clear();
                await UntilAsync(() => !service.Snapshot.IsCapturing
                    && service.Snapshot.Devices.All(d => d.Id != monitor.Id), token);
                Assert.Null(service.Snapshot.ActiveDeviceId);
                Assert.True(service.Snapshot.Message.Contains("disconnect", StringComparison.OrdinalIgnoreCase)
                    || service.Snapshot.Message.Contains("capture", StringComparison.OrdinalIgnoreCase), service.Snapshot.Message);
            }
            finally
            {
                playbackCancellation.Cancel();
                if (!playback.HasExited)
                {
                    playback.Kill(entireProcessTree: true);
                }
                await playback.WaitForExitAsync(TestContext.Current.CancellationToken);
                try
                {
                    await writer;
                }
                catch (OperationCanceledException) when (playbackCancellation.IsCancellationRequested) { }
                catch (IOException) when (playbackCancellation.IsCancellationRequested) { }
            }
        }
        finally
        {
            if (previousSink is not null)
            {
                await PulseProcess.CommandAsync(TestContext.Current.CancellationToken, "set-default-sink", previousSink);
            }
            if (previousSource is not null)
            {
                await PulseProcess.CommandAsync(TestContext.Current.CancellationToken, "set-default-source", previousSource);
            }
            foreach (var module in Enumerable.Reverse(modules))
            {
                await PulseProcess.CommandAsync(TestContext.Current.CancellationToken, "unload-module", module);
            }
        }
    }

    private static Process StartPlayback(string sink)
    {
        var start = new ProcessStartInfo("pacat")
        {
            UseShellExecute = false, RedirectStandardInput = true,
        };
        foreach (var argument in new[]
        {
            "--playback", "--raw", $"--device={sink}", "--format=float32le",
            "--rate=48000", "--channels=2", "--latency-msec=40",
        })
        {
            start.ArgumentList.Add(argument);
        }
        return Process.Start(start) ?? throw new IOException("Could not start synthetic playback.");
    }

    private static async Task PlayToneAsync(Stream output, CancellationToken token)
    {
        var samples = new float[48000 * 2];
        for (var i = 0; i < 48000; i++)
        {
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.2 * Math.Sin(2 * Math.PI * 600 * i / 48000));
        }
        var bytes = MemoryMarshal.AsBytes(samples.AsSpan()).ToArray();
        while (true)
        {
            await output.WriteAsync(bytes, token);
        }
    }

    private static async Task CheckToneAsync(LinuxAudioCaptureProvider provider, AudioDevice device, CancellationToken token)
    {
        await using var session = await provider.StartAsync(device.Id, token);
        await using var reader = session.ReadFramesAsync(token).GetAsyncEnumerator(token);
        long frames = 0, measured = 0;
        double energy = 0, sine = 0, cosine = 0;
        var stopwatch = Stopwatch.StartNew();
        while (frames < device.NativeFormat.SampleRate * 3)
        {
            Assert.True(await reader.MoveNextAsync());
            var frame = reader.Current;
            Assert.Equal(device.NativeFormat, frame.Format);
            Measure(frame, frames, ref measured, ref energy, ref sine, ref cosine);
            frames += frame.FrameCount;
        }
        await Task.WhenAll(session.StopAsync(token), session.StopAsync(token));
        Assert.InRange(stopwatch.Elapsed.TotalSeconds, 2, 10);
        var rms = Math.Sqrt(energy / measured);
        var amplitude = 2 * Math.Sqrt(sine * sine + cosine * cosine) / measured;
        Assert.InRange(rms, 0.12, 0.16);
        Assert.InRange(amplitude, 0.17, 0.23);
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{device.Kind}: {frames} frames, RMS {rms:F4}, 600 Hz amplitude {amplitude:F4}."));
    }

    private static void Measure(PcmFrame frame, long offset, ref long measured, ref double energy, ref double sine, ref double cosine)
    {
        var samples = MemoryMarshal.Cast<byte, float>(frame.Data.Span);
        for (var i = 0; i < frame.FrameCount; i++)
        {
            if (offset + i < frame.Format.SampleRate / 2)
            {
                continue;
            }
            var value = samples[i * frame.Format.Channels];
            Assert.True(float.IsFinite(value));
            var angle = 2 * Math.PI * 600 * (offset + i) / frame.Format.SampleRate;
            energy += value * value;
            sine += value * Math.Sin(angle);
            cosine += value * Math.Cos(angle);
            measured++;
        }
    }

    private static async Task UntilAsync(Func<bool> condition, CancellationToken token)
    {
        while (!condition())
        {
            await Task.Delay(50, token);
        }
    }
}
