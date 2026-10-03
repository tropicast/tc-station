using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tropicast.Station.Audio.MacOS;

public sealed class MacAudioCaptureProvider : IAudioCaptureProvider, IDisposable
{
    private readonly IMacAudioBackend _backend;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _sync = new();
    private Task? _watcher;
    private bool _disposed;

    public MacAudioCaptureProvider() : this(new CoreAudioBackend()) { }
    internal MacAudioCaptureProvider(IMacAudioBackend backend) => _backend = backend;

    public string Description => "macOS Core Audio inputs and ScreenCaptureKit system output (macOS 13+); virtual inputs such as BlackHole also supported.";
    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var devices = _backend.GetDevices();
                _watcher ??= Task.Run(WatchAsync);
                return devices;
            }
        }, cancellationToken);

    public async Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.Id == deviceId)
            ?? throw new IOException("The selected macOS audio source is no longer available.");
        return await Task.Run<IAudioCaptureSession>(() =>
        {
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                cancellationToken.ThrowIfCancellationRequested();
                var session = new MacCaptureSession(_backend.Open(device));
                session.Start();
                if (cancellationToken.IsCancellationRequested)
                {
                    session.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                return session;
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task WatchAsync()
    {
        IReadOnlyList<AudioDevice>? previous = null;
        var failed = false;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false))
            {
                IReadOnlyList<AudioDevice> devices;
                try
                {
                    lock (_sync)
                    {
                        if (_disposed)
                        {
                            return;
                        }
                        devices = _backend.GetDevices();
                    }
                }
                catch (IOException)
                {
                    if (!failed)
                    {
                        DevicesChanged?.Invoke(this, EventArgs.Empty);
                    }
                    failed = true;
                    continue;
                }
                if (failed || previous is null || !previous.SequenceEqual(devices))
                {
                    DevicesChanged?.Invoke(this, EventArgs.Empty);
                }
                failed = false;
                previous = devices;
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public void Dispose()
    {
        Task? watcher;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _shutdown.Cancel();
            watcher = _watcher;
        }
        watcher?.GetAwaiter().GetResult();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMacAudioCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAudioCaptureProvider, MacAudioCaptureProvider>();
        return services;
    }
}
