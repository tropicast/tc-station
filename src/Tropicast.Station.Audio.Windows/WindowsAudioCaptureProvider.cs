using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Tropicast.Station.Audio.Windows;

/// <summary>WASAPI shared-mode inputs and whole-render-endpoint loopback (not per-process capture).</summary>
public sealed class WindowsAudioCaptureProvider : IAudioCaptureProvider, IDisposable
{
    private readonly object _sync = new();
    private readonly Func<IWasapiBackend> _factory;
    private IWasapiBackend? _backend;
    private bool _disposed;

    public WindowsAudioCaptureProvider()
        : this(CreateBackend)
    {
    }

    internal WindowsAudioCaptureProvider(Func<IWasapiBackend> factory) => _factory = factory;

    public string Description => "Windows WASAPI: microphones/mixer inputs and system audio from each playback device.";
    public event EventHandler? DevicesChanged;

    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                try
                {
                    return Backend.GetDevices();
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    throw WindowsAudioErrors.Describe(ex);
                }
            }
        }, cancellationToken);

    public Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
        => Task.Run<IAudioCaptureSession>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                IWasapiSource? source = null;
                try
                {
                    var device = Backend.GetDevices().FirstOrDefault(d => d.Id == deviceId)
                        ?? throw new IOException("The selected Windows audio device is no longer available.");
                    if (device.Kind == AudioDeviceKind.Input && Backend.IsMicrophoneDenied())
                    {
                        throw new IOException(WindowsAudioErrors.PrivacyGuidance);
                    }

                    // Construct/start on an MTA worker: NAudio must not capture the UI SynchronizationContext.
                    source = Backend.Open(deviceId);
                    if (source.Format != device.NativeFormat)
                    {
                        throw new IOException("The device format changed while starting. Refresh devices and try again.");
                    }

                    var session = new WasapiCaptureSession(source);
                    source = null; // The session now owns cleanup, including start failures.
                    session.Start();
                    return session;
                }
                catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
                {
                    throw WindowsAudioErrors.Describe(ex);
                }
                finally
                {
                    source?.Dispose();
                }
            }
        }, cancellationToken);

    private IWasapiBackend Backend
    {
        get
        {
            if (_backend is null)
            {
                _backend = _factory();
                _backend.DevicesChanged += OnDevicesChanged;
            }

            return _backend;
        }
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);

    private static IWasapiBackend CreateBackend()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WASAPI capture requires Windows.");
        }

        return new NAudioWasapiBackend();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (!_disposed)
            {
                if (_backend is not null)
                {
                    _backend.DevicesChanged -= OnDevicesChanged;
                    _backend.Dispose();
                }

                _disposed = true;
            }
        }

        GC.SuppressFinalize(this);
    }
}

public static class ServiceCollectionExtensions
{
    /// <summary>Register before AddStationAudio; demo selection remains explicit in the app.</summary>
    public static IServiceCollection AddWindowsAudioCapture(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton<IAudioCaptureProvider, WindowsAudioCaptureProvider>();
        return services;
    }
}
