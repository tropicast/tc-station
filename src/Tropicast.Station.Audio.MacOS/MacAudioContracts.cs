namespace Tropicast.Station.Audio.MacOS;

internal interface IMacAudioBackend
{
    IReadOnlyList<AudioDevice> GetDevices();
    IMacAudioSource Open(AudioDevice device);
}

internal interface IMacAudioSource : IDisposable
{
    AudioFormat Format { get; }
    event EventHandler<MacPcmEventArgs>? DataAvailable;
    event EventHandler<MacFailureEventArgs>? Failed;
    void Start();
}

internal sealed class MacPcmEventArgs(byte[] data) : EventArgs
{
    internal byte[] Data { get; } = data;
}

internal sealed class MacFailureEventArgs(IOException error) : EventArgs
{
    internal IOException Error { get; } = error;
}

internal static class MacAudioErrors
{
    internal static IOException Describe(int code) => new(code switch
    {
        1 => "Microphone access denied. Enable Tropicast Station in System Settings → Privacy & Security → Microphone, then restart the app.",
        2 => "System audio requires macOS 13 or later. Use a virtual input such as BlackHole on unsupported systems.",
        3 => "System audio access failed. Enable Tropicast Station in System Settings → Privacy & Security → Screen & System Audio Recording (Screen Recording on macOS 13), then restart the app.",
        4 => "The selected macOS audio source disconnected or changed format. Refresh devices and select it again.",
        5 => "macOS returned an unsupported or malformed audio format.",
        6 => "Run the Tropicast Station .app bundle to request microphone/system audio permissions. Its Info.plist must contain the privacy usage descriptions.",
        7 => "System audio capture needs an available display. Use a virtual input such as BlackHole for headless audio routing.",
        _ => "macOS audio capture failed. Check the audio device, permissions and audio service.",
    });
}
