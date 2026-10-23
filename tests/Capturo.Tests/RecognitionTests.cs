using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturo.Capture;
using Xunit;
using ZXing;
using ZXing.Common;

namespace Capturo.Tests;

public class RecognitionTests
{
    [Fact]
    public void Reads_a_qr_code_from_an_image()
    {
        var writer = new BarcodeWriterPixelData
        {
            Format = BarcodeFormat.QR_CODE,
            Options = new EncodingOptions { Width = 240, Height = 240, Margin = 2 },
        };
        var data = writer.Write("https://example.com/capturo");
        var image = BitmapSource.Create(data.Width, data.Height, 96, 96, PixelFormats.Bgra32, null, data.Pixels, data.Width * 4);

        Assert.Equal("https://example.com/capturo", CodeReader.Read(image));
    }

    [Fact]
    public async Task Recognizes_printed_text_with_windows_ocr()
    {
        var image = await RunOnSta(() => RenderText("Capturo 2026 funciona"));
        string text = await TextRecognizer.RecognizeAsync(image);
        Assert.Contains("Capturo", text);
        Assert.Contains("2026", text);
    }

    private static BitmapSource RenderText(string text)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, 640, 120));
            var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 40, Brushes.Black, 1.0);
            dc.DrawText(formatted, new Point(20, 30));
        }
        var bitmap = new RenderTargetBitmap(640, 120, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Task<T> RunOnSta<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>();
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
