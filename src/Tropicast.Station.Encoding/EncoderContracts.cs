using Tropicast.Station.Audio;
using Tropicast.Station.Core.Broadcasting;
using Tropicast.Station.Core.Profiles;

namespace Tropicast.Station.Encoding;

public enum EncoderCodec { Mp3, Opus }

public sealed record EncoderOptions
{
    public EncoderOptions(AudioFormat? format = null, int bitrateKbps = 128, EncoderCodec codec = EncoderCodec.Mp3)
    {
        Format = format ?? new AudioFormat(44100, 2);
        if (Format.Encoding != PcmEncoding.Float32 || Format.Channels > 2
            || Format.SampleRate is not (44100 or 48000))
        {
            throw new ArgumentException("Encoder PCM must be float32 mono/stereo at 44.1 or 48 kHz.", nameof(format));
        }
        if (codec == EncoderCodec.Opus ? bitrateKbps is not (48 or 64 or 96)
            : bitrateKbps is not (64 or 96 or 128 or 192 or 320))
        {
            throw new ArgumentOutOfRangeException(nameof(bitrateKbps));
        }
        BitrateKbps = bitrateKbps;
        Codec = codec;
    }
    public AudioFormat Format { get; }
    public int BitrateKbps { get; }
    public EncoderCodec Codec { get; }
    /// <summary>Content type and display name of the encoded stream.</summary>
    public string ContentType => Codec == EncoderCodec.Opus ? "audio/ogg" : "audio/mpeg";
    public string CodecName => Codec == EncoderCodec.Opus ? "Opus" : "MP3";
    public static EncoderCodec CodecFor(string contentType) => contentType switch
    {
        "audio/mpeg" => EncoderCodec.Mp3,
        "audio/ogg" => EncoderCodec.Opus,
        _ => throw new ArgumentException("Select an audio/mpeg (MP3) or audio/ogg (Opus) profile.", nameof(contentType)),
    };
    public static EncoderOptions FromProfile(ConnectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (ProfileValidator.Validate(profile).Count > 0)
        {
            throw new ArgumentException("The saved profile has invalid stream settings.", nameof(profile));
        }
        return new(new(profile.SampleRate, profile.Channels), profile.BitrateKbps, CodecFor(profile.ContentType));
    }

    /// <summary>Options for another output of the same capture: same PCM format, that output's codec and bitrate.</summary>
    public EncoderOptions ForOutput(ConnectionProfile output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new(Format, output.BitrateKbps, CodecFor(output.ContentType));
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
