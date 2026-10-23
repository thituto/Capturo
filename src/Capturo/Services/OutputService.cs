using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Capturo.Services;

public static class OutputService
{
    /// <summary>Nome do arquivo de uma captura: Capturo_2026-10-22_14-05-33.png (com sufixo se já existir).</summary>
    public static string MakeCapturePath(string folder, DateTime when, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        string stem = $"Capturo_{when:yyyy-MM-dd_HH-mm-ss}";
        string path = Path.Combine(folder, stem + ".png");
        for (int i = 2; exists(path); i++)
            path = Path.Combine(folder, $"{stem}_{i}.png");
        return path;
    }

    public static string SaveNew(BitmapSource image, AppSettings settings)
    {
        Directory.CreateDirectory(settings.SaveFolder);
        string path = MakeCapturePath(settings.SaveFolder, DateTime.Now);
        Save(image, path);
        return path;
    }

    /// <summary>Salva como PNG ou JPEG conforme a extensão.</summary>
    public static void Save(BitmapSource image, string path)
    {
        string extension = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder;
        BitmapSource frame = image;
        if (extension is ".jpg" or ".jpeg")
        {
            encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            frame = new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0);
        }
        else
        {
            encoder = new PngBitmapEncoder();
        }
        encoder.Frames.Add(BitmapFrame.Create(frame));
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
            encoder.Save(stream);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Copia como bitmap e também como PNG (apps modernos preservam a qualidade pelo formato PNG).</summary>
    public static bool CopyToClipboard(BitmapSource image)
    {
        var png = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(png);

        var data = new DataObject();
        data.SetImage(image);
        data.SetData("PNG", png, false);

        // A área de transferência pode estar presa por outro processo por alguns milissegundos.
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, true);
                return true;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(60);
            }
            catch (COMException ex)
            {
                Log.Error("Falha ao copiar para a área de transferência.", ex);
            }
        }
        return false;
    }

    public static bool CopyText(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return true;
        }
        catch (COMException ex)
        {
            Log.Error("Falha ao copiar texto.", ex);
            return false;
        }
    }
}
