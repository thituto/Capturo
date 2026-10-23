using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Capturo.Native.NativeMethods;

namespace Capturo.Capture;

/// <summary>
/// Junta quadros sucessivos de uma área que rola numa imagem alta. Cada linha de pixels vira um hash;
/// o deslocamento entre dois quadros é o que alinha a maior parte das linhas "com conteúdo" (linhas lisas não contam).
/// Cabeçalho e rodapé fixos (iguais nos dois quadros) ficam fora da comparação e aparecem uma vez só.
/// </summary>
public sealed class ScrollStitcher
{
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;
    private readonly List<byte[]> _rows = new();
    private byte[]? _previous;
    private ulong[]? _previousHashes;
    private bool[]? _previousPlain;
    private int _header = -1;
    private int _footer;

    public ScrollStitcher(int width, int height)
    {
        _width = width;
        _height = height;
        _stride = width * 4;
    }

    public int Height => _rows.Count + (_previous == null ? 0 : _footer);

    /// <summary>Acrescenta um quadro. Devolve false quando não houve rolagem (fim da página) ou o alinhamento falhou.</summary>
    public bool Add(byte[] frame)
    {
        var (hashes, plain) = Analyze(frame);
        if (_previous == null)
        {
            Store(frame, hashes, plain);
            return true;
        }

        if (hashes.AsSpan().SequenceEqual(_previousHashes))
            return false;

        if (_header < 0)
        {
            _header = Math.Min(CommonPrefix(_previousHashes!, hashes), _height / 3);
            _footer = Math.Min(CommonSuffix(_previousHashes!, hashes), _height / 3);
            AppendRows(_previous, 0, _height - _footer);
        }

        int shift = FindShift(_previousHashes!, hashes, plain, _header, _footer);
        if (shift <= 0)
            return false;
        AppendRows(frame, _height - _footer - shift, _height - _footer);
        Store(frame, hashes, plain);
        return true;
    }

    public BitmapSource Build()
    {
        if (_previous == null)
            throw new InvalidOperationException("Nenhum quadro capturado.");
        if (_header < 0)
            AppendRows(_previous, 0, _height - _footer);
        AppendRows(_previous, _height - _footer, _height);
        _footer = 0;

        var pixels = new byte[_rows.Count * _stride];
        for (int i = 0; i < _rows.Count; i++)
            Buffer.BlockCopy(_rows[i], 0, pixels, i * _stride, _stride);
        var bitmap = BitmapSource.Create(_width, _rows.Count, 96, 96, PixelFormats.Bgr32, null, pixels, _stride);
        bitmap.Freeze();
        return bitmap;
    }

    private void Store(byte[] frame, ulong[] hashes, bool[] plain)
    {
        _previous = frame;
        _previousHashes = hashes;
        _previousPlain = plain;
    }

    private void AppendRows(byte[] frame, int from, int to)
    {
        for (int y = Math.Max(0, from); y < Math.Min(_height, to); y++)
        {
            var row = new byte[_stride];
            Buffer.BlockCopy(frame, y * _stride, row, 0, _stride);
            _rows.Add(row);
        }
    }

    private (ulong[] Hashes, bool[] Plain) Analyze(byte[] frame)
    {
        var hashes = new ulong[_height];
        var plain = new bool[_height];
        for (int y = 0; y < _height; y++)
        {
            int offset = y * _stride;
            ulong hash = 14695981039346656037UL;
            bool uniform = true;
            int first = BitConverter.ToInt32(frame, offset) & 0xFFFFFF;
            for (int x = 0; x < _width; x++)
            {
                int pixel = BitConverter.ToInt32(frame, offset + x * 4) & 0xFFFFFF;
                if (pixel != first) uniform = false;
                hash = (hash ^ (uint)pixel) * 1099511628211UL;
            }
            hashes[y] = hash;
            plain[y] = uniform;
        }
        return (hashes, plain);
    }

    internal static int CommonPrefix(ulong[] a, ulong[] b)
    {
        int n = 0;
        while (n < a.Length && a[n] == b[n]) n++;
        return n;
    }

    internal static int CommonSuffix(ulong[] a, ulong[] b)
    {
        int n = 0;
        while (n < a.Length && a[a.Length - 1 - n] == b[b.Length - 1 - n]) n++;
        return n;
    }

    /// <summary>Quantas linhas o conteúdo subiu entre <paramref name="previous"/> e <paramref name="next"/>; -1 se não achar.</summary>
    internal static int FindShift(ulong[] previous, ulong[] next, bool[] nextPlain, int header, int footer)
    {
        int body = previous.Length - header - footer;
        if (body < 16)
            return -1;
        int minimumOverlap = Math.Max(8, body / 10);
        int bestShift = -1;
        double bestScore = 0;
        int bestCompared = 0;
        for (int shift = 1; shift <= body - minimumOverlap; shift++)
        {
            int overlap = body - shift, compared = 0, matched = 0;
            for (int k = 0; k < overlap; k++)
            {
                if (nextPlain[header + k])
                    continue;
                compared++;
                if (next[header + k] == previous[header + k + shift])
                    matched++;
            }
            if (compared < Math.Max(4, minimumOverlap / 4))
                continue;
            double score = matched / (double)compared;
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && compared > bestCompared))
            {
                bestScore = score;
                bestShift = shift;
                bestCompared = compared;
            }
        }
        return bestScore >= 0.9 ? bestShift : -1;
    }
}

/// <summary>Rola a área selecionada com a roda do mouse e costura os quadros.</summary>
public static class ScrollingCapture
{
    private const int MaxHeight = 30000;

    public static async Task<BitmapSource> RunAsync(PxRect area)
    {
        GetCursorPos(out var saved);
        var stitcher = new ScrollStitcher(area.Width, area.Height);
        try
        {
            SetCursorPos(area.X + area.Width / 2, area.Y + area.Height / 2);
            stitcher.Add(GrabPixels(area));
            int clicks = Math.Clamp(area.Height / 300, 1, 5);
            for (int i = 0; i < 300 && stitcher.Height < MaxHeight; i++)
            {
                if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
                    break;
                MouseWheel(-clicks);
                await Task.Delay(380);
                if (!stitcher.Add(GrabPixels(area)))
                    break;
            }
        }
        finally
        {
            SetCursorPos(saved.X, saved.Y);
        }
        return stitcher.Build();
    }

    private static byte[] GrabPixels(PxRect area)
    {
        var image = ScreenGrabber.Grab(area);
        var converted = image.Format == PixelFormats.Bgr32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0);
        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        converted.CopyPixels(new Int32Rect(0, 0, area.Width, area.Height), pixels, stride, 0);
        return pixels;
    }
}
