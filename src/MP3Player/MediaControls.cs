using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media;

namespace MP3Player
{
    internal sealed class MediaControls : IDisposable
    {
        private readonly SystemMediaTransportControls controls;
        private readonly Action<SystemMediaTransportControlsButton> onCommand;
        private string currentTitle;

        public MediaControls(IntPtr window, Action<SystemMediaTransportControlsButton> onCommand)
        {
            this.onCommand = onCommand;
            var factory = (ISystemMediaTransportControlsInterop)WindowsRuntimeMarshal.GetActivationFactory(
                typeof(SystemMediaTransportControls));
            var iid = new Guid("99FA3FF4-1742-42A6-902E-087D41F965EC");
            factory.GetForWindow(window, ref iid, out IntPtr instance);
            try
            {
                controls = (SystemMediaTransportControls)Marshal.GetObjectForIUnknown(instance);
            }
            finally
            {
                Marshal.Release(instance);
            }

            controls.ButtonPressed += ButtonPressed;
        }

        public void Update(string title, bool playing, bool canPlay, bool canNext, bool canPrevious)
        {
            controls.IsEnabled = canPlay;
            controls.IsPlayEnabled = canPlay;
            controls.IsPauseEnabled = title != null;
            controls.IsStopEnabled = title != null;
            controls.IsNextEnabled = canNext;
            controls.IsPreviousEnabled = canPrevious;
            controls.PlaybackStatus = playing ? MediaPlaybackStatus.Playing
                : title != null ? MediaPlaybackStatus.Paused : MediaPlaybackStatus.Stopped;

            if (title == currentTitle)
                return;

            var display = controls.DisplayUpdater;
            display.ClearAll();
            if (title != null)
            {
                display.Type = MediaPlaybackType.Music;
                display.MusicProperties.Title = title;
            }
            display.Update();
            currentTitle = title;
        }

        private void ButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs e)
        {
            onCommand(e.Button);
        }

        public void Dispose()
        {
            controls.ButtonPressed -= ButtonPressed;
            controls.IsEnabled = false;
        }

        [ComImport]
        [Guid("DDB0472D-C911-4A1F-86D9-DC3D71A95F5A")]
        [InterfaceType(ComInterfaceType.InterfaceIsIInspectable)]
        private interface ISystemMediaTransportControlsInterop
        {
            void GetForWindow(IntPtr window, ref Guid iid, out IntPtr controls);
        }
    }
}
