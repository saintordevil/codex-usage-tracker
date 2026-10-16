using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace UsageDock;

// Explicit QA only: capture the Windows-composited pixels of our own window.
// No keyboard, mouse, focus, or clipboard interaction is involved.
internal sealed record NativeCaptureResult(long windowHandle, int width, int height, double dpi, int corners, int borderReadHResult,
    int cornerSetHResult, int borderSetHResult, double meanPixelError, double gutterPixelError, double perimeterPixelError, double pixelsOverTolerancePercent, bool noExtraNativeFrame);

internal static class NativeVisualCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int length);

    internal static async Task<NativeCaptureResult> CaptureAsync(MainWindow window, string path)
    {
        if (!window.Controller.Qa) throw new InvalidOperationException("Native QA capture is disabled.");
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelper.GetDpi(window);
        var matte = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual,
            Left = window.Left - 8, Top = window.Top - 8, Width = window.ActualWidth + 16, Height = window.ActualHeight + 16,
            Background = Ui.Brush("#487b86") };
        bool wasTopmost = window.Topmost;
        try
        {
            matte.Show();
            SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x13); // NOACTIVATE | NOMOVE | NOSIZE
            SetWindowPos(new WindowInteropHelper(matte).Handle, handle, 0, 0, 0, 0, 0x13);
            await Task.Delay(150); DwmFlush();
            GetWindowRect(handle, out var bounds);
            using var bitmap = new Bitmap(bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
            using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
            bitmap.Save(path, ImageFormat.Png);

            // Compare actual desktop composition with the same-size WPF render on this matte.
            var rendered = MainWindow.RenderImage((FrameworkElement)window.Content);
            var pixels = new byte[rendered.PixelWidth * rendered.PixelHeight * 4];
            rendered.CopyPixels(pixels, rendered.PixelWidth * 4, 0);
            double error = 0, gutterError = 0, perimeterError = 0; int samples = 0, gutterSamples = 0, perimeterSamples = 0, mismatched = 0;
            int width = Math.Min(bitmap.Width, rendered.PixelWidth), height = Math.Min(bitmap.Height, rendered.PixelHeight);
            int gutter = (int)Math.Round(12 * dpi.DpiScaleX);
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int i = (y * rendered.PixelWidth + x) * 4; double alpha = pixels[i + 3] / 255d;
                var actual = bitmap.GetPixel(x, y);
                var difference = (Math.Abs(actual.R - (pixels[i + 2] + 72 * (1 - alpha))) + Math.Abs(actual.G - (pixels[i + 1] + 123 * (1 - alpha))) + Math.Abs(actual.B - (pixels[i] + 134 * (1 - alpha)))) / 3;
                error += difference; samples++; if (difference > 8) mismatched++;
                if (x < gutter || x >= width - gutter || y < gutter || y >= height - gutter) { gutterError += difference; gutterSamples++; }
                if (x < 2 || x >= width - 2 || y < 2 || y >= height - 2) { perimeterError += difference; perimeterSamples++; }
            }
            int cornerRead = DwmGetWindowAttribute(handle, 33, out int corners, 4);
            int borderRead = DwmGetWindowAttribute(handle, 34, out _, 4); // This attribute may be set-only on the current OS.
            double perimeter = perimeterError / Math.Max(1, perimeterSamples);
            return new(handle.ToInt64(), width, height, dpi.PixelsPerInchX, corners, borderRead, window.NativeCornerResult, window.NativeBorderResult,
                error / Math.Max(1, samples), gutterError / Math.Max(1, gutterSamples), perimeter,
                100d * mismatched / Math.Max(1, samples), cornerRead == 0 && corners == 1 && window.NativeCornerResult == 0 && window.NativeBorderResult == 0 && perimeter < 2);
        }
        finally
        {
            matte.Close();
            SetWindowPos(handle, new IntPtr(wasTopmost ? -1 : -2), 0, 0, 0, 0, 0x13);
        }
    }
}
