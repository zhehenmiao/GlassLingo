using System;
using System.IO;
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
        private static async Task TestExpandedOverlayAsync()
        {
            foreach (string mode in new[] { "game", "movie" })
            {
                var cfg = new AppConfig { PerformanceMode = mode, OverlayOpacity = 1 };
                var window = new InlineOverlayWindow(cfg);
                try
                {
                    var source = new Int32Rect(400, 320, 180, 28);
                    var layout = new OcrOutcome { Lines = [new OcrLine { Text = "Short source", X = 0, Y = 0, Width = 180, Height = 28 }] };
                    string sentence = "这是一段比原文长很多的译文，不能裁掉句尾文字，也不能裁掉文字底色。";
                    window.Present(new SessionFrame(layout, source, sentence + sentence + sentence + "完整结束。", true, false, 1));
                    await Task.Delay(180); window.UpdateLayout();
                    var hwnd = new WindowInteropHelper(window).Handle;
                    NativeMethods.GetWindowRect(hwnd, out var native);
                    double scale = NativeMethods.ScaleForDpi(NativeMethods.GetDpiForWindow(hwnd));
                    var rect = window.VisibleBounds[0];
                    Check(!window.TextClipped && native.Width > source.Width && native.Height >= rect.Height * scale,
                        mode + " tight one-line OCR selection produces a larger native overlay, including backing height");
                    Check(source.X + rect.Left * scale >= native.Left - 1 && source.Y + rect.Top * scale >= native.Top - 1 &&
                        source.X + rect.Right * scale <= native.Right + 1 && source.Y + rect.Bottom * scale <= native.Bottom + 1,
                        mode + " every background edge lies inside the actual HWND instead of being cut at the OCR selection");
                    var body = Find<TextBlock>(window)!;
                    Check(body.DesiredSize.Height <= body.MaxHeight + .01 && body.Text.EndsWith("完整结束。", StringComparison.Ordinal),
                        mode + " full tail fits in the measured text body with vertical drawing padding");
                    NativeMethods.SetWindowDisplayAffinity(hwnd, 0);
                    WindowUtilities.Position(window, new Int32Rect(native.Left, native.Top, native.Width, native.Height));
                    await Task.Delay(150); GlassMaterials.DwmFlush();
                    using (var capture = Capture.ScreenCapture.CaptureRegion(native.Left, native.Top, native.Width, native.Height))
                    {
                        capture.Save(Path.Combine(Output, mode + "-expanded-native.png"));
                        int x = Math.Clamp((int)Math.Round(source.X + (rect.Left + rect.Width / 2) * scale - native.Left), 0, capture.Width - 1);
                        int y = Math.Clamp((int)Math.Floor(source.Y + (rect.Bottom - .7) * scale - native.Top), 0, capture.Height - 1);
                        var pixel = capture.GetPixel(x, y);
                        Check(Math.Abs(pixel.R - 16) < 12 && Math.Abs(pixel.G - 24) < 12 && Math.Abs(pixel.B - 32) < 12,
                            mode + " native screenshot retains the backing at its lower edge beyond the original selection");
                    }
                    NativeMethods.SetWindowDisplayAffinity(hwnd, 0x11);
                    window.FollowRegion(new Int32Rect(450, 360, 180, 28), true);
                    NativeMethods.GetWindowRect(hwnd, out var moved);
                    var screen = System.Windows.Forms.Screen.FromHandle(hwnd).Bounds;
                    Check(moved.Top == native.Top + 40 && moved.Width == native.Width && moved.Left >= screen.Left && moved.Right <= screen.Right,
                        mode + " moving the source follows vertically and keeps expanded text inside the monitor");
                }
                finally { window.Close(); }
            }
        }
    }
}
