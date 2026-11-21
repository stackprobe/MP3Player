using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using Windows.Media;
using WaveState = NAudio.Wave.PlaybackState;

namespace MP3Player
{
    internal sealed class MainForm : Form
    {
        private readonly Font uiFont = new Font("Meiryo", 10f, FontStyle.Regular, GraphicsUnit.Point);
        private readonly Icon windowIcon = new Icon(typeof(MainForm), "MP3Player.ico");
        private PlaybackQueue queue = new PlaybackQueue();
        private readonly Label fileName = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        private readonly Label trackNumber = new Label
        {
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        private readonly Button previous = CreateButton("前の曲");
        private readonly Button restart = CreateButton("曲の先頭");
        private readonly Button playbackToggle = CreateButton("再生");
        private readonly Button next = CreateButton("次の曲");
        private readonly Button mode = CreateButton("順番ループ");
        private readonly Button reset = CreateButton("リセット");
        private AudioFileReader reader;
        private volatile WasapiOut output;
        private string outputDeviceId;
        private long outputStartPosition;
        private MediaControls mediaControls;
        private MMDeviceEnumerator deviceEnumerator;
        private OutputNotifications outputNotifications;

        private bool IsPlaying => output?.PlaybackState == WaveState.Playing;

        public MainForm()
        {
            Text = "MP3 Player";
            Font = uiFont;
            Icon = windowIcon;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(650, 140);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var buttons = new[] { previous, restart, playbackToggle, next, mode, reset };
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = buttons.Length,
                RowCount = 3
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 23));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 35));
            layout.Controls.Add(fileName, 0, 0);
            layout.SetColumnSpan(fileName, layout.ColumnCount);
            layout.Controls.Add(trackNumber, 0, 1);
            layout.SetColumnSpan(trackNumber, layout.ColumnCount);
            for (int column = 0; column < buttons.Length; column++)
            {
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, buttons[column] == mode ? 20 : 16));
                layout.Controls.Add(buttons[column], column, 2);
            }
            Controls.Add(layout);

            previous.Click += (sender, e) => PlayPrevious();
            restart.Click += (sender, e) => Advance(repeatCurrent: true);
            playbackToggle.Click += (sender, e) => TogglePlayback();
            next.Click += (sender, e) => Advance();
            mode.Click += (sender, e) =>
            {
                queue.CycleMode();
                UpdateControls();
            };
            reset.Click += (sender, e) =>
            {
                CloseTrack();
                queue = new PlaybackQueue();
                UpdateControls();
            };

            EnableFileDrop(this);
            UpdateControls();
        }

        public void AddPaths(IEnumerable<string> paths)
        {
            bool wasEmpty = queue.IsEmpty;
            queue.Add(paths);
            if (wasEmpty && !queue.IsEmpty)
                Advance();
            else
                UpdateControls();
        }

        private static Button CreateButton(string text)
        {
            return new Button { Text = text, Dock = DockStyle.Fill, UseVisualStyleBackColor = true };
        }

        private void EnableFileDrop(Control control)
        {
            control.AllowDrop = true;
            control.DragEnter += (sender, e) =>
            {
                e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) && (e.AllowedEffect & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.None;
            };
            control.DragDrop += (sender, e) =>
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
                    AddPaths(paths);
            };
            foreach (Control child in control.Controls)
                EnableFileDrop(child);
        }

        private void PlayPrevious()
        {
            string path = queue.Current;
            if (path == null)
                return;

            // The WASAPI device clock can keep advancing while the source is paused.
            if (queue.Mode != PlaybackMode.Shuffle ||
                (reader != null && reader.CurrentTime <= TimeSpan.FromSeconds(3)))
                path = queue.TakePrevious();

            if (!TryPlay(path))
            {
                queue.DiscardCurrent();
                Advance();
            }
            else
                UpdateControls();
        }

        private void Advance(bool repeatCurrent = false)
        {
            CloseTrack();
            string path = repeatCurrent ? queue.Current : queue.TakeNext();
            while (path != null && !TryPlay(path))
            {
                queue.DiscardCurrent();
                path = queue.TakeNext();
            }
            UpdateControls();
        }

        private bool TryPlay(string path)
        {
            CloseTrack();
            try
            {
                reader = new AudioFileReader(path);
                if (reader.Length == 0)
                    throw new InvalidDataException("The MP3 contains no audio.");

                StartOutput();
                return true;
            }
            catch (COMException ex) when (reader != null && IsOutputUnavailable(ex))
            {
                Trace.WriteLine(ex);
                CloseOutput();
                return true;
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                Trace.WriteLine(ex);
                CloseTrack();
                return false;
            }
        }

        private void StartOutput()
        {
            using (var enumerator = new MMDeviceEnumerator())
            using (var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console))
            {
                outputDeviceId = endpoint.ID;
                outputStartPosition = reader.Position;
                // Keep the same shared mode, event synchronization and latency as WasapiOut's default.
                var device = new WasapiOut(endpoint, AudioClientShareMode.Shared, true, 200);
                output = device;
                device.Init(reader);
                device.PlaybackStopped += PlaybackStopped;
                device.Play();
            }
        }

        private void TogglePlayback()
        {
            if (IsPlaying)
                PausePlayback();
            else
                Play();
        }

        private void Play()
        {
            if (IsPlaying)
                return;

            if (reader == null)
            {
                Advance();
                return;
            }

            bool needsOutput = output == null;
            try
            {
                if (needsOutput)
                    StartOutput();
                else if (output.PlaybackState == WaveState.Paused)
                    output.Play();
            }
            catch (Exception ex) when (IsPlaybackError(ex) && needsOutput)
            {
                // Leave the current track paused so Play can retry when an output is available.
                Trace.WriteLine(ex);
                CloseOutput();
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                SkipFailedTrack(ex);
            }
            UpdateControls();
        }

        private void PausePlayback()
        {
            if (!IsPlaying)
                return;

            output.Pause();
            UpdateControls();
        }

        private void SuspendOutput()
        {
            long? renderedPosition = null;
            try
            {
                if (output.PlaybackState != WaveState.Stopped)
                    renderedPosition = outputStartPosition + output.GetPosition();
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                // A disconnected endpoint may have already lost its clock.
                Trace.WriteLine(ex);
            }

            CloseOutput();
            if (renderedPosition.HasValue)
            {
                long position = Math.Max(outputStartPosition, Math.Min(renderedPosition.Value, reader.Position));
                reader.Position = position - position % reader.WaveFormat.BlockAlign;
            }
            UpdateControls();
        }

        private void PlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (!IsHandleCreated || Disposing)
                return;

            if (InvokeRequired)
            {
                PostToForm(() => PlaybackStopped(sender, e));
                return;
            }

            // Stop/dispose can leave a queued callback from the previous output device.
            if (!ReferenceEquals(sender, output))
                return;

            if (e.Exception is COMException comError && IsOutputUnavailable(comError))
                SuspendOutput();
            else if (e.Exception != null)
                SkipFailedTrack(e.Exception);
            else if (reader.Position == 0)
                SkipFailedTrack(new InvalidDataException("The MP3 contains no playable audio."));
            else
                Advance(queue.Mode == PlaybackMode.RepeatOne);
        }

        private void SkipFailedTrack(Exception ex)
        {
            Trace.WriteLine(ex);
            queue.DiscardCurrent();
            Advance();
        }

        private void UpdateControls()
        {
            string title = reader == null ? null : Path.GetFileName(queue.Current);
            fileName.Text = title ?? "MP3 ファイルまたはフォルダーをドロップ";
            trackNumber.Text = $"{(reader == null ? 0 : queue.CurrentNumber)} / {queue.Count}";
            previous.Enabled = queue.Current != null;
            restart.Enabled = queue.Current != null;
            playbackToggle.Text = IsPlaying ? "一時停止" : "再生";
            playbackToggle.Enabled = reader != null || queue.HasNext;
            next.Enabled = reader != null || queue.HasNext;
            reset.Enabled = !queue.IsEmpty || queue.Mode != PlaybackMode.Sequence;
            switch (queue.Mode)
            {
                case PlaybackMode.Shuffle:
                    mode.Text = "シャッフル";
                    break;
                case PlaybackMode.RepeatOne:
                    mode.Text = "1曲ループ";
                    break;
                default:
                    mode.Text = "順番ループ";
                    break;
            }

            try
            {
                mediaControls?.Update(title, IsPlaying, playbackToggle.Enabled, next.Enabled, previous.Enabled);
            }
            catch (COMException ex)
            {
                Trace.WriteLine(ex);
            }
        }

        private void CloseTrack()
        {
            CloseOutput();
            reader?.Dispose();
            reader = null;
        }

        private void CloseOutput()
        {
            WasapiOut device = output;
            output = null;
            outputDeviceId = null;
            try
            {
                if (device != null)
                {
                    device.PlaybackStopped -= PlaybackStopped;
                    device.Dispose();
                }
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                Trace.WriteLine(ex);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                mediaControls = new MediaControls(Handle, button => PostToForm(() => HandleMediaButton(button)));
            }
            catch (Exception ex) when (ex is COMException || ex is TypeLoadException
                || ex is PlatformNotSupportedException || ex is InvalidCastException)
            {
                Trace.WriteLine(ex);
            }

            try
            {
                deviceEnumerator = new MMDeviceEnumerator();
                var notifications = new OutputNotifications(OutputChanged);
                Marshal.ThrowExceptionForHR(deviceEnumerator.RegisterEndpointNotificationCallback(notifications));
                outputNotifications = notifications;
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                Trace.WriteLine(ex);
            }
            UpdateControls();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            try
            {
                mediaControls?.Dispose();
            }
            catch (COMException ex)
            {
                Trace.WriteLine(ex);
            }
            mediaControls = null;

            if (outputNotifications != null)
                deviceEnumerator.UnregisterEndpointNotificationCallback(outputNotifications);
            outputNotifications = null;
            deviceEnumerator?.Dispose();
            deviceEnumerator = null;
            base.OnHandleDestroyed(e);
        }

        private void OutputChanged(string deviceId, bool defaultChanged)
        {
            WasapiOut activeOutput = output;
            if (activeOutput == null)
                return;

            PostToForm(() =>
            {
                bool affectsOutput = defaultChanged ? deviceId != outputDeviceId : deviceId == outputDeviceId;
                if (ReferenceEquals(activeOutput, output) && affectsOutput)
                    SuspendOutput();
            });
        }

        private void PostToForm(Action action)
        {
            if (!IsHandleCreated || Disposing)
                return;

            try
            {
                BeginInvoke(action);
            }
            catch (InvalidOperationException) when (!IsHandleCreated || Disposing || IsDisposed)
            {
                // The window closed before its notification could be queued.
            }
        }

        private void HandleMediaButton(SystemMediaTransportControlsButton button)
        {
            switch (button)
            {
                case SystemMediaTransportControlsButton.Play:
                    Play();
                    break;
                case SystemMediaTransportControlsButton.Pause:
                case SystemMediaTransportControlsButton.Stop:
                    PausePlayback();
                    break;
                case SystemMediaTransportControlsButton.Next:
                    if (next.Enabled)
                        Advance();
                    break;
                case SystemMediaTransportControlsButton.Previous:
                    PlayPrevious();
                    break;
            }
        }

        protected override void WndProc(ref Message message)
        {
            const int WmAppCommand = 0x0319;
            if (message.Msg == WmAppCommand && playbackToggle.Enabled && !Disposing)
            {
                const int MediaNextTrack = 11;
                const int MediaPreviousTrack = 12;
                const int MediaStop = 13;
                const int MediaPlayPause = 14;
                const int MediaPlay = 46;
                const int MediaPause = 47;

                // WM_APPCOMMAND carries the command and its source in the high word.
                int command = (int)((message.LParam.ToInt64() >> 16) & 0x0fff);
                switch (command)
                {
                    case MediaNextTrack: HandleMediaButton(SystemMediaTransportControlsButton.Next); break;
                    case MediaPreviousTrack: HandleMediaButton(SystemMediaTransportControlsButton.Previous); break;
                    case MediaStop: HandleMediaButton(SystemMediaTransportControlsButton.Stop); break;
                    case MediaPlayPause: TogglePlayback(); break;
                    case MediaPlay: HandleMediaButton(SystemMediaTransportControlsButton.Play); break;
                    case MediaPause: HandleMediaButton(SystemMediaTransportControlsButton.Pause); break;
                    default:
                        base.WndProc(ref message);
                        return;
                }
                message.Result = new IntPtr(1);
                return;
            }
            base.WndProc(ref message);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CloseTrack();
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CloseTrack();
            base.Dispose(disposing);
            if (disposing)
            {
                windowIcon.Dispose();
                uiFont.Dispose();
            }
        }

        private static bool IsPlaybackError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException
                || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException
                || ex is InvalidDataException || ex is COMException;
        }

        private static bool IsOutputUnavailable(COMException ex)
        {
            const int EndpointNotFound = unchecked((int)0x80070490);
            return ex.ErrorCode == EndpointNotFound || ex.ErrorCode == AudioClientErrorCode.DeviceInvalidated
                || ex.ErrorCode == AudioClientErrorCode.ResourcesInvalidated;
        }
    }
}
