using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Ocr;
using GlassLingo.Overlay;
using GlassLingo.Sessions;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static async Task TestCenteredSubtitlesAsync()
        {
            foreach (string mode in new[] { "game", "movie" })
            {
                var window = new InlineOverlayWindow(new AppConfig { PerformanceMode = mode });
                try
                {
                    var source = new OcrOutcome { Lines = [new OcrLine { Text = "Open the gate", X = 230, Y = 90, Width = 200, Height = 28 }] };
                    var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
                    var region = new Int32Rect(screen.Left + screen.Width / 2 - 330, screen.Top + 100, 660, 240);
                    var frame = new SessionFrame(source, region, "打开城门", true, false, 1);
                    window.Present(frame);
                    double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(new WindowInteropHelper(window).Handle));
                    Check(Math.Abs(window.FirstLeft + window.FirstWidth / 2 - 330 / scale) < 1,
                        mode + " short translation is centered on the original text, not on the selection");
                    double font = window.RenderedFontSize;
                    window.Present(frame with { Text = "请先打开前方的城门，然后跟随队伍一起前往港口寻找下一位同伴。最后几个字必须显示完整。" });
                    var body = Find<TextBlock>(window)!;
                    Check(!window.TextClipped && body.TextTrimming == TextTrimming.None && body.TextWrapping == TextWrapping.Wrap &&
                        body.DesiredSize.Height <= window.VisibleBounds[0].Height + .1,
                        mode + " long subtitle wraps and reserves enough height for its last characters");
                    Check(window.RenderedFontSize == font && Math.Abs(window.FirstLeft + window.FirstWidth / 2 - 330 / scale) < 1,
                        mode + " long subtitles retain font size and source-centered alignment");
                    await Task.Delay(120); await SnapshotAsync(window, mode + "-centered-long-subtitle");
                    window.Present(frame with { Ocr = new OcrOutcome { Lines = [new OcrLine { Text = "Open the gate", X = 0, Y = 0, Width = 160, Height = 28 }] }, Text = "这段长译文应当完整显示，不能把最后的几个字吞掉。" });
                    Check(!window.TextClipped, mode + " near-selection-edge text can expand beyond OCR selection without losing its tail");
                }
                finally { window.Close(); }
            }
        }
    }
}
