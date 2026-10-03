namespace Tropicast.Station.Audio.Windows;

/// <summary>Small native boundary for deterministic lifecycle tests without Windows hardware.</summary>
internal interface IWasapiBackend : IDisposable
{
    event EventHandler? DevicesChanged;
    IReadOnlyList<AudioDevice> GetDevices();
    bool IsMicrophoneDenied();
    IWasapiSource Open(string deviceId);
}

internal interface IWasapiSource : IDisposable
{
    AudioFormat Format { get; }
    event EventHandler<WasapiDataEventArgs>? DataAvailable;
    event EventHandler<WasapiStoppedEventArgs>? Stopped;
    void Start();
    void Stop();
}

internal sealed class WasapiDataEventArgs(byte[] buffer, int count) : EventArgs
{
    public byte[] Buffer { get; } = buffer;
    public int Count { get; } = count;
}

internal sealed class WasapiStoppedEventArgs(Exception? exception) : EventArgs
{
    public Exception? Exception { get; } = exception;
}

internal static class WindowsAudioErrors
{
    internal const string PrivacyGuidance = "Windows denied microphone access. Open Settings > Privacy & security > Microphone "
        + "(Windows 10: Settings > Privacy > Microphone) and enable Microphone access and Let desktop apps access your microphone.";

    internal static IOException Describe(Exception exception)
        => new(exception.HResult switch
        {
            unchecked((int)0x80070005) => PrivacyGuidance,
            unchecked((int)0x88890004) or unchecked((int)0x88890026) => "The selected Windows audio device was disconnected or invalidated. Select an available source.",
            unchecked((int)0x8889000A) => "The Windows audio service is not running. Start Windows Audio and retry.",
            unchecked((int)0x88890008) => "The device rejected the shared-mode PCM format. Check its sample rate and channel settings.",
            unchecked((int)0x8889000E) => "The audio device is in exclusive use. Close the application holding it and retry.",
            _ => "Windows audio capture failed. Check the device, Windows Audio service, permissions and shared-mode format.",
        });
}
