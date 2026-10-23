using System.Runtime.InteropServices.WindowsRuntime;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;
using ZXing.Common;

namespace Capturo.Capture;

/// <summary>OCR com o mecanismo nativo do Windows (sem envio de dados para fora do PC).</summary>
public static class TextRecognizer
{
    public static async Task<string> RecognizeAsync(BitmapSource image)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"))
            ?? throw new InvalidOperationException("Nenhum idioma de OCR está instalado no Windows (Configurações › Hora e idioma › Idioma).");

        // Texto pequeno é reconhecido melhor ampliado; o mecanismo tem um tamanho máximo de imagem.
        double scale = Math.Min(image.PixelWidth, image.PixelHeight) < 400 ? 2 : 1;
        scale = Math.Min(scale, OcrEngine.MaxImageDimension / (double)Math.Max(image.PixelWidth, image.PixelHeight));
        using var bitmap = ToSoftwareBitmap(scale == 1 ? image : new TransformedBitmap(image, new ScaleTransform(scale, scale)));
        var result = await engine.RecognizeAsync(bitmap);
        return string.Join(Environment.NewLine, result.Lines.Select(l => l.Text));
    }

    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource source)
    {
        var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            bgra.PixelWidth, bgra.PixelHeight, BitmapAlphaMode.Premultiplied);
    }
}

/// <summary>Leitura de QR Code e códigos de barras com ZXing.</summary>
public static class CodeReader
{
    public static string? Read(BitmapSource image)
    {
        var bgra = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true },
        };
        var source = new RGBLuminanceSource(pixels, bgra.PixelWidth, bgra.PixelHeight, RGBLuminanceSource.BitmapFormat.BGRA32);
        return reader.Decode(source)?.Text;
    }
}
