using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tropicast.Station.Audio.Linux;

internal static partial class PulseDevices
{
    internal static IReadOnlyList<AudioDevice> Parse(string sourcesJson, string sinksJson, string infoJson)
    {
        try
        {
            using var sources = JsonDocument.Parse(sourcesJson);
            using var sinks = JsonDocument.Parse(sinksJson);
            using var info = JsonDocument.Parse(infoJson);
            var defaultSource = info.RootElement.GetProperty("default_source_name").GetString();
            var defaultSink = info.RootElement.GetProperty("default_sink_name").GetString();
            var monitors = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var sink in sinks.RootElement.EnumerateArray())
            {
                var monitor = sink.GetProperty("monitor_source");
                // PulseAudio versions may emit the monitor index rather than its name.
                var key = monitor.ValueKind == JsonValueKind.String ? RequiredString(monitor) : monitor.GetRawText();
                monitors[key] = RequiredString(sink.GetProperty("name"));
            }

            var result = new List<AudioDevice>();
            foreach (var source in sources.RootElement.EnumerateArray())
            {
                var name = RequiredString(source.GetProperty("name"));
                var description = RequiredString(source.GetProperty("description"));
                var specification = RequiredString(source.GetProperty("sample_specification"));
                var match = SampleSpecification().Match(specification);
                if (!match.Success || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(description))
                {
                    throw new IOException("PulseAudio returned an invalid source description or sample specification.");
                }

                var rate = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                var channels = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                var isMonitor = monitors.TryGetValue(name, out var sinkName)
                    || monitors.TryGetValue(source.GetProperty("index").GetRawText(), out sinkName)
                    || name.EndsWith(".monitor", StringComparison.Ordinal);
                var isDefault = isMonitor ? sinkName == defaultSink : name == defaultSource;
                // libpulse converts the server's native sample encoding to float32le for parec.
                result.Add(new(name, description, isMonitor ? AudioDeviceKind.Loopback : AudioDeviceKind.Input,
                    isDefault, new(rate, channels)));
            }

            if (result.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != result.Count)
            {
                throw new IOException("PulseAudio returned duplicate source names.");
            }

            return result;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException)
        {
            throw new IOException("Cannot read PulseAudio device information. Upgrade pactl and check the server configuration.");
        }
    }

    private static string RequiredString(JsonElement value)
        => value.GetString() ?? throw new IOException("PulseAudio returned a missing device description.");

    internal static bool IsDeviceEvent(string line)
        => line.Contains(" on source #", StringComparison.Ordinal)
            || line.Contains(" on sink #", StringComparison.Ordinal)
            || line.Contains(" on server #", StringComparison.Ordinal)
            || line.Contains(" on card #", StringComparison.Ordinal);

    [GeneratedRegex(@"^\S+ (\d+)ch (\d+)Hz$")]
    private static partial Regex SampleSpecification();
}
