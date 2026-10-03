using System.IO.Compression;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Tropicast.Station.App.ViewModels;
using Tropicast.Station.App.Views;
using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;
using Tropicast.Station.Encoding;
using Tropicast.Station.Infrastructure;
using Tropicast.Station.Tests;

namespace Tropicast.Station.App.Tests;

public sealed class DiagnosticsTests
{
    [AvaloniaFact]
    public async Task Live_session_export_has_versions_devices_and_logs_but_no_credentials_or_private_names()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-diagnostics-{Guid.NewGuid():N}");
        using var console = new StringWriter();
        using var logs = new SafeLogProvider(directory, console);
        var profile = TestProfiles.Valid() with { StreamName = "private-stream-sentinel" };
        const string password = "diagnostic-live-password-sentinel";
        var secrets = new MemorySecrets();
        secrets.Items[profile.Id] = password;
        try
        {
            using var host = AppHost.Create(["--demo-audio"], services =>
            {
                services.AddSingleton(logs);
                services.AddSingleton<IProfileStore>(new MemoryProfiles { Items = [profile] });
                services.AddSingleton<ISecretStore>(secrets);
                services.AddSingleton<IBroadcastEncoder, LiveEncoder>();
            });
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Show();
            var model = host.Services.GetRequiredService<MainViewModel>();
            await model.Profiles.LoadCommand.ExecuteAsync(null);
            await model.Audio.RefreshCommand.ExecuteAsync(null);
            model.Profiles.SelectedProfile = profile;
            model.Audio.SelectedInput = model.Audio.Inputs[0];
            await model.Broadcast.GoLiveCommand.ExecuteAsync(null);
            await UntilAsync(() => model.Broadcast.State == BroadcastState.Live);
            using var bundle = new MemoryStream();
            await host.Services.GetRequiredService<Diagnostics>().ExportAsync(bundle, TestContext.Current.CancellationToken);
            bundle.Position = 0;
            using var archive = new ZipArchive(bundle, ZipArchiveMode.Read);
            Assert.Equal(2, archive.Entries.Count);
            using var manifest = new StreamReader(archive.GetEntry("diagnostics.json")!.Open());
            var manifestText = await manifest.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var document = JsonDocument.Parse(manifestText);
            Assert.Equal(2, document.RootElement.GetProperty("Devices").GetArrayLength());
            Assert.Equal("Live", document.RootElement.GetProperty("Broadcast").GetProperty("State").GetString());
            Assert.True(document.RootElement.TryGetProperty("AppVersion", out _));
            Assert.True(document.RootElement.TryGetProperty("OS", out _));
            using var recent = new StreamReader(archive.GetEntry("logs/recent.jsonl")!.Open());
            var all = manifestText + await recent.ReadToEndAsync(TestContext.Current.CancellationToken)
                + console + string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText));
            foreach (var privateText in new[] { password, profile.StreamName, profile.Name, profile.Mount, "demo-input", "Demo microphone" })
            {
                Assert.DoesNotContain(privateText, all, StringComparison.Ordinal);
            }
            Assert.DoesNotContain("icecast", all, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"EventId\":1302", all, StringComparison.Ordinal);
            await host.Services.GetRequiredService<BroadcastController>().StopAsync(TestContext.Current.CancellationToken);
            Dispatcher.UIThread.RunJobs();
            window.Close();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public async Task Dispatcher_error_stops_capture_displays_safe_dialog_and_requests_shutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-errors-{Guid.NewGuid():N}");
        using var logs = new SafeLogProvider(directory);
        try
        {
            using var host = AppHost.Create(["--demo-audio"], services => services.AddSingleton(logs));
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Show();
            var audio = host.Services.GetRequiredService<AudioDevicesViewModel>();
            await audio.RefreshCommand.ExecuteAsync(null);
            audio.SelectedInput = audio.Inputs[0];
            await audio.StartCommand.ExecuteAsync(null);
            var errors = host.Services.GetRequiredService<UnhandledErrors>();
            errors.Owner = window;
            var shutdown = false;
            errors.Shutdown = () => shutdown = true;
            errors.Attach();
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException("password-in-unhandled-error"));
            Dispatcher.UIThread.RunJobs();
            await UntilAsync(() => window.OwnedWindows.Count > 0);
            Assert.False(host.Services.GetRequiredService<AudioCaptureService>().Snapshot.IsCapturing);
            var dialog = Assert.Single(window.OwnedWindows);
            var content = Assert.IsType<StackPanel>(dialog.Content);
            var message = Assert.IsType<TextBlock>(content.Children[0]).Text!;
            Assert.Contains("restart", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("password-in-unhandled-error", message, StringComparison.Ordinal);
            dialog.Close();
            await UntilAsync(() => shutdown);
            errors.Dispose();
            errors.Dispose();
            Assert.DoesNotContain("password-in-unhandled-error", string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText)), StringComparison.Ordinal);
            window.Close();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20, deadline.Token);
        }
    }

    [AvaloniaFact]
    public async Task Corrupt_logs_fail_export_before_writing_and_diagnostics_button_is_accessible()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"tc-export-errors-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        using var logs = new SafeLogProvider(directory);
        try
        {
            using var host = AppHost.Create(["--demo-audio"], services => services.AddSingleton(logs));
            var window = host.Services.GetRequiredService<MainWindow>();
            window.Show();
            var button = window.FindControl<Button>("ExportDiagnosticsButton")!;
            Assert.Equal("Export diagnostics", Avalonia.Automation.AutomationProperties.GetName(button));
            Assert.Contains(directory, window.FindControl<TextBlock>("LogDirectory")!.Text!, StringComparison.Ordinal);
            await File.WriteAllTextAsync(Path.Combine(directory, "station-corrupt.jsonl"), "credential-bearing bad record",
                TestContext.Current.CancellationToken);
            using var destination = new MemoryStream();
            await Assert.ThrowsAsync<IOException>(() => host.Services.GetRequiredService<Diagnostics>()
                .ExportAsync(destination, TestContext.Current.CancellationToken));
            Assert.Equal(0, destination.Length);
            window.Close();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class LiveEncoder : IBroadcastEncoder
    {
        public Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default)
            => Task.FromResult<IEncoderSession>(new Session());
        private sealed class Session : IEncoderSession
        {
            private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public EncoderSnapshot Snapshot { get; private set; } = new(EncoderState.Starting, "Starting", 0);
            public Task Completion => _completion.Task;
            public void Submit(PcmFrame frame) => Snapshot = new(EncoderState.Streaming, "Publishing", frame.Data.Length);
            public Task StopAsync(CancellationToken cancellationToken = default)
            {
                _completion.TrySetResult();
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync() => new(StopAsync(TestContext.Current.CancellationToken));
        }
    }
}
