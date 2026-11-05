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
        private readonly PlaybackQueue queue = new PlaybackQueue();
        private readonly Label fileName = new Label
        {
            AutoEllipsis = true,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter
        };
        private readonly Button previous = CreateButton("前の曲");
        private readonly Button pause = CreateButton("一時停止");
        private readonly Button play = CreateButton("再生");
        private readonly Button next = CreateButton("次の曲");
        private readonly Button mode = CreateButton("順番");
        private AudioFileReader reader;
        private WasapiOut output;

        public MainForm()
        {
            Text = "MP3 Player";
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(470, 120);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(12),
                ColumnCount = 5,
                RowCount = 2
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42));
            layout.Controls.Add(fileName, 0, 0);
            layout.SetColumnSpan(fileName, 5);
            var buttons = new[] { previous, pause, play, next, mode };
            for (int column = 0; column < buttons.Length; column++)
            {
                layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
                layout.Controls.Add(buttons[column], column, 1);
            }
            Controls.Add(layout);

            previous.Click += (sender, e) => PlayPrevious();
            pause.Click += (sender, e) => ChangePlayback(WaveState.Playing, device => device.Pause());
            play.Click += (sender, e) =>
            {
                if (output == null)
                    Advance();
                else
                    ChangePlayback(WaveState.Paused, device => device.Play());
            };
            next.Click += (sender, e) => Advance();
            mode.Click += (sender, e) =>
            {
                queue.ToggleMode();
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
            if (reader != null && reader.CurrentTime <= TimeSpan.FromSeconds(3))
                path = queue.TakePrevious();

            if (!TryPlay(path))
            {
                queue.DiscardCurrent();
                Advance();
            }
            else
                UpdateControls();
        }

        private void Advance()
        {
            CloseTrack();
            string path;
            while ((path = queue.TakeNext()) != null)
            {
                if (TryPlay(path))
                    break;

                queue.DiscardCurrent();
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

        private void ChangePlayback(WaveState from, Action<WasapiOut> change)
        {
            if (output?.PlaybackState != from)
                return;

            try
            {
                change(output);
                UpdateControls();
            }
            catch (Exception ex) when (IsPlaybackError(ex))
            {
                SkipFailedTrack(ex);
            }
        }

        private void PlaybackStopped(object sender, StoppedEventArgs e)
        {
            // Stop/dispose can leave a queued callback from the previous output device.
            if (!ReferenceEquals(sender, output))
                return;

            if (e.Exception != null)
                SkipFailedTrack(e.Exception);
            else
                Advance();
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
            previous.Enabled = queue.Current != null;
            pause.Enabled = state == WaveState.Playing;
            play.Enabled = state == WaveState.Paused || (output == null && queue.HasNext);
            next.Enabled = output != null || queue.HasNext;
            mode.Text = queue.Shuffle ? "シャッフル" : "順番";
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

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                CloseTrack();
            base.Dispose(disposing);
        }

        private static bool IsPlaybackError(Exception ex)
        {
            return ex is IOException || ex is UnauthorizedAccessException || ex is SecurityException
                || ex is ArgumentException || ex is NotSupportedException || ex is InvalidOperationException
                || ex is InvalidDataException || ex is COMException;
        }
    }
}
