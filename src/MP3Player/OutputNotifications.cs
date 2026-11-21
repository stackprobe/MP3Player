using System;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace MP3Player
{
    internal sealed class OutputNotifications : IMMNotificationClient
    {
        private readonly Action<string, bool> onChanged;

        public OutputNotifications(Action<string, bool> onChanged)
        {
            this.onChanged = onChanged;
        }

        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string deviceId)
        {
            if (flow == DataFlow.Render && role == Role.Console)
                onChanged(deviceId, true);
        }

        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            if (newState != DeviceState.Active)
                OnDeviceRemoved(deviceId);
        }

        public void OnDeviceRemoved(string deviceId) => onChanged(deviceId, false);

        public void OnDeviceAdded(string deviceId) { }

        public void OnPropertyValueChanged(string deviceId, PropertyKey key) { }
    }
}
