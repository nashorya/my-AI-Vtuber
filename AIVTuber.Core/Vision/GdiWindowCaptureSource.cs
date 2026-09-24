using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIVTuber.Core.Vision;

/// <summary>
/// Production capture source using per-HWND GDI capture (PrintWindow, BitBlt fallback).
/// Captures only the exact target window — never the desktop, never a substitute window.
/// This is a partial implementation of VIS-01: the preferred path is Windows Graphics Capture
/// via CreateForWindow interop (plan [D10]); GDI is used until WGC interop is validated on
/// the target WPF/Windows build. Both implement <see cref="IWindowCaptureSource"/> so the
/// swap does not touch the worker or runtime wiring.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class GdiWindowCaptureSource : IWindowCaptureSource
{
    public WindowIdentity? ProbeWindow(long hwnd)
    {
        var h = (nint)hwnd;
        if (!IsWindow(h)) return null;
        _ = GetWindowThreadProcessId(h, out var pid);
        string processName;
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            processName = p.ProcessName;
        }
        catch
        {
            processName = ""; // process died between IsWindow and here
        }
        var sb = new System.Text.StringBuilder(512);
        _ = GetWindowText(h, sb, sb.Capacity);
        GetClientRect(h, out var rc);
        return new WindowIdentity(hwnd, (int)pid, processName, sb.ToString(), rc.Right, rc.Bottom);
    }

    public CaptureResult Capture(WindowTarget target, VisionConfig config)
    {
        var identity = ProbeWindow(target.Identity.Hwnd);
        if (identity is null)
            return CaptureResult.Fail(CaptureStatus.WindowClosed, "window handle is gone");
        if (IsIconic((nint)target.Identity.Hwnd))
            return CaptureResult.Fail(CaptureStatus.Minimized, "window is minimized");
        if (!identity.SameWindowAs(target.Identity))
            // Never capture content of a different window that reused the handle.
            return CaptureResult.Fail(CaptureStatus.IdentityChanged,
                $"identity changed: '{identity.WindowTitle}' {identity.Width}x{identity.Height} pid={identity.ProcessId}");

        GetClientRect((nint)target.Identity.Hwnd, out var client);
        var width = client.Right;
        var height = client.Bottom;
        if (width <= 0 || height <= 0)
            return CaptureResult.Fail(CaptureStatus.WindowClosed, "empty client area");

        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
        var bmpData = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, bitmap.PixelFormat);
        var memDc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = width,
                    biHeight = -height, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0, // BI_RGB
                },
            };
            var dib = CreateDIBSection(memDc, ref info, 0, out var pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero)
                return CaptureResult.Fail(CaptureStatus.DeviceLost, "CreateDIBSection failed");
            try
            {
                using var wndDc = GetWindowDC((nint)target.Identity.Hwnd);
                if (wndDc.Value == IntPtr.Zero)
                    return CaptureResult.Fail(CaptureStatus.PermissionFailure, "GetWindowDC returned NULL");
                // PW_RENDERFULLCONTENT (2) captures DirectComposition windows on Win 8.1+.
                var printed = PrintWindow((nint)target.Identity.Hwnd, memDc, 2);
                if (!printed)
                {
                    // Fallback: BitBlt from the window DC — still this window only.
                    if (!BitBlt(memDc, 0, 0, width, height, wndDc.Value, 0, 0, SRCCOPY | CAPTUREBLT))
                        return CaptureResult.Fail(CaptureStatus.PermissionFailure, "PrintWindow and BitBlt both failed");
                }
                // Black-frame check before copying: a capture that failed silently often reads as all-black.
                if (IsAllBlack(pixels, width, height))
                    return CaptureResult.Fail(CaptureStatus.BlackFrame, "captured frame is uniformly black");
                CopyMemory(bmpData.Scan0, pixels, (nuint)(width * height * 4));
            }
            finally
            {
                _ = DeleteObject(dib);
            }
        }
        finally
        {
            _ = DeleteDC(memDc);
        }
        bitmap.UnlockBits(bmpData);

        var (hash, changed) = (ComputeHash(bitmap), false); // ChangedSinceLast set by the service
        var processed = Process(bitmap, config);
        if (processed is null)
            return CaptureResult.Fail(CaptureStatus.PermissionFailure, "image processing failed (limits too tight)");

        return CaptureResult.Ok(new CapturedFrame
        {
            FrameId = $"f-{Guid.NewGuid():N}",
            SourceWindowId = target.SourceWindowId,
            CaptureEpoch = target.CaptureEpoch,
            CapturedAtMs = 0, // service stamps the clock
            Jpeg = processed,
            ContentHash = hash,
            ChangedSinceLast = changed,
        });
    }

    public void Dispose() { }

    // --- processing: ROI crop, masks, downscale, JPEG, byte cap ---

    internal static Bitmap? ApplyProcessing(Bitmap src, VisionConfig config)
    {
        var rect = new Rectangle(0, 0, src.Width, src.Height);
        if (config.Roi is { } roi && roi.Width > 0 && roi.Height > 0)
        {
            rect = Rectangle.Intersect(rect, new Rectangle(roi.X, roi.Y, roi.Width, roi.Height));
            if (rect.IsEmpty) return null;
        }
        var cropped = src.Clone(rect, src.PixelFormat);
        foreach (var m in config.Masks)
        {
            var r = Rectangle.Intersect(new Rectangle(0, 0, cropped.Width, cropped.Height),
                new Rectangle(m.X - rect.X, m.Y - rect.Y, m.Width, m.Height));
            if (r.IsEmpty) continue;
            using var g = Graphics.FromImage(cropped);
            using var brush = new SolidBrush(Color.Black);
            g.FillRectangle(brush, r);
        }
        return cropped;
    }

    internal static byte[] EncodeJpeg(Bitmap image, int quality)
    {
        quality = Math.Clamp(quality, 1, 100);
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(c => c.MimeType == "image/jpeg")
            ?? throw new InvalidOperationException("JPEG encoder unavailable");
        using var ps = new EncoderParameters(1);
        ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
        using var ms = new MemoryStream();
        image.Save(ms, encoder, ps);
        return ms.ToArray();
    }

    private static byte[]? Process(Bitmap src, VisionConfig config)
    {
        var final = ApplyProcessing(src, config);
        if (final is null) return null;
        try
        {
            var longEdge = Math.Max(final.Width, final.Height);
            var scale = config.MaxLongEdge > 0 && longEdge > config.MaxLongEdge
                ? (double)config.MaxLongEdge / longEdge
                : 1.0;
            if (scale < 1.0)
                final = Downscale(final, scale);

            // Enforce upload byte cap by stepping quality down, then edges.
            var quality = Math.Clamp(config.JpegQuality, 1, 100);
            var bytes = EncodeJpeg(final, quality);
            while (bytes.Length > config.MaxUploadBytes && quality > 10)
            {
                quality -= 15;
                bytes = EncodeJpeg(final, quality);
            }
            while (bytes.Length > config.MaxUploadBytes && Math.Max(final.Width, final.Height) > 320)
            {
                final = Downscale(final, 0.5);
                bytes = EncodeJpeg(final, quality);
            }
            return bytes;
        }
        finally
        {
            final.Dispose();
        }
    }

    private static Bitmap Downscale(Bitmap src, double scale)
    {
        var w = Math.Max(1, (int)Math.Round(src.Width * scale));
        var h = Math.Max(1, (int)Math.Round(src.Height * scale));
        var scaled = new Bitmap(w, h);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(src, 0, 0, w, h);
        }
        src.Dispose();
        return scaled;
    }

    internal static bool IsAllBlackPixels(nint scan0, int width, int height)
        => IsAllBlack(scan0, width, height);

    private static unsafe bool IsAllBlack(nint scan0, int width, int height)
    {
        var p = (byte*)scan0;
        var stride = width * 4;
        var step = Math.Max(1, (width * height) / 20_000); // sample ~20k pixels
        var n = 0;
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
            {
                var idx = y * stride + x * 4;
                if (idx % (step * 4) != 0) continue;
                // BGRA; ignore fully transparent capture artefacts too.
                if (p[idx + 2] > 8 || p[idx + 1] > 8 || p[idx] > 8) return false;
                if (++n >= 20_000) return true;
            }
        return n > 0;
    }

    internal static ulong ComputeHash(Bitmap image)
    {
        // FNV-1a over a coarse luminance grid: cheap non-model change signal.
        const int grid = 16;
        ulong hash = 14695981039346656037;
        void Mix(byte b)
        {
            hash ^= b;
            hash *= 1099511628211;
        }
        for (var gy = 0; gy < grid; gy++)
        {
            var y = (int)((long)gy * image.Height / grid);
            for (var gx = 0; gx < grid; gx++)
            {
                var x = (int)((long)gx * image.Width / grid);
                var c = image.GetPixel(x, y);
                Mix((byte)((c.R * 299 + c.G * 587 + c.B * 114) / 1000));
            }
        }
        return hash;
    }

    // --- Win32 ---

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint hWnd, System.Text.StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(nint hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(nint hwnd, nint hdcBlt, uint nFlags);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    private readonly struct DcHandle : IDisposable
    {
        private readonly nint _hWnd;
        public readonly nint Value;
        public DcHandle(nint hWnd, nint dc) { _hWnd = hWnd; Value = dc; }
        public void Dispose()
        {
            if (Value != nint.Zero) _ = ReleaseDC(_hWnd, Value);
        }
    }

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hWnd, nint hDC);

    private static DcHandle GetWindowDC(nint hWnd) => new(hWnd, GetDC(hWnd));

    [DllImport("gdi32.dll")]
    private static extern nint CreateCompatibleDC(nint hdc);

    [DllImport("gdi32.dll")]
    private static extern nint CreateDIBSection(nint hdc, ref BITMAPINFO pbmi, uint iUsage,
        out nint ppvBits, nint hSection, uint dwOffset);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(nint hdcDest, int nXDest, int nYDest, int nWidth, int nHeight,
        nint hdcSrc, int nXSrc, int nYSrc, uint dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(nint hdc);

    [DllImport("kernel32.dll")]
    private static extern void CopyMemory(nint dest, nint src, nuint count);
}
