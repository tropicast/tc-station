using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;

namespace Tropicast.Station.Encoding;

public sealed record EncoderOptions
{
    public EncoderOptions(AudioFormat? format = null, int bitrateKbps = 128)
    {
        Format = format ?? AudioFormat.EncoderDefault;
        if (Format.Encoding != PcmEncoding.Float32 || Format.Channels > 2
            || Format.SampleRate is not (44100 or 48000))
        {
            throw new ArgumentException("Encoder PCM must be float32 mono/stereo at 44.1 or 48 kHz.", nameof(format));
        }
        if (bitrateKbps is < 32 or > 320)
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        }
        BitrateKbps = bitrateKbps;
    }
    public AudioFormat Format { get; }
    public int BitrateKbps { get; }
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
