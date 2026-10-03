using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Tropicast.Station.Audio.Windows;

// Public and explicitly COM-visible so Windows can query the IMMNotificationClient CCW.
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
public sealed class WindowsEndpointNotifications : IMMNotificationClient
{
    private readonly Action _notify;

    internal WindowsEndpointNotifications(Action notify) => _notify = notify;

    public void OnDeviceStateChanged(string deviceId, DeviceState newState) => _notify();
    public void OnDeviceAdded(string pwstrDeviceId) => _notify();
    public void OnDeviceRemoved(string deviceId) => _notify();
    public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId) => _notify();
    public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) => _notify();
}
