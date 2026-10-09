using System.Diagnostics;
using System.Globalization;

namespace Tropicast.Station.Encoding;

internal sealed class FfmpegExecutable
{
    internal FfmpegExecutable(string? path = null)
        => Path = path ?? System.IO.Path.Combine(AppContext.BaseDirectory, "ffmpeg", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");

    internal string Path { get; }

    internal Process Start(EncoderOptions options)
    {
        if (!File.Exists(Path))
        {
            throw new EncoderException("Bundled FFmpeg is missing. Run scripts/build-ffmpeg.sh for this platform, then rebuild the app.");
        }
        var start = new ProcessStartInfo(Path)
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var argument in Arguments(options))
        {
            start.ArgumentList.Add(argument);
        }
        var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            return process;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            process.Dispose();
            throw new EncoderException("Bundled FFmpeg could not start. Check the platform/architecture and executable permissions.");
        }
    }

    internal static IReadOnlyList<string> Arguments(EncoderOptions options)
    {
        var bitrate = $"{options.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k";
        string[] input =
        [
            "-hide_banner", "-nostdin", "-loglevel", "warning", "-nostats",
            "-f", "f32le", "-ar", options.Format.SampleRate.ToString(CultureInfo.InvariantCulture),
            "-ac", options.Format.Channels.ToString(CultureInfo.InvariantCulture),
            "-probesize", "32", "-analyzeduration", "0",
            "-i", "pipe:0", "-map", "0:a:0",
        ];
        string[] output = options.Codec == EncoderCodec.Opus
            // Opus runs at 48 kHz; FFmpeg resamples 44.1 kHz capture.
            ? ["-c:a", "libopus", "-b:a", bitrate, "-application", "audio", "-ar", "48000",
                "-flush_packets", "1", "-f", "ogg", "pipe:1"]
            : ["-c:a", "libmp3lame", "-b:a", bitrate, "-flush_packets", "1", "-write_xing", "0", "-f", "mp3", "pipe:1"];
        return [.. input, .. output];
    }
}
