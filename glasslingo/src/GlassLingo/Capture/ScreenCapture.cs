using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;
using GlassLingo.Logging;

namespace GlassLingo.Capture
{
    /// <summary>
    /// 截屏 + 转 SoftwareBitmap。
    /// 两条 P0 用血换来的规矩，别改：
    /// 1. 用 Graphics.CopyFromScreen，不要 BitBlt + Image.FromHbitmap
    ///    （后者返回的位图依赖原 HBITMAP，DeleteObject 后就炸）
    /// 2. 转 SoftwareBitmap 用 LockBits 直拷像素，不要 BMP 编码往返
    ///    （实测 6ms vs 39ms）
    /// </summary>
    internal static class ScreenCapture
    {
        /// <summary>截一块屏幕区域（物理像素坐标，支持负坐标的多显示器）。</summary>
        public static Bitmap CaptureRegion(int x, int y, int w, int h)
        {
            if (w <= 0 || h <= 0)
                throw new ArgumentOutOfRangeException(nameof(w), $"截取区域尺寸非法 {w}×{h}");

            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.CopyFromScreen(x, y, 0, 0, new System.Drawing.Size(w, h),
                        CopyPixelOperation.SourceCopy);
                }
                return bmp;
            }
            catch
            {
                bmp.Dispose();
                throw;
            }
        }

        /// <summary>
        /// 放大位图，给小字 OCR 用。
        ///
        /// 为什么：Windows.Media.Ocr 在字高低于 ~20px 时掉字很快。
        /// 实测 1020×480 塞 10 行小字（12 号左右），「ms」认成「(s)」、
        /// 「1935,791」认成「193s791」——这不是后处理能救的，是像素不够。
        /// 放大不增加信息，但能让识别器拿到更多采样点。
        ///
        /// 用 HighQualityBicubic 而不是 NearestNeighbor：后者放大后
        /// 锯齿会被当成笔画，反而更糟。
        /// </summary>
        public static Bitmap ScaleUp(Bitmap src, double factor)
        {
            if (factor <= 1.0) return src;

            int w = (int)Math.Round(src.Width * factor);
            int h = (int)Math.Round(src.Height * factor);
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            try
            {
                using (var g = Graphics.FromImage(dst))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                    g.DrawImage(src, new Rectangle(0, 0, w, h));
                }
                return dst;
            }
            catch
            {
                dst.Dispose();
                throw;
            }
        }

        /// <summary>整块虚拟屏幕（所有显示器合起来）。</summary>
        public static Bitmap CaptureVirtualScreen()
        {
            int x = Native.NativeMethods.GetSystemMetrics(Native.NativeMethods.SM_XVIRTUALSCREEN);
            int y = Native.NativeMethods.GetSystemMetrics(Native.NativeMethods.SM_YVIRTUALSCREEN);
            int w = Native.NativeMethods.GetSystemMetrics(Native.NativeMethods.SM_CXVIRTUALSCREEN);
            int h = Native.NativeMethods.GetSystemMetrics(Native.NativeMethods.SM_CYVIRTUALSCREEN);
            if (w <= 0 || h <= 0)
                throw new InvalidOperationException($"取不到虚拟屏幕尺寸 {w}×{h}");
            return CaptureRegion(x, y, w, h);
        }

        /// <summary>Bitmap → SoftwareBitmap（Bgra8）。像素级直拷，无编码往返。</summary>
        public static SoftwareBitmap ToSoftwareBitmap(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = Math.Abs(data.Stride);
                byte[] buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, w, h,
                    BitmapAlphaMode.Premultiplied);
                sb.CopyFromBuffer(CryptographicBuffer.CreateFromByteArray(buf));
                return sb;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
