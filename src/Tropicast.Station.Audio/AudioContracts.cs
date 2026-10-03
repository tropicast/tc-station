namespace Tropicast.Station.Audio;

public enum AudioDeviceKind { Input, Loopback }
#pragma warning disable CA1720 // PCM encoding names intentionally identify their numeric representation.
public enum PcmEncoding { Float32, Signed16 }
#pragma warning restore CA1720

/// <summary>Interleaved, little-endian PCM. Channel order follows the capture endpoint.</summary>
public sealed record AudioFormat
{
    public AudioFormat(int sampleRate, int channels, PcmEncoding encoding = PcmEncoding.Float32)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(sampleRate, 192000);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(channels, 8);
        if (!Enum.IsDefined(encoding))
        {
            throw new ArgumentOutOfRangeException(nameof(encoding));
        }

        SampleRate = sampleRate;
        Channels = channels;
        Encoding = encoding;
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public PcmEncoding Encoding { get; }
    public int BytesPerFrame => Channels * (Encoding == PcmEncoding.Float32 ? 4 : 2);
    public static AudioFormat EncoderDefault { get; } = new(48000, 2);
}

public sealed record AudioDevice(string Id, string DisplayName, AudioDeviceKind Kind, bool IsDefault, AudioFormat NativeFormat)
{
    public string Label => $"{DisplayName}{(IsDefault ? " (default)" : "")} — {NativeFormat.SampleRate} Hz, {NativeFormat.Channels} ch";
}

/// <summary>Owned buffer, valid beyond the next read. Adapters must not reuse its underlying memory.</summary>
public sealed class PcmFrame
{
    public PcmFrame(AudioFormat format, ReadOnlyMemory<byte> data)
    {
        ArgumentNullException.ThrowIfNull(format);
        if (data.Length == 0 || data.Length % format.BytesPerFrame != 0)
        {
            throw new ArgumentException("PCM data must contain complete interleaved frames.", nameof(data));
        }

        Format = format;
        Data = data;
    }

    public AudioFormat Format { get; }
    public ReadOnlyMemory<byte> Data { get; }
    public int FrameCount => Data.Length / Format.BytesPerFrame;
}

/// <summary>
/// Platform adapter. IDs remain stable across enumeration. Raise DevicesChanged for additions,
/// removals, format/default changes. Do not silently switch endpoints on removal.
/// </summary>
public interface IAudioCaptureProvider
{
    string Description { get; }
    event EventHandler? DevicesChanged;
    Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One consumer per session. Cancellation/StopAsync must unblock ReadFramesAsync and release native
/// resources. Device loss and capture errors throw IOException; unexpected EOF is not success.
/// Bound any adapter queue; report overruns rather than silently dropping audio.
/// StopAsync and DisposeAsync must be thread-safe and idempotent, including after the reader ends.
/// </summary>
public interface IAudioCaptureSession : IAsyncDisposable
{
    IAsyncEnumerable<PcmFrame> ReadFramesAsync(CancellationToken cancellationToken = default);
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Explicitly unavailable until the platform-specific adapters in #5–#7 are installed.</summary>
public sealed class UnavailableAudioCaptureProvider : IAudioCaptureProvider
{
    public string Description => "Native audio capture is not implemented yet. Use --demo-audio for synthetic sources.";
    public event EventHandler? DevicesChanged { add { } remove { } }
    public Task<IReadOnlyList<AudioDevice>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<AudioDevice>>([]);
    }

    public Task<IAudioCaptureSession> StartAsync(string deviceId, CancellationToken cancellationToken = default)
        => throw new NotSupportedException(Description);
}
