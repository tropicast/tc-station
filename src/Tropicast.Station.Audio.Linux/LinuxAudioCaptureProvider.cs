using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tropicast.Station.Audio.Linux;

/// <summary>PulseAudio protocol through libpulse's pactl/parec clients, including pipewire-pulse.</summary>
public sealed class LinuxAudioCaptureProvider : IAudioCaptureProvider, IDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _subscription;
    private string? _subscriptionError;
    private bool _disposed;

    public string Description => "Linux PulseAudio/PipeWire: microphone/mixer sources and playback sink monitors.";
    public event EventHandler? DevicesChanged;

    public async Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        EnsureSubscription();
        var sources = PulseProcess.CommandAsync(cancellationToken, "--format=json", "list", "sources");
        var sinks = PulseProcess.CommandAsync(cancellationToken, "--format=json", "list", "sinks");
        var info = PulseProcess.CommandAsync(cancellationToken, "--format=json", "info");
        await Task.WhenAll(sources, sinks, info).ConfigureAwait(false);
        if (Volatile.Read(ref _subscriptionError) is { } error)
        {
            throw new IOException(error);
        }

        return PulseDevices.Parse(await sources.ConfigureAwait(false), await sinks.ConfigureAwait(false), await info.ConfigureAwait(false));
    }

    public async Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        var devices = await GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        var device = devices.FirstOrDefault(d => d.Id == deviceId)
            ?? throw new IOException("The selected Linux audio source is no longer available.");
        cancellationToken.ThrowIfCancellationRequested();
        return new PulseCaptureSession(device);
    }

    private void EnsureSubscription()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("PulseAudio capture requires Linux.");
        }

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _subscription ??= Task.Run(() => SubscribeAsync(_shutdown.Token));
        }
    }

    private async Task SubscribeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var child = new PulseProcess("pactl", "subscribe");
                Volatile.Write(ref _subscriptionError, null);
                DevicesChanged?.Invoke(this, EventArgs.Empty); // Refresh after reconnect.
                while (await child.Lines.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    if (PulseDevices.IsDeviceEvent(line))
                    {
                        DevicesChanged?.Invoke(this, EventArgs.Empty);
                    }
                }

                await child.WaitAsync(cancellationToken).ConfigureAwait(false);
                throw new IOException("PulseAudio device notifications stopped. Check the audio server; reconnecting.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException ex)
            {
                Volatile.Write(ref _subscriptionError, ex.Message);
                DevicesChanged?.Invoke(this, EventArgs.Empty);
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public void Dispose()
    {
        Task? subscription;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
            subscription = _subscription;
        }

        subscription?.GetAwaiter().GetResult();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLinuxAudioCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAudioCaptureProvider, LinuxAudioCaptureProvider>();
        return services;
    }
}
