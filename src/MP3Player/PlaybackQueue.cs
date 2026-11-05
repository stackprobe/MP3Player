using System;
using System.Collections.Generic;

namespace MP3Player
{
    internal enum PlaybackMode
    {
        Sequence,
        Shuffle,
        RepeatOne
    }

    internal sealed class PlaybackQueue
    {
        private readonly HashSet<string> registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> available = new List<string>();
        private readonly List<string> pending = new List<string>();
        private readonly List<string> history = new List<string>();
        private readonly Random random = new Random();
        private int historyIndex = -1;

        public bool IsEmpty => registered.Count == 0;
        public PlaybackMode Mode { get; private set; }
        public bool HasNext => available.Count > 0;
        public string Current => historyIndex >= 0 ? history[historyIndex] : null;
        public int Count => available.Count;
        public int CurrentNumber => available.IndexOf(Current) + 1;

        public void Add(IEnumerable<string> paths)
        {
            foreach (string path in Mp3Paths.Expand(paths))
            {
                if (registered.Add(path))
                {
                    available.Add(path);
                    pending.Add(path);
                }
            }
        }

        public void CycleMode()
        {
            switch (Mode)
            {
                case PlaybackMode.Sequence:
                    Mode = PlaybackMode.Shuffle;
                    break;
                case PlaybackMode.Shuffle:
                    Mode = PlaybackMode.RepeatOne;
                    break;
                default:
                    Mode = PlaybackMode.Sequence;
                    break;
            }
            // A mode change makes the next selection use the remaining unplayed tracks.
            int next = historyIndex + 1;
            history.RemoveRange(next, history.Count - next);
        }

        public string TakeNext()
        {
            if (historyIndex + 1 < history.Count)
                return history[++historyIndex];

            if (!HasNext)
                return null;

            bool newPass = pending.Count == 0;
            if (newPass)
                pending.AddRange(available);

            int index = Mode == PlaybackMode.Shuffle ? PickShuffledIndex(newPass) : 0;
            string path = pending[index];
            pending.RemoveAt(index);
            history.Add(path);
            historyIndex++;
            // Keep enough history to go back one full pass without growing indefinitely.
            if (history.Count - 1 > available.Count)
            {
                history.RemoveAt(0);
                historyIndex--;
            }
            return path;
        }

        private int PickShuffledIndex(bool newPass)
        {
            int previous = newPass && pending.Count > 1 ? pending.IndexOf(Current) : -1;
            if (previous < 0)
                return random.Next(pending.Count);

            int index = random.Next(pending.Count - 1);
            return index >= previous ? index + 1 : index;
        }

        public string TakePrevious()
        {
            if (historyIndex > 0)
                historyIndex--;
            return Current;
        }

        public void DiscardCurrent()
        {
            string path = Current;
            available.Remove(path);
            pending.Remove(path);
            for (int index = history.Count - 1; index >= 0; index--)
            {
                if (history[index] != path)
                    continue;

                history.RemoveAt(index);
                if (index <= historyIndex)
                    historyIndex--;
            }
        }
    }
}
