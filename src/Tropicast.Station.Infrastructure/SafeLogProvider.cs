using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tropicast.Station.Infrastructure;

public sealed record SafeLogRecord(DateTimeOffset Timestamp, LogLevel Level, string Category, int EventId,
    string ErrorType, IReadOnlyDictionary<string, double> Metrics);

/// <summary>
/// All sinks share a fail-closed projection: free text, scopes, exception diagnostics and
/// arbitrary structured values are never written. This also covers unknown/unregistered secrets.
/// </summary>
public sealed class SafeLogProvider : ILoggerProvider
{
    public const int MaximumFileBytes = 1024 * 1024;
    public const int RetainedFiles = 10;
    private static readonly HashSet<string> Categories =
    [
        "Microsoft.Hosting.Lifetime",
        "Tropicast.Station.Audio.AudioCaptureService",
        "Tropicast.Station.Encoding.BroadcastController",
        "Tropicast.Station.Encoding.FfmpegBroadcastEncoder",
        "Tropicast.Station.App.ViewModels.ProfileEditorViewModel",
        "Tropicast.Station.App.Diagnostics",
    ];
    private static readonly HashSet<string> ErrorTypes =
    [
        "IOException", "InvalidOperationException", "ArgumentException", "ArgumentOutOfRangeException",
        "UnauthorizedAccessException", "SecretStoreException", "TropicastSourceException", "EncoderException",
        "OperationCanceledException", "TaskCanceledException", "AggregateException", "SocketException",
        "NullReferenceException", "ObjectDisposedException", "TimeoutException",
        "BroadcastInterrupted", "TransientConnectionFailure",
    ];
    private static readonly HashSet<string> MetricNames =
    [
        "State", "ReconnectCount", "RetryAttempt", "DowntimeSeconds", "ElapsedSeconds", "DeviceCount",
    ];
    private readonly object _sync = new();
    private readonly TextWriter? _console;
    private readonly string _session = Guid.NewGuid().ToString("N");
    private readonly int _maximumBytes;
    private readonly int _retainedFiles;
    private string? _path;
    private int _sequence;
    private bool _disposed;
    private int _writeFailed;

    public SafeLogProvider(string directory, TextWriter? console = null,
        int maximumBytes = MaximumFileBytes, int retainedFiles = RetainedFiles)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(retainedFiles, 1);
        DirectoryPath = directory;
        _console = console;
        _maximumBytes = maximumBytes;
        _retainedFiles = retainedFiles;
    }

    public string DirectoryPath { get; }
    public bool HasWriteFailure => Volatile.Read(ref _writeFailed) != 0;

    public static string DefaultDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "Tropicast", "Station")
        : OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tropicast", "Station", "logs")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") is { Length: > 0 } state && Path.IsPathRooted(state)
                ? state : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"),
                "Tropicast", "Station", "logs");

    public ILogger CreateLogger(string categoryName) => new SafeLogger(this, categoryName);

    private static SafeLogRecord Project(SafeLogRecord record)
        => record with
        {
            Level = Enum.IsDefined(record.Level) ? record.Level : LogLevel.Error,
            Category = Categories.Contains(record.Category) ? record.Category : "Other",
            ErrorType = ErrorTypes.Contains(record.ErrorType) ? record.ErrorType : record.ErrorType.Length == 0 ? "" : "UnexpectedException",
            Metrics = record.Metrics.Where(pair => MetricNames.Contains(pair.Key) && double.IsFinite(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value),
        };

    private void Write(SafeLogRecord record)
    {
        var line = JsonSerializer.Serialize(Project(record)) + "\n";
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            try
            {
                Directory.CreateDirectory(DirectoryPath);
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                if (_path is null || !File.Exists(_path) || new FileInfo(_path).Length + Encoding.UTF8.GetByteCount(line) > _maximumBytes)
                {
                    _path = Path.Combine(DirectoryPath, $"station-{DateTime.UtcNow:yyyyMMdd}-{_session}-{_sequence++:D4}.jsonl");
                    Prune();
                }
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read | FileShare.Delete };
                if (!OperatingSystem.IsWindows())
                {
                    options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                }
                using var output = new FileStream(_path, options);
                output.Write(Encoding.UTF8.GetBytes(line));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _path = null;
                Interlocked.Exchange(ref _writeFailed, 1);
                ReportSinkFailure();
            }
            try
            {
                _console?.Write(line);
            }
            catch (IOException)
            {
                Interlocked.Exchange(ref _writeFailed, 1);
                ReportSinkFailure();
            }
        }
    }

    private static void ReportSinkFailure()
    {
        try
        {
            Console.Error.WriteLine("Tropicast could not write diagnostic logs. Check the user log directory permissions and free space.");
        }
        catch (IOException)
        {
            // A disconnected stderr cannot report itself; HasWriteFailure remains visible to the UI/export.
        }
    }

    private void Prune()
    {
        foreach (var file in Directory.GetFiles(DirectoryPath, "station-*.jsonl")
            .OrderByDescending(File.GetLastWriteTimeUtc).Skip(_retainedFiles - 1))
        {
            File.Delete(file);
        }
    }

    /// <summary>Re-projects recent records so manually edited log text cannot leak into exports.</summary>
    public IReadOnlyList<SafeLogRecord> ReadRecent()
    {
        lock (_sync)
        {
            if (!Directory.Exists(DirectoryPath))
            {
                return [];
            }
            var records = new List<SafeLogRecord>();
            foreach (var file in Directory.GetFiles(DirectoryPath, "station-*.jsonl")
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(5).Reverse())
            {
                if (new FileInfo(file).Length > MaximumFileBytes)
                {
                    throw new IOException("A diagnostic log exceeds the allowed export size.");
                }
                foreach (var line in File.ReadLines(file))
                {
                    try
                    {
                        var record = JsonSerializer.Deserialize<SafeLogRecord>(line)
                            ?? throw new IOException("A diagnostic log contains an empty record.");
                        if (record.Metrics is null || record.Category is null || record.ErrorType is null)
                        {
                            throw new IOException("A diagnostic log contains invalid structured fields.");
                        }
                        records.Add(Project(record));
                    }
                    catch (JsonException)
                    {
                        throw new IOException("A diagnostic log is corrupt. Export was not created.");
                    }
                }
            }
            return records;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private sealed class SafeLogger(SafeLogProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var errorType = exception?.GetType().Name ?? "";
            var metrics = new Dictionary<string, double>();
            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var pair in fields)
                {
                    if (pair.Key == "ErrorType" && pair.Value is string type && ErrorTypes.Contains(type))
                    {
                        errorType = type;
                    }
                    else if (MetricNames.Contains(pair.Key) && pair.Value is byte or short or int or long or float or double)
                    {
                        metrics[pair.Key] = Convert.ToDouble(pair.Value, CultureInfo.InvariantCulture);
                    }
                }
            }
            owner.Write(new(DateTimeOffset.UtcNow, logLevel, category, eventId.Id, errorType, metrics));
        }
    }
}
