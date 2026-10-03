using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Encoding;

public sealed record EncoderOptions
{
    public EncoderOptions(AudioFormat? format = null, int bitrateKbps = 128)
    {
        Format = format ?? new AudioFormat(44100, 2);
        if (Format.Encoding != PcmEncoding.Float32 || Format.Channels > 2
            || Format.SampleRate is not (44100 or 48000))
        {
            throw new ArgumentException("Encoder PCM must be float32 mono/stereo at 44.1 or 48 kHz.", nameof(format));
        }
        if (bitrateKbps is not (64 or 96 or 128 or 192 or 320))
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        }
        BitrateKbps = bitrateKbps;
    }
    public AudioFormat Format { get; }
    public int BitrateKbps { get; }
    public static EncoderOptions FromProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (ProfileValidator.Validate(profile).Count > 0)
        {
            throw new ArgumentException("The saved profile has invalid stream settings.", nameof(profile));
        }
        return new(new(profile.SampleRate, profile.Channels), profile.BitrateKbps);
    }
}

public enum EncoderState { Starting, Streaming, Stopping, Stopped, Failed }
public sealed record EncoderSnapshot(EncoderState State, string Message, long EncodedBytes);

public interface IEncoderSession : IAsyncDisposable
{
    EncoderSnapshot Snapshot { get; }
    Task Completion { get; }
    /// <summary>Non-blocking callback handoff. Owns a copy; queue overload is an explicit failure.</summary>
    void Submit(PcmFrame frame);
    Task StopAsync(CancellationToken cancellationToken = default);
}

public interface IBroadcastEncoder
{
    Task<IEncoderSession> StartAsync(BroadcastTarget target, EncoderOptions? options = null, CancellationToken cancellationToken = default);
}

internal sealed class EncoderException(string message, bool isTransient = false) : IOException(message)
{
    internal bool IsTransient { get; } = isTransient;
}
