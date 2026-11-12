using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace GlassLingo.Ocr
{
    internal sealed class PaddleOcrEngine : IOcrEngine, IDisposable
    {
        private readonly int _threads;
        private readonly bool _highPerformance;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private NamedPipeServerStream? _pipe;
        private Process? _process;
        private int _disposed;
        internal long WorkerWorkingSet => _process is { HasExited: false } ? _process.WorkingSet64 : 0;
        internal int? WorkerId => _process is { HasExited: false } ? _process.Id : null;
        public PaddleOcrEngine(int threads = 2, bool highPerformance = false)
        { _threads = Math.Clamp(threads, 1, 4); _highPerformance = highPerformance; }
        public string Name => "PaddleOCR · 本地 / " + _threads + " 线程";
        public IReadOnlyList<string> Languages { get; } = new[] { "en-US", "zh-Hans-CN", "zh-Hant-TW", "ja-JP", "fr-FR", "de-DE", "es-ES" };
        public bool RetryAllLanguagesWhenEmpty => false;
        public bool UsesLanguageSpecificModels => false;
        public bool Supports(string tag) => tag == "auto" || Languages.ContainsTag(tag);
        internal async Task WarmAsync(CancellationToken ct)
        {
            using var sample = new Bitmap(32, 32, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(sample)) graphics.Clear(Color.White);
            await RecognizeAsync(sample, "auto", ct).ConfigureAwait(false);
        }
        public async Task<OcrOutcome> RecognizeAsync(Bitmap bmp, string tag, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                ct.ThrowIfCancellationRequested();
                if (_pipe == null || !_pipe.IsConnected)
                {
                    DisposeWorker();
                    string name = "glasslingo-" + Guid.NewGuid().ToString("N");
                    _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    var start = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        Arguments = $"--ocr-worker {name} {_threads}", UseShellExecute = false,
                        CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                        WorkingDirectory = AppContext.BaseDirectory
                    };
                    start.Environment["OMP_NUM_THREADS"] = _threads.ToString();
                    start.Environment["MKL_NUM_THREADS"] = _threads.ToString();
                    start.Environment["OMP_WAIT_POLICY"] = "PASSIVE";
                    _process = Process.Start(start) ?? throw new IOException("无法启动本地 OCR 工作进程");
                    if (Volatile.Read(ref _disposed) != 0 || ct.IsCancellationRequested)
                    { DisposeWorker(); ct.ThrowIfCancellationRequested(); throw new ObjectDisposedException(nameof(PaddleOcrEngine)); }
                    try { _process.PriorityClass = _highPerformance ? ProcessPriorityClass.Normal : ProcessPriorityClass.BelowNormal; } catch { }
                    using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    connect.CancelAfter(TimeSpan.FromSeconds(15));
                    await _pipe.WaitForConnectionAsync(connect.Token).ConfigureAwait(false);
                }
                var bits = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                byte[] pixels;
                try
                {
                    pixels = new byte[bmp.Width * bmp.Height * 4];
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, y * bmp.Width * 4, bmp.Width * 4);
                }
                finally { bmp.UnlockBits(bits); }
                byte[] header = new byte[8];
                BitConverter.GetBytes(bmp.Width).CopyTo(header, 0); BitConverter.GetBytes(bmp.Height).CopyTo(header, 4);
                await _pipe.WriteAsync(header, ct).ConfigureAwait(false);
                await _pipe.WriteAsync(pixels, ct).ConfigureAwait(false);
                await _pipe.FlushAsync(ct).ConfigureAwait(false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(45));
                byte[] size = new byte[4]; await _pipe.ReadExactlyAsync(size, timeout.Token).ConfigureAwait(false);
                int length = BitConverter.ToInt32(size);
                if (length < 1 || length > 8_000_000) throw new IOException("OCR 返回数据无效");
                byte[] json = new byte[length]; await _pipe.ReadExactlyAsync(json, timeout.Token).ConfigureAwait(false);
                var reply = JsonSerializer.Deserialize<WorkerReply>(json) ?? throw new IOException("OCR 返回为空");
                if (reply.Error != null) throw new InvalidOperationException(reply.Error);
                return reply.Result ?? throw new IOException("OCR 没有返回结果");
            }
            catch { DisposeWorker(); throw; }
            finally { _gate.Release(); }
        }
        public void Dispose() { Interlocked.Exchange(ref _disposed, 1); DisposeWorker(); }
        private void DisposeWorker()
        {
            try { _pipe?.Dispose(); } catch { } _pipe = null;
            try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); } catch { }
            _process?.Dispose(); _process = null;
        }
    }
    internal sealed class WorkerReply
    {
        public OcrOutcome? Result { get; set; }
        public string? Error { get; set; }
    }
    internal static class PaddleWorker
    {
        public static async Task RunAsync(string name, int threads)
        {
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(15000).ConfigureAwait(false);
            var engine = new PaddleLocalEngine(threads);
            while (pipe.IsConnected)
            {
                try
                {
                    byte[] header = new byte[8]; await pipe.ReadExactlyAsync(header).ConfigureAwait(false);
                    int w = BitConverter.ToInt32(header, 0), h = BitConverter.ToInt32(header, 4);
                    if (w < 1 || h < 1 || (long)w * h > 20_000_000) break;
                    byte[] pixels = new byte[checked(w * h * 4)]; await pipe.ReadExactlyAsync(pixels).ConfigureAwait(false);
                    using var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
                    var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                    try { for (int y = 0; y < h; y++) Marshal.Copy(pixels, y * w * 4, IntPtr.Add(bits.Scan0, y * bits.Stride), w * 4); }
                    finally { bmp.UnlockBits(bits); }
                    var reply = new WorkerReply();
                    try { reply.Result = await engine.RecognizeAsync(bmp, "auto", CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { reply.Error = ex.Message; }
                    byte[] json = JsonSerializer.SerializeToUtf8Bytes(reply);
                    await pipe.WriteAsync(BitConverter.GetBytes(json.Length)).ConfigureAwait(false);
                    await pipe.WriteAsync(json).ConfigureAwait(false); await pipe.FlushAsync().ConfigureAwait(false);
                }
                catch (IOException) { break; }
            }
        }
    }
    internal static class LanguageList
    {
        public static bool ContainsTag(this IReadOnlyList<string> list, string tag)
        { foreach (string item in list) if (item.StartsWith(tag.Split('-')[0], StringComparison.OrdinalIgnoreCase)) return true; return false; }
    }
}
