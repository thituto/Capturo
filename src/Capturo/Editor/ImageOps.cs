using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Capturo.Capture;

namespace Capturo.Editor;

/// <summary>Operações que mudam a imagem em si (e reposicionam as anotações para acompanhar).</summary>
public static class ImageOps
{
    public static EditorState Crop(EditorState state, Int32Rect area)
    {
        var offset = new Vector(-area.X, -area.Y);
        return state with
        {
            Image = BitmapUtil.Crop(state.Image, area),
            Items = state.Items.Select(i => i.Offset(offset)).ToImmutableList(),
        };
    }

    /// <summary>
    /// Recorta uma faixa do meio e junta as partes (tira uma área sem interesse de uma tela comprida).
    /// <paramref name="horizontal"/> = faixa de linhas (a imagem fica mais baixa); senão, de colunas.
    /// Anotações depois da faixa sobem (ou vão para a esquerda) junto com a imagem.
    /// </summary>
    public static EditorState CutOut(EditorState state, int start, int length, bool horizontal)
    {
        var image = Pixels.From(state.Image);
        int limit = horizontal ? image.Height : image.Width;
        start = Math.Clamp(start, 0, limit);
        length = Math.Clamp(length, 0, limit - start);
        if (length <= 0 || length >= limit)
            return state;

        Pixels result;
        if (horizontal)
        {
            result = new Pixels(image.Width, image.Height - length);
            Buffer.BlockCopy(image.Data, 0, result.Data, 0, start * image.Stride);
            Buffer.BlockCopy(image.Data, (start + length) * image.Stride, result.Data, start * image.Stride, (image.Height - start - length) * image.Stride);
        }
        else
        {
            result = new Pixels(image.Width - length, image.Height);
            for (int y = 0; y < image.Height; y++)
            {
                Buffer.BlockCopy(image.Data, y * image.Stride, result.Data, y * result.Stride, start * 4);
                Buffer.BlockCopy(image.Data, y * image.Stride + (start + length) * 4, result.Data, y * result.Stride + start * 4, (image.Width - start - length) * 4);
            }
        }

        var shift = horizontal ? new Vector(0, -length) : new Vector(-length, 0);
        var items = state.Items.Select(item =>
        {
            double edge = horizontal ? item.Bounds.Top : item.Bounds.Left;
            return edge >= start + length ? item.Offset(shift) : item;
        }).ToImmutableList();
        return state with { Image = result.ToBitmap(), Items = items };
    }

    /// <summary>Muda o tamanho da imagem; as anotações escalam junto.</summary>
    public static EditorState Resize(EditorState state, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        double sx = width / (double)state.Image.PixelWidth, sy = height / (double)state.Image.PixelHeight;
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen())
            dc.DrawImage(state.Image, new Rect(0, 0, width, height));
        return state with { Image = Render(visual, width, height), Items = state.Items.Select(i => i.Scale(sx, sy)).ToImmutableList() };
    }

    /// <summary>Aumenta a área da imagem com margens numa cor de fundo (ou corta, com margens negativas).</summary>
    public static EditorState ExpandCanvas(EditorState state, Thickness margin, Color background)
    {
        int width = (int)Math.Round(state.Image.PixelWidth + margin.Left + margin.Right);
        int height = (int)Math.Round(state.Image.PixelHeight + margin.Top + margin.Bottom);
        if (width < 1 || height < 1)
            return state;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            dc.DrawImage(state.Image, new Rect(margin.Left, margin.Top, state.Image.PixelWidth, state.Image.PixelHeight));
        }
        var offset = new Vector(margin.Left, margin.Top);
        return state with { Image = Render(visual, width, height), Items = state.Items.Select(i => i.Offset(offset)).ToImmutableList() };
    }

    /// <summary>Corta as bordas de cor uniforme (a cor do canto superior esquerdo, com tolerância).</summary>
    public static EditorState TrimUniformBorders(EditorState state, int tolerance = 10)
    {
        var image = Pixels.From(state.Image);
        uint corner = image.At(0, 0);
        bool Same(uint pixel) =>
            Math.Abs((int)(pixel & 0xFF) - (int)(corner & 0xFF)) <= tolerance
            && Math.Abs((int)((pixel >> 8) & 0xFF) - (int)((corner >> 8) & 0xFF)) <= tolerance
            && Math.Abs((int)((pixel >> 16) & 0xFF) - (int)((corner >> 16) & 0xFF)) <= tolerance;
        bool RowUniform(int y) { for (int x = 0; x < image.Width; x++) if (!Same(image.At(x, y))) return false; return true; }
        bool ColumnUniform(int x, int top, int bottom) { for (int y = top; y < bottom; y++) if (!Same(image.At(x, y))) return false; return true; }

        int t = 0, b = image.Height;
        while (t < b && RowUniform(t)) t++;
        while (b > t && RowUniform(b - 1)) b--;
        if (b - t < 2)
            return state;
        int l = 0, r = image.Width;
        while (l < r && ColumnUniform(l, t, b)) l++;
        while (r > l && ColumnUniform(r - 1, t, b)) r--;
        if (r - l < 2 || (l == 0 && t == 0 && r == image.Width && b == image.Height))
            return state;
        return Crop(state, new Int32Rect(l, t, r - l, b - t));
    }

    private static BitmapSource Render(Visual visual, int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Pixels Pbgra32 em memória, para operações linha a linha.</summary>
    private sealed class Pixels
    {
        public Pixels(int width, int height)
        {
            Width = width;
            Height = height;
            Data = new byte[Stride * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int Stride => Width * 4;
        public byte[] Data { get; }

        public uint At(int x, int y) => BitConverter.ToUInt32(Data, y * Stride + x * 4);

        public static Pixels From(BitmapSource source)
        {
            var converted = source.Format == PixelFormats.Pbgra32 ? source : new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
            var pixels = new Pixels(converted.PixelWidth, converted.PixelHeight);
            converted.CopyPixels(pixels.Data, pixels.Stride, 0);
            return pixels;
        }

        public BitmapSource ToBitmap()
        {
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, Data, Stride);
            bitmap.Freeze();
            return bitmap;
        }
    }
}
