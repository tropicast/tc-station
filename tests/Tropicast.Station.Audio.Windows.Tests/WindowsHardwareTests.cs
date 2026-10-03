using System.Diagnostics;

namespace Tropicast.Station.Audio.Windows.Tests;

public sealed class WindowsHardwareTests
{
    [Fact]
    public async Task Selected_windows_input_and_loopback_capture_for_qualification_duration()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("TC_TEST_WASAPI") != "1")
        {
            Assert.Skip("Opt-in Windows hardware test: TC_TEST_WASAPI=1 plus TC_WASAPI_INPUT_ID and TC_WASAPI_LOOPBACK_ID.");
            return;
        }

        using var provider = new WindowsAudioCaptureProvider();
        var devices = await provider.GetDevicesAsync(TestContext.Current.CancellationToken);
        foreach (var device in devices)
        {
            TestContext.Current.TestOutputHelper?.WriteLine($"{device.Id}: {device.Label} ({device.Kind})");
        }

        var duration = int.Parse(Environment.GetEnvironmentVariable("TC_WASAPI_DURATION_SECONDS") ?? "1800",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(duration, 5, 1800);
        foreach (var (variable, kind) in new[]
        {
            ("TC_WASAPI_INPUT_ID", AudioDeviceKind.Input),
            ("TC_WASAPI_LOOPBACK_ID", AudioDeviceKind.Loopback),
        })
        {
            var id = Environment.GetEnvironmentVariable(variable);
            var device = Assert.Single(devices, d => d.Id == id && d.Kind == kind);
            await using var session = await provider.StartAsync(device.Id, TestContext.Current.CancellationToken);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(duration));
            var stopwatch = Stopwatch.StartNew();
            long totalFrames = 0;
            try
            {
                await foreach (var frame in session.ReadFramesAsync(deadline.Token))
                {
                    Assert.Equal(device.NativeFormat, frame.Format);
                    totalFrames += frame.FrameCount;
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                // The qualification window, not capture failure, ended the read.
            }

            await session.StopAsync(TestContext.Current.CancellationToken);
            Assert.True(stopwatch.Elapsed.TotalSeconds >= duration * 0.98, "Capture ended before the qualification duration.");
            Assert.True(totalFrames >= (long)(duration * device.NativeFormat.SampleRate * 0.98),
                "Less than 98% of expected PCM received. Feed the input and keep playback active throughout loopback qualification.");
            TestContext.Current.TestOutputHelper?.WriteLine($"{kind}: {totalFrames} frames captured in {stopwatch.Elapsed}.");
        }
    }
}
