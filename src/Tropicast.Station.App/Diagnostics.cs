using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Tropicast.Station.Audio;
using Tropicast.Station.Core;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;

namespace Tropicast.Station.App;

public sealed partial class Diagnostics(SafeLogProvider logs, IAppInfo appInfo, AudioCaptureService capture,
    BroadcastController broadcast, ILogger<Diagnostics> logger)
{
    public string LogDirectory => logs.DirectoryPath;
    public bool HasLogFailure => logs.HasWriteFailure;

    internal void ReportExportFailure(Exception error)
    {
        var type = error.GetType().Name;
        LogExportFailure(logger, type);
    }

    public async Task ExportAsync(Stream destination, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        var recent = logs.ReadRecent();
        var devices = capture.Snapshot.Devices.Select((device, index) => new
        {
            Index = index + 1, Kind = device.Kind.ToString(), device.IsDefault,
            device.NativeFormat.SampleRate, device.NativeFormat.Channels, Encoding = device.NativeFormat.Encoding.ToString(),
        }).ToArray();
        var snapshot = broadcast.Snapshot;
        var manifest = new
        {
            AppVersion = appInfo.Version.ToString(), OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.OSArchitecture.ToString(), Runtime = Environment.Version.ToString(),
            CreatedUtc = DateTimeOffset.UtcNow, Devices = devices,
            Broadcast = new
            {
                State = snapshot.State.ToString(), snapshot.ReconnectCount, snapshot.RetryAttempt,
                DowntimeSeconds = snapshot.Downtime.TotalSeconds, ElapsedSeconds = snapshot.Elapsed.TotalSeconds,
            },
            LogWriteFailure = logs.HasWriteFailure,
            Privacy = "No profiles, credentials, endpoints, metadata, device names/IDs, paths, raw exception text, command lines or audio.",
        };
        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        await using (var entry = archive.CreateEntry("diagnostics.json").Open())
        {
            await JsonSerializer.SerializeAsync(entry, manifest, cancellationToken: token).ConfigureAwait(false);
        }
        await using (var entry = archive.CreateEntry("logs/recent.jsonl").Open())
        {
            foreach (var record in recent)
            {
                await JsonSerializer.SerializeAsync(entry, record, cancellationToken: token).ConfigureAwait(false);
                await entry.WriteAsync(new byte[] { (byte)'\n' }, token).ConfigureAwait(false);
            }
        }
        LogExport(logger, devices.Length);
    }

    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Diagnostics exported ({DeviceCount} devices).")]
    private static partial void LogExport(ILogger logger, int deviceCount);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Warning, Message = "Diagnostic export failed ({ErrorType}).")]
    private static partial void LogExportFailure(ILogger logger, string errorType);
}
