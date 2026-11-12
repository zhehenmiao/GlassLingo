using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;

namespace GlassLingo.Ocr
{
    // One engine/worker across regions. Cancelling a queued region does not tear down
    // the worker being used by another region; an active call drains before disposal.
    internal sealed class SharedOcrPool : IDisposable
    {
        private readonly IOcrEngine _engine;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly object _lifetime = new();
        private readonly CancellationTokenSource _shutdown = new();
        private int _references = 1;
        private bool _ownerClosed, _disposed, _engineClosed;
        private long _lastStart;
        private readonly int _minimumGap;
        internal SharedOcrPool(IOcrEngine engine, bool highPerformance = false) { _engine = engine; _minimumGap = highPerformance ? 0 : 120; }
        internal IOcrEngine Rent()
        {
            lock (_lifetime)
            {
                ObjectDisposedException.ThrowIf(_ownerClosed || _disposed, this);
                _references++; return new Lease(this);
            }
        }
        private void Release()
        {
            bool dispose;
            lock (_lifetime) { dispose = --_references == 0; if (dispose) _disposed = true; }
            if (dispose) CloseEngine();
        }
        private void CloseEngine()
        {
            lock (_lifetime) { if (_engineClosed) return; _engineClosed = true; }
            _shutdown.Cancel(); (_engine as IDisposable)?.Dispose(); _shutdown.Dispose();
        }
        internal void Shutdown() { CloseEngine(); Dispose(); }
        public void Dispose()
        { lock (_lifetime) { if (_ownerClosed) return; _ownerClosed = true; } Release(); }
        private async Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string tag, CancellationToken ct)
        {
            lock (_lifetime) { ct.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(_disposed || _engineClosed, this); _references++; }
            try
            {
                await _gate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    // Bound total OCR starts across all regions; never multiply model threads.
                    long gap = _minimumGap - (Environment.TickCount64 - _lastStart);
                    if (gap > 0) await Task.Delay((int)gap, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested(); _lastStart = Environment.TickCount64;
                    var result = await _engine.RecognizeAsync(bitmap, tag, _shutdown.Token).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested(); return result;
                }
                finally { _gate.Release(); }
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            finally { Release(); }
        }
        private sealed class Lease(SharedOcrPool pool) : IOcrEngine, IDisposable
        {
            private int _disposed;
            public string Name => pool._engine.Name;
            public IReadOnlyList<string> Languages => pool._engine.Languages;
            public bool RetryAllLanguagesWhenEmpty => pool._engine.RetryAllLanguagesWhenEmpty;
            public bool UsesLanguageSpecificModels => pool._engine.UsesLanguageSpecificModels;
            public bool Supports(string languageTag) => pool._engine.Supports(languageTag);
            public Task<OcrOutcome> RecognizeAsync(Bitmap bitmap, string languageTag, CancellationToken ct)
            {
                if (Volatile.Read(ref _disposed) != 0) { ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(Lease)); }
                return pool.RecognizeAsync(bitmap, languageTag, ct);
            }
            public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) pool.Release(); }
        }
    }
}
