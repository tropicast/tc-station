using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Tropicast.Station.Infrastructure.Tests;

public sealed class SafeLogTests
{
    [Fact]
    public void File_and_console_sinks_drop_free_text_credentials_scopes_and_exception_data()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-logs-{Guid.NewGuid():N}");
        using var console = new StringWriter();
        try
        {
            using var provider = new SafeLogProvider(directory, console);
            var logger = provider.CreateLogger("secret-category-sentinel");
            const string password = "configured-password-sentinel-13";
            using var scope = logger.BeginScope(password);
            logger.Log(LogLevel.Error, new EventId(42, password),
                new[]
                {
                    new KeyValuePair<string, object?>("Password", password),
                    new KeyValuePair<string, object?>("Url", $"https://source:{password}@server/live?token={password}"),
                    new KeyValuePair<string, object?>("Command", $"ffmpeg -password {password}"),
                    new KeyValuePair<string, object?>("ErrorType", "IOException"),
                    new KeyValuePair<string, object?>("State", 2),
                }, new IOException(password), (_, _) => throw new InvalidOperationException("Unsafe formatter must never run."));
            var file = Assert.Single(Directory.GetFiles(directory));
            var text = File.ReadAllText(file);
            Assert.Equal(text, console.ToString());
            Assert.DoesNotContain(password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-category-sentinel", text, StringComparison.Ordinal);
            Assert.DoesNotContain("https", text, StringComparison.Ordinal);
            Assert.DoesNotContain("ffmpeg", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("icecast", text, StringComparison.OrdinalIgnoreCase);
            var record = JsonSerializer.Deserialize<SafeLogRecord>(text)!;
            Assert.Equal("Other", record.Category);
            Assert.Equal("IOException", record.ErrorType);
            Assert.Equal(2, record.Metrics["State"]);
            Assert.Equal(42, record.EventId);
            Assert.False(provider.HasWriteFailure);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Rolling_files_stay_bounded_and_exports_reproject_tampered_text()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-logs-{Guid.NewGuid():N}");
        try
        {
            using var provider = new SafeLogProvider(directory, maximumBytes: 1024, retainedFiles: 3);
            var logger = provider.CreateLogger("Tropicast.Station.App.Diagnostics");
            for (var i = 0; i < 100; i++)
            {
                logger.Log(LogLevel.Information, new EventId(i), "private-unstructured", null, (_, _) => "private-unstructured");
            }
            var files = Directory.GetFiles(directory);
            Assert.InRange(files.Length, 2, 3);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 1024));
            var tampered = new SafeLogRecord(DateTimeOffset.UtcNow, LogLevel.Error, "password-in-category", 1303,
                "password-in-error", new Dictionary<string, double> { ["password-in-key"] = 1, ["RetryAttempt"] = 3 });
            File.WriteAllText(files[0], JsonSerializer.Serialize(tampered) + "\n");
            var records = provider.ReadRecent();
            Assert.NotEmpty(records);
            var exported = JsonSerializer.Serialize(records);
            Assert.DoesNotContain("password-in", exported, StringComparison.Ordinal);
            Assert.Contains("RetryAttempt", exported, StringComparison.Ordinal);
            File.WriteAllText(files[0], "corrupt secret log");
            Assert.Throws<IOException>(() => provider.ReadRecent());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Write_failure_is_visible_and_does_not_leak_the_exception_path()
    {
        var file = Path.Combine(Path.GetTempPath(), $"tc-log-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(file, "");
        try
        {
            using var console = new StringWriter();
            using var provider = new SafeLogProvider(file, console);
            provider.CreateLogger("Tropicast.Station.App.Diagnostics").Log(LogLevel.Error, new EventId(1),
                "password-in-message", null, (_, _) => "password-in-message");
            Assert.True(provider.HasWriteFailure);
            Assert.DoesNotContain(file, console.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("password-in-message", console.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
