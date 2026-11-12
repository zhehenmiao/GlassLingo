using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace GlassLingo.Native
{
    // Smooth bursty cumulative responses over at most 80 ms, never an unbounded typewriter queue.
    // Timer runs only while there is received text waiting to be painted.
    internal sealed class StreamTextMotion(Action<string, bool> paint, int catchUpMs = 80) : IDisposable
    {
        private readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(16) };
        private string _target = "", _visible = "";
        private int[] _ends = [];
        private long _deadline;
        private bool _attached, _final;
        private TaskCompletionSource? _completion;
        internal string Visible => _visible;
        internal bool Running => _timer.IsEnabled;
        internal void Push(string text, bool final = false)
        {
            _final = final;
            if (!_attached) { _attached = true; _timer.Tick += (_, _) => Tick(); }
            if (text == _target && (_timer.IsEnabled || text == _visible))
            { if (text == _visible) { paint(text, final); _completion?.TrySetResult(); } return; }
            bool append = text.StartsWith(_visible, StringComparison.Ordinal);
            _target = text; _ends = StringInfo.ParseCombiningCharacters(text);
            _deadline = Environment.TickCount64 + Math.Clamp(catchUpMs, 16, 120);
            if (!append) _visible = "";
            if (!SystemParameters.ClientAreaAnimation || text.Length == 0)
            { Flush(); return; }
            if (_visible.Length == 0)
            {
                int initial = Math.Min(2, _ends.Length);
                _visible = text[..(initial < _ends.Length ? _ends[initial] : text.Length)];
                paint(_visible, _visible == _target && _final);
            }
            if (_visible == _target) { _completion?.TrySetResult(); return; }
            _timer.Start();
        }
        internal Task FinishAsync(string text)
        {
            _completion?.TrySetResult();
            _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var done = _completion.Task; Push(text, true); return done;
        }
        private void Tick()
        {
            int index = Array.BinarySearch(_ends, _visible.Length);
            if (index < 0) index = ~index;
            int remaining = _ends.Length - index;
            long time = _deadline - Environment.TickCount64;
            int step = time <= 16 ? remaining : Math.Max(1, (int)Math.Ceiling(remaining * 16d / time));
            int next = Math.Min(_ends.Length, index + step);
            _visible = _target[..(next < _ends.Length ? _ends[next] : _target.Length)];
            bool caught = _visible == _target;
            paint(_visible, caught && _final);
            if (caught) { _timer.Stop(); _completion?.TrySetResult(); }
        }
        private void Flush()
        { _timer.Stop(); _visible = _target; paint(_visible, _final); _completion?.TrySetResult(); }
        internal void Reset()
        { _timer.Stop(); _target = _visible = ""; _ends = []; _completion?.TrySetResult(); _completion = null; }
        public void Dispose() => Reset();
    }
}
