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
            throw new IOException("Bundled FFmpeg is missing. Run scripts/build-ffmpeg.sh for this platform, then rebuild the app.");
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
            throw new IOException("Bundled FFmpeg could not start. Check the platform/architecture and executable permissions.");
        }
    }

    internal static IReadOnlyList<string> Arguments(EncoderOptions options) =>
    [
        "-hide_banner", "-nostdin", "-loglevel", "warning", "-nostats",
        "-f", "f32le", "-ar", options.Format.SampleRate.ToString(CultureInfo.InvariantCulture),
        "-ac", options.Format.Channels.ToString(CultureInfo.InvariantCulture),
        "-probesize", "32", "-analyzeduration", "0",
        "-i", "pipe:0", "-map", "0:a:0", "-c:a", "libmp3lame",
        "-b:a", $"{options.BitrateKbps.ToString(CultureInfo.InvariantCulture)}k",
        "-flush_packets", "1", "-write_xing", "0", "-f", "mp3", "pipe:1",
    ];
}
