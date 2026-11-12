using System;
using System.IO;
using System.Linq;
using System.Windows;
using GlassLingo.Config;
using GlassLingo.Native;
using GlassLingo.Overlay;
using GlassLingo.Sessions;

namespace GlassLingo.Diagnostics
{
    internal static partial class SelfTests
    {
        private static void TestOptimizations()
        {
            foreach (var work in new[] { new System.Drawing.Rectangle(-1920, 0, 1920, 1040), new System.Drawing.Rectangle(2560, -200, 1600, 900) })
            foreach (double dpi in new[] { 1d, 1.5, 2 })
            {
                var placed = PopupPlacement.Calculate(new Int32Rect(work.Right - 40, work.Bottom - 60, 120, 50), work, (int)(520 * dpi), (int)(330 * dpi));
                Check(work.Contains(new System.Drawing.Rectangle(placed.X, placed.Y, placed.Width, placed.Height)),
                    $"popup physical placement fits target monitor with offset {work.Left} and DPI {dpi}");
            }
            var small = PopupPlacement.Calculate(new Int32Rect(0, 0, 10, 10), new System.Drawing.Rectangle(0, 0, 320, 200), 1000, 1000);
            Check(small.Width == 320 && small.Height == 200, "oversized popup is constrained to target work area");
            Check(RegionOverlap.Duplicate(new Int32Rect(100, 100, 400, 180), new Int32Rect(102, 101, 400, 180)) &&
                !RegionOverlap.Duplicate(new Int32Rect(100, 100, 400, 180), new Int32Rect(300, 100, 400, 180)),
                "near-identical regions are reused while partially overlapping regions remain independent");
            var cfg = new AppConfig();
            var a = new InlineOverlayWindow(cfg, true); var b = new InlineOverlayWindow(cfg, true);
            try
            {
                var frame = new SessionFrame(Layout(("Open gate", 20, 20)), new Int32Rect(100, 100, 400, 180), "打开城门", true, false, 1);
                a.Present(frame); b.Present(frame);
                Check(a.VisibleBounds.Count == 1 && b.VisibleBounds.Count == 0, "overlapping regions never paint subtitles over each other");
                a.Hide(); Check(b.VisibleBounds.Count == 1, "hiding an earlier region immediately restores the suppressed peer subtitle");
                a.Show(); Check(b.VisibleBounds.Count == 0, "showing an earlier region reapplies deterministic ownership");
                a.Close(); Check(b.VisibleBounds.Count == 1, "closing the owning region releases space without another OCR or API request");
                b.FollowRegion(new Int32Rect(600, 100, 400, 180), true);
                Check(b.VisibleBounds.Count == 1, "peer collision placement follows source movement in physical pixels");
            }
            finally { if (a.IsVisible) a.Close(); b.Close(); }
            string folder = Path.Combine(Output, "log-retention-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                string current = Path.Combine(folder, "glasslingo-20261111.log"), old = Path.Combine(folder, "glasslingo-20260101.log");
                File.WriteAllText(old, "old"); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
                File.WriteAllText(Path.Combine(folder, "glasslingo-user.log"), "keep");
                using (var file = File.Create(current)) file.SetLength(Logging.Log.MaxFileBytes);
                Logging.Log.Maintain(folder, current, DateTime.UtcNow);
                Check(File.Exists(current + ".previous") && !File.Exists(current), "large log rotates to a bounded previous file");
                Check(!File.Exists(old) && File.Exists(Path.Combine(folder, "glasslingo-user.log")), "retention removes expired dated logs and preserves unrelated files");
            }
            finally { foreach (string file in Directory.GetFiles(folder)) File.Delete(file); Directory.Delete(folder); }
        }
    }
}
