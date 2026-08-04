using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace NativeWidget.Services;

public static class ScreenOcrService
{
    private const int Srccopy = 0x00CC0020;
    private const int Captureblt = 0x40000000;

    public static async Task<string> CaptureAndReadAsync(Rect region)
    {
        var width = Math.Max(1, (int)Math.Round(region.Width));
        var height = Math.Max(1, (int)Math.Round(region.Height));
        var source = Capture((int)Math.Round(region.X), (int)Math.Round(region.Y), width, height);
        return await ReadAsync(source);
    }

    public static async Task<string> ReadAsync(BitmapSource source)
    {

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
        using var png = new MemoryStream();
        encoder.Save(png);

        using var randomStream = new InMemoryRandomAccessStream();
        using (var output = randomStream.GetOutputStreamAt(0))
        using (var writer = new DataWriter(output))
        {
            writer.WriteBytes(png.ToArray());
            await writer.StoreAsync();
            await writer.FlushAsync();
        }
        randomStream.Seek(0);

        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(randomStream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? throw new InvalidOperationException("Windows OCR chưa có gói ngôn ngữ phù hợp.");
        var result = await engine.RecognizeAsync(bitmap);
        return result.Text.Trim();
    }

    private static BitmapSource Capture(int x, int y, int width, int height)
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var bitmap = CreateCompatibleBitmap(screenDc, width, height);
        var old = SelectObject(memoryDc, bitmap);
        try
        {
            if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, Srccopy | Captureblt))
                throw new InvalidOperationException("Không chụp được vùng màn hình.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            SelectObject(memoryDc, old);
            DeleteObject(bitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr destination, int xDestination, int yDestination,
        int width, int height, IntPtr source, int xSource, int ySource, int operation);
}
