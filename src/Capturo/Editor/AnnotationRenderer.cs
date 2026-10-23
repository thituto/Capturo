using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Capturo.Editor;

/// <summary>Transforma uma anotação em elementos WPF posicionados num Canvas do tamanho da imagem.</summary>
public static class AnnotationRenderer
{
    private const int PixelBlock = 10;

    public static FrameworkElement Create(Annotation annotation, BitmapSource source) => annotation switch
    {
        ArrowAnnotation a => Arrow(a),
        LineAnnotation l => Line(l),
        RectangleAnnotation r => Outline(new Rectangle(), r.Rect, r.Color, r.Thickness),
        EllipseAnnotation e => Outline(new Ellipse(), e.Rect, e.Color, e.Thickness),
        PenAnnotation p => Pen(p),
        TextAnnotation t => Text(t),
        StepAnnotation s => Step(s),
        PixelateAnnotation b => Pixelate(b, source),
        _ => throw new NotSupportedException(annotation.GetType().Name),
    };

    private static FrameworkElement Arrow(ArrowAnnotation a)
    {
        var brush = Brush(a.Color);
        var direction = a.End - a.Start;
        double length = direction.Length;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (length < 1)
            {
                ctx.BeginFigure(a.Start, false, false);
                ctx.LineTo(a.End, true, false);
            }
            else
            {
                direction.Normalize();
                double head = Math.Min(Math.Max(12, a.Thickness * 4), length);
                var normal = new Vector(-direction.Y, direction.X);
                var headBase = a.End - direction * head;
                double halfWidth = head * 0.55;

                ctx.BeginFigure(a.Start, false, false);
                ctx.LineTo(headBase, true, true);
                ctx.BeginFigure(a.End, true, true);
                ctx.LineTo(headBase + normal * halfWidth, true, true);
                ctx.LineTo(headBase - normal * halfWidth, true, true);
            }
        }
        geometry.Freeze();
        return new Path
        {
            Data = geometry,
            Stroke = brush,
            Fill = brush,
            StrokeThickness = a.Thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    private static FrameworkElement Line(LineAnnotation l) => new Line
    {
        X1 = l.Start.X,
        Y1 = l.Start.Y,
        X2 = l.End.X,
        Y2 = l.End.Y,
        Stroke = Brush(l.Color),
        StrokeThickness = l.Thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
    };

    private static FrameworkElement Outline(Shape shape, Rect rect, Color color, double thickness)
    {
        shape.Width = Math.Max(1, rect.Width);
        shape.Height = Math.Max(1, rect.Height);
        shape.Stroke = Brush(color);
        shape.StrokeThickness = thickness;
        Canvas.SetLeft(shape, rect.X);
        Canvas.SetTop(shape, rect.Y);
        return shape;
    }

    private static FrameworkElement Pen(PenAnnotation p)
    {
        var geometry = new StreamGeometry();
        if (!p.Points.IsDefaultOrEmpty)
        {
            using var ctx = geometry.Open();
            ctx.BeginFigure(p.Points[0], false, false);
            if (p.Points.Length == 1)
                ctx.LineTo(p.Points[0] + new Vector(0.1, 0.1), true, true);
            else
                ctx.PolyLineTo(p.Points.Skip(1).ToList(), true, true);
        }
        geometry.Freeze();
        var color = p.Highlighter ? Color.FromArgb(0x70, p.Color.R, p.Color.G, p.Color.B) : p.Color;
        return new Path
        {
            Data = geometry,
            Stroke = Brush(color),
            StrokeThickness = p.StrokeWidth,
            StrokeStartLineCap = p.Highlighter ? PenLineCap.Square : PenLineCap.Round,
            StrokeEndLineCap = p.Highlighter ? PenLineCap.Square : PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
    }

    private static FrameworkElement Text(TextAnnotation t)
    {
        var block = new TextBlock
        {
            Text = t.Text,
            Foreground = Brush(t.Color),
            FontSize = t.FontSize,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
        };
        Canvas.SetLeft(block, t.Origin.X);
        Canvas.SetTop(block, t.Origin.Y);
        return block;
    }

    private static FrameworkElement Step(StepAnnotation s)
    {
        var grid = new Grid { Width = s.Diameter, Height = s.Diameter };
        grid.Children.Add(new Ellipse { Fill = Brush(s.Color), Stroke = Brushes.White, StrokeThickness = 2 });
        grid.Children.Add(new TextBlock
        {
            Text = s.Number.ToString(),
            Foreground = Brushes.White,
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = s.Diameter * 0.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(grid, s.Center.X - s.Diameter / 2);
        Canvas.SetTop(grid, s.Center.Y - s.Diameter / 2);
        return grid;
    }

    private static FrameworkElement Pixelate(PixelateAnnotation b, BitmapSource source)
    {
        var area = ToPixelArea(b.Rect, source);
        if (area.Width <= 0 || area.Height <= 0)
            return new Canvas();
        var region = new CroppedBitmap(source, area);
        double factor = 1.0 / PixelBlock;
        var tiny = new TransformedBitmap(region, new ScaleTransform(factor, factor));
        var image = new Image { Source = tiny, Width = area.Width, Height = area.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(image, area.X);
        Canvas.SetTop(image, area.Y);
        return image;
    }

    /// <summary>Arredonda um retângulo para pixels inteiros dentro da imagem.</summary>
    public static Int32Rect ToPixelArea(Rect rect, BitmapSource source)
    {
        if (rect.IsEmpty)
            return Int32Rect.Empty;
        int left = Math.Clamp((int)Math.Floor(rect.Left), 0, source.PixelWidth);
        int top = Math.Clamp((int)Math.Floor(rect.Top), 0, source.PixelHeight);
        int right = Math.Clamp((int)Math.Ceiling(rect.Right), 0, source.PixelWidth);
        int bottom = Math.Clamp((int)Math.Ceiling(rect.Bottom), 0, source.PixelHeight);
        return new Int32Rect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
