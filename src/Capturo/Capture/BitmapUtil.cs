using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturo.Capture;

public static class BitmapUtil
{
    /// <summary>Copia os pixels para um bitmap independente, congelado e a 96 DPI (1 pixel = 1 unidade WPF).</summary>
    public static BitmapSource Materialize(BitmapSource source)
    {
        BitmapSource converted = source.Format == PixelFormats.Bgr32 || source.Format == PixelFormats.Pbgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        int stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            converted.Format, null, pixels, stride);
        result.Freeze();
        return result;
    }

    public static BitmapSource Crop(BitmapSource source, Int32Rect area) =>
        Materialize(new CroppedBitmap(source, area));

    /// <summary>Cor de um pixel de um bitmap Bgr32 ou Pbgra32.</summary>
    public static Color GetPixel(BitmapSource source, int x, int y)
    {
        if (x < 0 || y < 0 || x >= source.PixelWidth || y >= source.PixelHeight)
            return Colors.Transparent;
        var buffer = new byte[4];
        source.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
        return Color.FromRgb(buffer[2], buffer[1], buffer[0]);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
