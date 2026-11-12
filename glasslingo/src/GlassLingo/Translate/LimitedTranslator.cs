using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GlassLingo.Translate
{
    internal sealed class LimitedTranslator(ITranslator inner, SemaphoreSlim gate) : ITranslator, IRowTranslator
    {
        public bool SupportsRows => inner is IRowTranslator { SupportsRows: true };
        public async Task<TranslateResult> TranslateRowsAsync(IReadOnlyList<string> rows, string? from, string to,
            Action<IReadOnlyList<TranslatedRow>>? onRows, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await ((IRowTranslator)inner).TranslateRowsAsync(rows, from, to, onRows, ct).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
        public string Name => inner.Name;
        public Task<TranslateResult> TranslateAsync(string text, string? from, string to, CancellationToken ct)
            => Run(text, from, to, null, ct);
        public Task<TranslateResult> TranslateStreamingAsync(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
            => Run(text, from, to, onText, ct);
        private async Task<TranslateResult> Run(string text, string? from, string to, Action<string>? onText, CancellationToken ct)
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try { return await (onText == null ? inner.TranslateAsync(text, from, to, ct) : inner.TranslateStreamingAsync(text, from, to, onText, ct)).ConfigureAwait(false); }
            finally { gate.Release(); }
        }
    }
}
