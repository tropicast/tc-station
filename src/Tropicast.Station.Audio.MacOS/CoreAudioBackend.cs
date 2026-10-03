using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tropicast.Station.Audio.MacOS;

internal sealed class CoreAudioBackend : IMacAudioBackend
{
    public IReadOnlyList<AudioDevice> GetDevices()
    {
        if (!OperatingSystem.IsMacOS())
        {
            throw new PlatformNotSupportedException("Core Audio capture requires macOS.");
        }
        try
        {
            var pointer = NativeMethods.ListDevices(out var error);
            if (pointer == IntPtr.Zero)
            {
                throw MacAudioErrors.Describe(error);
            }
            try
            {
                return ParseDevices(Marshal.PtrToStringUTF8(pointer) ?? throw new IOException("Core Audio returned no device data."));
            }
            finally
            {
                NativeMethods.Free(pointer);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            throw new IOException("The macOS audio bridge is missing or incompatible. Build on macOS using Xcode Command Line Tools and run the .app bundle.");
        }
    }

    internal static IReadOnlyList<AudioDevice> ParseDevices(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var devices = document.RootElement.EnumerateArray().Select(d => new AudioDevice(
                d.GetProperty("id").GetString() ?? throw new IOException("Core Audio returned a missing device ID."),
                d.GetProperty("name").GetString() ?? throw new IOException("Core Audio returned a missing device name."),
                d.GetProperty("loopback").GetBoolean() ? AudioDeviceKind.Loopback : AudioDeviceKind.Input,
                d.GetProperty("default").GetBoolean(),
                new(d.GetProperty("rate").GetInt32(), d.GetProperty("channels").GetInt32()))).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            if (devices.Any(d => string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.DisplayName))
                || devices.Select(d => d.Id).Distinct(StringComparer.Ordinal).Count() != devices.Length)
            {
                throw new IOException("Core Audio returned invalid or duplicate devices.");
            }
            return devices;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException or FormatException)
        {
            throw new IOException("Cannot read Core Audio device information. Check the audio service.");
        }
    }

    public IMacAudioSource Open(AudioDevice device) => new CoreAudioSource(device);
}

internal sealed class CoreAudioSource(AudioDevice device) : IMacAudioSource
{
    private readonly object _sync = new();
    private NativeMethods.PcmCallback? _pcm;
    private NativeMethods.ErrorCallback? _error;
    private IntPtr _handle;

    public AudioFormat Format => device.NativeFormat;
    public event EventHandler<MacPcmEventArgs>? DataAvailable;
    public event EventHandler<MacFailureEventArgs>? Failed;

    public void Start()
    {
        lock (_sync)
        {
            _pcm = OnPcm;
            _error = code => Failed?.Invoke(this, new(MacAudioErrors.Describe(code)));
            var result = NativeMethods.Start(device.Id, Format.SampleRate, Format.Channels, _pcm, _error, out _handle);
            if (result != 0)
            {
                throw MacAudioErrors.Describe(result);
            }
        }
    }

    private void OnPcm(IntPtr data, int size)
    {
        if (size <= 0 || size % Format.BytesPerFrame != 0 || size > Format.SampleRate * Format.BytesPerFrame * 2)
        {
            Failed?.Invoke(this, new(new IOException("Core Audio returned an invalid PCM packet.")));
            return;
        }
        var bytes = new byte[size];
        Marshal.Copy(data, bytes, 0, size);
        DataAvailable?.Invoke(this, new(bytes));
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_handle != IntPtr.Zero)
            {
                var result = NativeMethods.Stop(_handle);
                _handle = IntPtr.Zero;
                GC.KeepAlive(_pcm);
                GC.KeepAlive(_error);
                if (result != 0)
                {
                    throw new IOException("macOS capture could not stop cleanly. Restart the app before retrying.");
                }
            }
        }
    }
}

internal static partial class NativeMethods
{
    private const string Library = "tropicast_audio";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void PcmCallback(IntPtr bytes, int count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ErrorCallback(int code);

    [LibraryImport(Library, EntryPoint = "tc_audio_list")]
    internal static partial IntPtr ListDevices(out int error);
    [LibraryImport(Library, EntryPoint = "tc_audio_free")]
    internal static partial void Free(IntPtr pointer);
    [LibraryImport(Library, EntryPoint = "tc_audio_start", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int Start(string id, int rate, int channels, PcmCallback pcm, ErrorCallback error, out IntPtr handle);
    [LibraryImport(Library, EntryPoint = "tc_audio_stop")]
    internal static partial int Stop(IntPtr handle);
}
