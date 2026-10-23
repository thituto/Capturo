using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Capturo.Editor;

public enum TornEdges
{
    None,
    Bottom,
    BottomRight,
    All,
}

/// <summary>Efeitos aplicados à imagem inteira na exportação: borda, sombra, cantos arredondados, borda rasgada.</summary>
public sealed record ImageEffects(bool Border, Color BorderColor, double BorderWidth, bool Shadow, bool RoundedCorners, TornEdges Torn)
{
    public static readonly ImageEffects None = new(false, Color.FromRgb(0x9C, 0xA3, 0xAF), 2, false, false, TornEdges.None);

    public const double CornerRadius = 14;
    public const double ShadowMargin = 32;

    public bool Any => Border || Shadow || RoundedCorners || Torn != TornEdges.None;

    /// <summary>Contorno da imagem com os efeitos de forma (cantos ou borda rasgada), em coordenadas da imagem.</summary>
    public Geometry Outline(Size size)
    {
        var rect = new Rect(size);
        Geometry geometry = Torn != TornEdges.None
            ? TornOutline(size, Torn)
            : RoundedCorners
                ? new RectangleGeometry(rect, CornerRadius, CornerRadius)
                : new RectangleGeometry(rect);
        geometry.Freeze();
        return geometry;
    }

    /// <summary>Imagem final com os efeitos; a sombra acrescenta uma margem transparente.</summary>
    public BitmapSource Apply(BitmapSource image)
    {
        if (!Any)
            return image;

        var size = new Size(image.PixelWidth, image.PixelHeight);
        double margin = Shadow ? ShadowMargin : 0;
        var outline = Outline(size);

        var root = new Canvas { Width = size.Width + margin * 2, Height = size.Height + margin * 2 };
        if (Shadow)
            root.Children.Add(ShadowShape(outline, margin));

        var picture = new Grid { Width = size.Width, Height = size.Height, Clip = outline };
        picture.Children.Add(new Image { Source = image, Width = size.Width, Height = size.Height, Stretch = Stretch.Fill });
        if (Border)
            picture.Children.Add(BorderShape(outline));
        Canvas.SetLeft(picture, margin);
        Canvas.SetTop(picture, margin);
        root.Children.Add(picture);

        root.Measure(new Size(root.Width, root.Height));
        root.Arrange(new Rect(0, 0, root.Width, root.Height));
        root.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)root.Width, (int)root.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>Contorno desenhado por dentro da imagem (metade externa fica fora do recorte).</summary>
    public Path BorderShape(Geometry outline) => new()
    {
        Data = outline,
        Stroke = new SolidColorBrush(BorderColor),
        StrokeThickness = BorderWidth * 2,
        StrokeLineJoin = PenLineJoin.Round,
        IsHitTestVisible = false,
    };

    public static Path ShadowShape(Geometry outline, double offset)
    {
        var shape = new Path
        {
            Data = outline,
            Fill = Brushes.Black,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 22, ShadowDepth = 6, Direction = 270, Opacity = 0.55, Color = Colors.Black },
        };
        Canvas.SetLeft(shape, offset);
        Canvas.SetTop(shape, offset);
        return shape;
    }

    /// <summary>Polígono com "dentes" irregulares nos lados escolhidos. A semente fixa deixa o rasgo igual na prévia e no arquivo.</summary>
    private static Geometry TornOutline(Size size, TornEdges torn)
    {
        const double depth = 7;
        var random = new Random(20261022);
        bool top = torn == TornEdges.All;
        bool right = torn is TornEdges.BottomRight or TornEdges.All;
        bool bottom = true;
        bool left = torn == TornEdges.All;

        var points = new List<Point>();
        void Edge(Point from, Point to, bool tear, Vector inward)
        {
            points.Add(from);
            if (!tear)
                return;
            var along = to - from;
            double length = along.Length;
            along.Normalize();
            for (double d = 6 + random.NextDouble() * 8; d < length - 6; d += 6 + random.NextDouble() * 10)
                points.Add(from + along * d + inward * (random.NextDouble() * depth));
        }

        double w = size.Width, h = size.Height;
        Edge(new Point(0, 0), new Point(w, 0), top, new Vector(0, 1));
        Edge(new Point(w, 0), new Point(w, h), right, new Vector(-1, 0));
        Edge(new Point(w, h), new Point(0, h), bottom, new Vector(0, -1));
        Edge(new Point(0, h), new Point(0, 0), left, new Vector(1, 0));

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], true, true);
            ctx.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        return geometry;
    }
}
