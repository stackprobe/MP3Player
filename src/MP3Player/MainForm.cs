using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows.Forms;
using NAudio.Wave;
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
        private WasapiOut output;

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

                // WASAPI uses shared mode and posts completion to the captured WinForms context.
                output = new WasapiOut();
                output.Init(reader);
                output.PlaybackStopped += PlaybackStopped;
                output.Play();
                return true;
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                Trace.WriteLine(ex);
                CloseTrack();
                return false;
            }
        }

        private void TogglePlayback()
        {
            if (output == null)
            {
                Advance();
                return;
            }

            try
            {
                switch (output.PlaybackState)
                {
                    case WaveState.Playing:
                        output.Pause();
                        break;
                    case WaveState.Paused:
                        output.Play();
                        break;
                }
                UpdateControls();
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                SkipFailedTrack(ex);
            }
        }

        private void PlaybackStopped(object sender, StoppedEventArgs e)
        {
            if (!IsHandleCreated || Disposing)
                return;

            if (InvokeRequired)
            {
                try
                {
                    BeginInvoke(new Action(() => PlaybackStopped(sender, e)));
                }
                catch (InvalidOperationException) when (!IsHandleCreated || Disposing)
                {
                    // The form closed before the notification could be queued.
                }
                return;
            }

            // Stop/dispose can leave a queued callback from the previous output device.
            if (!ReferenceEquals(sender, output))
                return;

            if (e.Exception != null)
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
            WaveState state = output?.PlaybackState ?? WaveState.Stopped;
            fileName.Text = output == null ? "MP3 ファイルまたはフォルダーをドロップ" : Path.GetFileName(queue.Current);
            trackNumber.Text = $"{(output == null ? 0 : queue.CurrentNumber)} / {queue.Count}";
            previous.Enabled = queue.Current != null;
            restart.Enabled = queue.Current != null;
            playbackToggle.Text = state == WaveState.Playing ? "一時停止" : "再生";
            playbackToggle.Enabled = state == WaveState.Playing || state == WaveState.Paused
                || (output == null && queue.HasNext);
            next.Enabled = output != null || queue.HasNext;
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
        }

        private void CloseTrack()
        {
            WasapiOut device = output;
            output = null;
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
            finally
            {
                reader?.Dispose();
                reader = null;
            }
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
    }
}
