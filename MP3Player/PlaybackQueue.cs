using System;
using System.Collections.Generic;

namespace MP3Player
{
    internal sealed class PlaybackQueue
    {
        private readonly HashSet<string> registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> pending = new List<string>();
        private readonly List<string> history = new List<string>();
        private readonly Random random = new Random();
        private int historyIndex = -1;

        public bool IsEmpty => registered.Count == 0;
        public bool Shuffle { get; private set; }
        public bool HasNext => historyIndex + 1 < history.Count || pending.Count > 0;
        public string Current => historyIndex >= 0 ? history[historyIndex] : null;

        public void Add(IEnumerable<string> paths)
        {
            foreach (string path in Mp3Paths.Expand(paths))
            {
                if (registered.Add(path))
                    pending.Add(path);
            }
        }

        public void ToggleMode()
        {
            Shuffle = !Shuffle;
            // A mode change makes the next selection use the remaining unplayed tracks.
            int next = historyIndex + 1;
            history.RemoveRange(next, history.Count - next);
        }

        public string TakeNext()
        {
            if (historyIndex + 1 < history.Count)
                return history[++historyIndex];

            if (pending.Count == 0)
                return null;

            int index = Shuffle ? random.Next(pending.Count) : 0;
            string path = pending[index];
            pending.RemoveAt(index);
            history.Add(path);
            historyIndex++;
            return path;
        }

        public string TakePrevious()
        {
            if (historyIndex > 0)
                historyIndex--;
            return Current;
        }

        public void DiscardCurrent()
        {
            history.RemoveAt(historyIndex);
            historyIndex--;
        }
    }
}
