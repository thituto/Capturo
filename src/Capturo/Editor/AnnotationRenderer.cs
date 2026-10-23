using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Capturo.Editor;

/// <summary>Transforma anotações em elementos WPF posicionados num Canvas do tamanho da imagem.</summary>
public static class AnnotationRenderer
{
    private const int PixelBlock = 10;

    /// <summary>
    /// Todos os elementos de uma cena, na ordem de desenho: a máscara dos destaques fica logo acima da imagem
    /// e abaixo das demais anotações, para que setas e textos fora do destaque continuem nítidos.
    /// </summary>
    public static IEnumerable<FrameworkElement> Scene(IReadOnlyList<Annotation> items, BitmapSource source)
    {
        var spotlights = items.OfType<SpotlightAnnotation>().ToList();
        if (spotlights.Count > 0)
            yield return SpotlightMask(spotlights, new Size(source.PixelWidth, source.PixelHeight));
        foreach (var item in items)
        {
            if (item is not SpotlightAnnotation)
                yield return Create(item, source);
        }
    }

    public static FrameworkElement Create(Annotation annotation, BitmapSource source) => annotation switch
    {
        ArrowAnnotation a => Arrow(a),
        LineAnnotation l => Line(l),
        RectangleAnnotation r => Shape(new Rectangle(), r.Rect, r.Color, r.Thickness, r.Style),
        EllipseAnnotation e => Shape(new Ellipse(), e.Rect, e.Color, e.Thickness, e.Style),
        PenAnnotation p => Pen(p),
        TextAnnotation t => Text(t),
        StepAnnotation s => Step(s),
        PixelateAnnotation b => Pixelate(b, source),
        SpotlightAnnotation s => SpotlightMask(new[] { s }, new Size(source.PixelWidth, source.PixelHeight)),
        MagnifierAnnotation m => Magnifier(m, source),
        CalloutAnnotation c => Callout(c),
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

    private static FrameworkElement Shape(Shape shape, Rect rect, Color color, double thickness, ShapeStyle style)
    {
        shape.Width = Math.Max(1, rect.Width);
        shape.Height = Math.Max(1, rect.Height);
        shape.Stroke = Brush(color);
        shape.StrokeThickness = thickness;
        shape.Opacity = Math.Clamp(style.Opacity, 0.05, 1);
        if (style.Filled)
            shape.Fill = Brush(color);
        if (shape is Rectangle rectangle && style.Rounded)
            rectangle.RadiusX = rectangle.RadiusY = Math.Min(16, Math.Min(rect.Width, rect.Height) / 4);
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
            Foreground = Brush(t.Filled ? Geometry2D.ContrastText(t.Color) : t.Color),
            FontSize = t.FontSize,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
        };
        FrameworkElement element = block;
        if (t.Filled)
        {
            element = new Border
            {
                Background = Brush(t.Color),
                CornerRadius = new CornerRadius(t.FontSize * 0.25),
                Padding = new Thickness(t.FontSize * 0.35, t.FontSize * 0.15, t.FontSize * 0.35, t.FontSize * 0.2),
                Child = block,
            };
        }
        Canvas.SetLeft(element, t.Origin.X);
        Canvas.SetTop(element, t.Origin.Y);
        return element;
    }

    private static FrameworkElement Step(StepAnnotation s)
    {
        var grid = new Grid { Width = s.Diameter, Height = s.Diameter };
        grid.Children.Add(new Ellipse { Fill = Brush(s.Color), Stroke = Brushes.White, StrokeThickness = 2 });
        grid.Children.Add(new TextBlock
        {
            Text = s.Number.ToString(),
            Foreground = Brush(Geometry2D.ContrastText(s.Color)),
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

    /// <summary>Uma única máscara escura com um "furo" por destaque.</summary>
    public static FrameworkElement SpotlightMask(IReadOnlyList<SpotlightAnnotation> spotlights, Size image)
    {
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var s in spotlights)
        {
            double radius = s.Rounded ? Math.Min(16, Math.Min(s.Rect.Width, s.Rect.Height) / 4) : 0;
            holes.Children.Add(new RectangleGeometry(s.Rect, radius, radius));
        }
        double dim = spotlights.Max(s => s.Dim);
        return new Path
        {
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(new Rect(image)), holes),
            Fill = Brush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(dim, 0, 1)), 0, 0, 0)),
            IsHitTestVisible = false,
        };
    }

    private static FrameworkElement Magnifier(MagnifierAnnotation m, BitmapSource source)
    {
        var canvas = new Canvas();
        var stroke = Brush(m.Color);

        // Ligação entre as bordas das duas elipses, sem atravessar a lente.
        var sourceCenter = Center(m.Source);
        var targetCenter = Center(m.Target);
        var direction = targetCenter - sourceCenter;
        if (direction.Length > 1)
        {
            direction.Normalize();
            var start = sourceCenter + direction * EllipseRadiusAlong(m.Source, direction);
            var end = targetCenter - direction * EllipseRadiusAlong(m.Target, direction);
            canvas.Children.Add(new Line
            {
                X1 = start.X, Y1 = start.Y, X2 = end.X, Y2 = end.Y,
                Stroke = stroke, StrokeThickness = m.Thickness, StrokeDashArray = new DoubleCollection { 3, 2 },
            });
        }

        var origin = new Ellipse { Width = Math.Max(1, m.Source.Width), Height = Math.Max(1, m.Source.Height), Stroke = stroke, StrokeThickness = m.Thickness };
        Canvas.SetLeft(origin, m.Source.X);
        Canvas.SetTop(origin, m.Source.Y);
        canvas.Children.Add(origin);

        var lens = new Ellipse
        {
            Width = Math.Max(1, m.Target.Width),
            Height = Math.Max(1, m.Target.Height),
            Stroke = stroke,
            StrokeThickness = m.Thickness * 2,
            Fill = new ImageBrush(source) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = m.Source, Stretch = Stretch.Fill },
        };
        RenderOptions.SetBitmapScalingMode(lens, BitmapScalingMode.HighQuality);
        Canvas.SetLeft(lens, m.Target.X);
        Canvas.SetTop(lens, m.Target.Y);
        canvas.Children.Add(lens);
        return canvas;
    }

    private static FrameworkElement Callout(CalloutAnnotation c)
    {
        var canvas = new Canvas();
        canvas.Children.Add(new Path
        {
            Data = CalloutGeometry(c.Box, c.Tail),
            Fill = Brush(c.Color),
            Stroke = Brush(Darken(c.Color)),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        });
        double padding = Math.Max(8, c.FontSize * 0.45);
        var text = new TextBlock
        {
            Text = c.Text,
            Width = Math.Max(1, c.Box.Width - padding * 2),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Brush(Geometry2D.ContrastText(c.Color)),
            FontSize = c.FontSize,
            FontFamily = new FontFamily("Segoe UI"),
            FontWeight = FontWeights.SemiBold,
        };
        Canvas.SetLeft(text, c.Box.X + padding);
        Canvas.SetTop(text, c.Box.Y + padding * 0.7);
        canvas.Children.Add(text);
        return canvas;
    }

    /// <summary>Caixa arredondada unida a um triângulo cuja base fica no lado da caixa voltado para a ponta.</summary>
    public static Geometry CalloutGeometry(Rect box, Point tail)
    {
        double radius = Math.Min(12, Math.Min(box.Width, box.Height) / 3);
        var body = new RectangleGeometry(box, radius, radius);
        if (box.Contains(tail))
            return body;

        Point a, b;
        if (tail.Y > box.Bottom || tail.Y < box.Top)
        {
            double y = tail.Y > box.Bottom ? box.Bottom - 1 : box.Top + 1;
            double half = Math.Min(16, box.Width / 6);
            double x = Math.Clamp(tail.X, box.Left + radius + half, box.Right - radius - half);
            a = new Point(x - half, y);
            b = new Point(x + half, y);
        }
        else
        {
            double x = tail.X > box.Right ? box.Right - 1 : box.Left + 1;
            double half = Math.Min(16, box.Height / 6);
            double y = Math.Clamp(tail.Y, box.Top + radius + half, box.Bottom - radius - half);
            a = new Point(x, y - half);
            b = new Point(x, y + half);
        }

        var pointer = new StreamGeometry();
        using (var ctx = pointer.Open())
        {
            ctx.BeginFigure(a, true, true);
            ctx.LineTo(tail, true, true);
            ctx.LineTo(b, true, true);
        }
        return new CombinedGeometry(GeometryCombineMode.Union, body, pointer);
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

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    private static double EllipseRadiusAlong(Rect r, Vector unit)
    {
        double a = Math.Max(0.5, r.Width / 2), b = Math.Max(0.5, r.Height / 2);
        return 1 / Math.Sqrt(unit.X * unit.X / (a * a) + unit.Y * unit.Y / (b * b));
    }

    private static Color Darken(Color c) => Color.FromRgb((byte)(c.R * 0.75), (byte)(c.G * 0.75), (byte)(c.B * 0.75));

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
