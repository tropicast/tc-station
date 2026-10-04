using System.Diagnostics;
using System.Net;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.E2E.Tests;

internal static class Media
{
    internal static string FfmpegPath { get; } = Path.Combine(AppContext.BaseDirectory, "ffmpeg",
        OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    /// <summary>Connects like a listener and returns at least <paramref name="seconds"/> of audio, proving it decodes.</summary>
    internal static async Task AssertListenerHearsAudioAsync(ConnectionProfile profile, int seconds, CancellationToken token)
    {
        using var http = new HttpClient();
        using var response = await http.GetAsync(profile.Endpoint, HttpCompletionOption.ResponseHeadersRead, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("audio/mpeg", response.Content.Headers.ContentType?.MediaType);
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var captured = new MemoryStream();
        var buffer = new byte[8192];
        while (captured.Length < profile.BitrateKbps * 1000 / 8 * seconds)
        {
            var count = await stream.ReadAsync(buffer, token);
            Assert.True(count > 0, "The listener stream ended before enough audio arrived.");
            captured.Write(buffer, 0, count);
        }
        await AssertDecodableAsync(captured.ToArray(), seconds - 0.3, profile, token);
    }

    internal static async Task AssertDecodableAsync(byte[] mp3, double minimumSeconds, ConnectionProfile profile, CancellationToken token)
    {
        var start = new ProcessStartInfo(FfmpegPath)
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
        var read = process.StandardOutput.BaseStream.CopyToAsync(decoded, token);
        var diagnostics = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.StandardInput.BaseStream.WriteAsync(mp3, token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
            await read;
            Assert.True(process.ExitCode == 0, $"ffmpeg could not decode the listener audio: {await diagnostics}");
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        // 44-byte WAV header followed by 16-bit samples at the profile's rate and channel count.
        var seconds = (decoded.Length - 44) / (profile.SampleRate * (double)profile.Channels * 2);
        Assert.True(seconds >= minimumSeconds, $"Decoded {seconds:F2}s of audio, expected at least {minimumSeconds:F2}s.");
        var samples = decoded.ToArray().AsSpan(44);
        var peak = 0;
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            peak = Math.Max(peak, Math.Abs(BitConverter.ToInt16(samples.Slice(i, 2))));
        }
        Assert.True(peak > 1000, $"The decoded audio is silent (peak {peak}); the tone did not reach the listener.");
    }
}
