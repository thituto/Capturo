using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;

namespace Capturo.Editor;

/// <summary>Estilo das formas: preenchida ou só contorno, opacidade (0 a 1) e cantos arredondados.</summary>
public readonly record struct ShapeStyle(bool Filled, double Opacity, bool Rounded)
{
    public static readonly ShapeStyle Default = new(false, 1, false);
}

/// <summary>
/// Anotação imutável, em coordenadas de pixel da imagem. O editor guarda uma lista destas e redesenha a partir dela,
/// o que torna desfazer/refazer trivial e a exportação idêntica ao que aparece na tela.
/// </summary>
public abstract record Annotation
{
    public abstract Rect Bounds { get; }

    /// <summary>Se um clique neste ponto pega a anotação.</summary>
    public virtual bool HitTest(Point p) => Bounds.Contains(p);

    public abstract Annotation Offset(Vector delta);

    /// <summary>Mover arrastando a partir de <paramref name="grab"/>; a lupa usa isso para mover só a parte agarrada.</summary>
    public virtual Annotation OffsetAt(Point grab, Vector delta) => Offset(delta);

    /// <summary>Alças de edição, em coordenadas da imagem.</summary>
    public virtual IReadOnlyList<Point> Handles => Array.Empty<Point>();

    /// <summary>Nova versão com a alça <paramref name="index"/> levada até <paramref name="position"/>.</summary>
    public virtual Annotation MoveHandle(int index, Point position) => this;

    public virtual Annotation WithColor(Color color) => this;
    public virtual Annotation WithSize(double size) => this;
    public virtual Annotation WithStyle(ShapeStyle style) => this;

    /// <summary>Acompanha um redimensionamento da imagem: posições e tamanhos escalam junto.</summary>
    public abstract Annotation Scale(double sx, double sy);

    protected static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }

    protected static Point S(Point p, double sx, double sy) => new(p.X * sx, p.Y * sy);
    protected static Rect S(Rect r, double sx, double sy) => new(r.X * sx, r.Y * sy, r.Width * sx, r.Height * sy);
    protected static double S(double length, double sx, double sy) => length * (sx + sy) / 2;
}

/// <summary>As 8 alças de um retângulo: cantos e meios de lado, em sentido horário a partir do canto superior esquerdo.</summary>
public static class RectHandles
{
    public static IReadOnlyList<Point> Of(Rect r) => new[]
    {
        r.TopLeft, new Point(r.X + r.Width / 2, r.Y), r.TopRight, new Point(r.Right, r.Y + r.Height / 2),
        r.BottomRight, new Point(r.X + r.Width / 2, r.Bottom), r.BottomLeft, new Point(r.X, r.Y + r.Height / 2),
    };

    public static Rect Move(Rect r, int index, Point p)
    {
        double left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        switch (index)
        {
            case 0: left = p.X; top = p.Y; break;
            case 1: top = p.Y; break;
            case 2: right = p.X; top = p.Y; break;
            case 3: right = p.X; break;
            case 4: right = p.X; bottom = p.Y; break;
            case 5: bottom = p.Y; break;
            case 6: left = p.X; bottom = p.Y; break;
            case 7: left = p.X; break;
        }
        return new Rect(new Point(left, top), new Point(right, bottom));
    }
}

public sealed record ArrowAnnotation(Point Start, Point End, Color Color, double Thickness) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Start = S(Start, sx, sy), End = S(End, sx, sy), Thickness = S(Thickness, sx, sy) };
    public override Rect Bounds => Inflate(new Rect(Start, End), Thickness * 3);
    public override bool HitTest(Point p) => Geometry2D.DistanceToSegment(p, Start, End) <= Thickness * 2 + 4;
    public override Annotation Offset(Vector delta) => this with { Start = Start + delta, End = End + delta };
    public override IReadOnlyList<Point> Handles => new[] { Start, End };
    public override Annotation MoveHandle(int index, Point position) =>
        index == 0 ? this with { Start = position } : this with { End = position };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record LineAnnotation(Point Start, Point End, Color Color, double Thickness) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Start = S(Start, sx, sy), End = S(End, sx, sy), Thickness = S(Thickness, sx, sy) };
    public override Rect Bounds => Inflate(new Rect(Start, End), Thickness + 4);
    public override bool HitTest(Point p) => Geometry2D.DistanceToSegment(p, Start, End) <= Thickness + 5;
    public override Annotation Offset(Vector delta) => this with { Start = Start + delta, End = End + delta };
    public override IReadOnlyList<Point> Handles => new[] { Start, End };
    public override Annotation MoveHandle(int index, Point position) =>
        index == 0 ? this with { Start = position } : this with { End = position };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record RectangleAnnotation(Rect Rect, Color Color, double Thickness, ShapeStyle Style) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Rect = S(Rect, sx, sy), Thickness = S(Thickness, sx, sy) };
    public override Rect Bounds => Inflate(Rect, Thickness);
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Rect);
    public override Annotation MoveHandle(int index, Point position) => this with { Rect = RectHandles.Move(Rect, index, position) };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
    public override Annotation WithStyle(ShapeStyle style) => this with { Style = style };
}

public sealed record EllipseAnnotation(Rect Rect, Color Color, double Thickness, ShapeStyle Style) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Rect = S(Rect, sx, sy), Thickness = S(Thickness, sx, sy) };
    public override Rect Bounds => Inflate(Rect, Thickness);
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Rect);
    public override Annotation MoveHandle(int index, Point position) => this with { Rect = RectHandles.Move(Rect, index, position) };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
    public override Annotation WithStyle(ShapeStyle style) => this with { Style = style with { Rounded = false } };
}

/// <summary>Traço livre; com <see cref="Highlighter"/> vira marca-texto translúcido e largo.</summary>
public sealed record PenAnnotation(ImmutableArray<Point> Points, Color Color, double Thickness, bool Highlighter) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Points = Points.Select(p => S(p, sx, sy)).ToImmutableArray(), Thickness = S(Thickness, sx, sy) };
    public double StrokeWidth => Highlighter ? Thickness * 4 : Thickness;

    public override Rect Bounds
    {
        get
        {
            if (Points.IsDefaultOrEmpty)
                return Rect.Empty;
            var rect = new Rect(Points[0], Points[0]);
            foreach (var p in Points)
                rect.Union(p);
            return Inflate(rect, StrokeWidth / 2 + 2);
        }
    }

    public override bool HitTest(Point p)
    {
        if (Points.Length < 2)
            return Bounds.Contains(p);
        for (int i = 1; i < Points.Length; i++)
        {
            if (Geometry2D.DistanceToSegment(p, Points[i - 1], Points[i]) <= StrokeWidth / 2 + 4)
                return true;
        }
        return false;
    }

    public override Annotation Offset(Vector delta) =>
        this with { Points = Points.Select(p => p + delta).ToImmutableArray() };

    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

/// <summary>Texto solto; <see cref="Filled"/> desenha um fundo na cor escolhida, com o texto em contraste.</summary>
public sealed record TextAnnotation(Point Origin, string Text, Color Color, double FontSize, bool Filled) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Origin = S(Origin, sx, sy), FontSize = S(FontSize, sx, sy) };
    public override Rect Bounds
    {
        get
        {
            var lines = Text.Split('\n');
            double padding = Filled ? FontSize * 0.35 : 0;
            double width = lines.Max(l => l.Length) * FontSize * 0.6 + 8 + padding * 2;
            double height = lines.Length * FontSize * 1.35 + 4 + padding * 2;
            return new Rect(Origin.X - 2, Origin.Y - 2, width, height);
        }
    }

    public override Annotation Offset(Vector delta) => this with { Origin = Origin + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { FontSize = SizeMapping.FontSize(size) };
    public override Annotation WithStyle(ShapeStyle style) => this with { Filled = style.Filled };
}

/// <summary>Marcador numerado (1, 2, 3...) para indicar a ordem de passos num tutorial.</summary>
public sealed record StepAnnotation(Point Center, int Number, Color Color, double Diameter) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Center = S(Center, sx, sy), Diameter = S(Diameter, sx, sy) };
    public override Rect Bounds => new(Center.X - Diameter / 2, Center.Y - Diameter / 2, Diameter, Diameter);
    public override Annotation Offset(Vector delta) => this with { Center = Center + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Diameter = SizeMapping.StepDiameter(size) };
}

/// <summary>Pixeliza a área para esconder dados sensíveis.</summary>
public sealed record PixelateAnnotation(Rect Rect) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Rect = S(Rect, sx, sy) };
    public override Rect Bounds => Rect;
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Rect);
    public override Annotation MoveHandle(int index, Point position) => this with { Rect = RectHandles.Move(Rect, index, position) };
}

/// <summary>Destaque: escurece toda a imagem menos esta área. Vários destaques abrem várias "janelas" na mesma máscara.</summary>
public sealed record SpotlightAnnotation(Rect Rect, double Dim, bool Rounded) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Rect = S(Rect, sx, sy) };
    public override Rect Bounds => Rect;
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Rect);
    public override Annotation MoveHandle(int index, Point position) => this with { Rect = RectHandles.Move(Rect, index, position) };
    public override Annotation WithStyle(ShapeStyle style) => this with { Dim = Math.Clamp(style.Opacity, 0.1, 0.95), Rounded = style.Rounded };
}

/// <summary>Lupa: mostra a área <see cref="Source"/> ampliada dentro de <see cref="Target"/>.</summary>
public sealed record MagnifierAnnotation(Rect Source, Rect Target, Color Color, double Thickness) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Source = S(Source, sx, sy), Target = S(Target, sx, sy), Thickness = S(Thickness, sx, sy) };
    public override Rect Bounds => Rect.Union(Inflate(Source, Thickness), Inflate(Target, Thickness));
    public override bool HitTest(Point p) => Source.Contains(p) || Target.Contains(p);
    public override Annotation Offset(Vector delta) =>
        this with { Source = Rect.Offset(Source, delta), Target = Rect.Offset(Target, delta) };

    public override Annotation OffsetAt(Point grab, Vector delta) =>
        Target.Contains(grab) ? this with { Target = Rect.Offset(Target, delta) }
        : Source.Contains(grab) ? this with { Source = Rect.Offset(Source, delta) }
        : Offset(delta);

    /// <summary>Alças 0–7 redimensionam a lente; 8–15, a área de origem.</summary>
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Target).Concat(RectHandles.Of(Source)).ToArray();

    public override Annotation MoveHandle(int index, Point position) => index < 8
        ? this with { Target = RectHandles.Move(Target, index, position) }
        : this with { Source = RectHandles.Move(Source, index - 8, position) };

    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = Math.Max(1, size / 2) };

    /// <summary>Cria a lente 2× ao lado da área de origem, do lado que couber na imagem.</summary>
    public static MagnifierAnnotation Create(Rect source, Size image, Color color, double thickness)
    {
        double width = Math.Min(source.Width * 2, image.Width * 0.45);
        double height = width * source.Height / Math.Max(1, source.Width);
        double gap = 24;
        double x = source.Right + gap + width <= image.Width ? source.Right + gap : Math.Max(0, source.Left - gap - width);
        double y = Math.Clamp(source.Top + source.Height / 2 - height / 2, 0, Math.Max(0, image.Height - height));
        return new MagnifierAnnotation(source, new Rect(x, y, width, height), color, Math.Max(1, thickness / 2));
    }
}

/// <summary>Balão de texto com ponta (callout).</summary>
public sealed record CalloutAnnotation(Rect Box, Point Tail, string Text, Color Color, double FontSize) : Annotation
{
    public override Annotation Scale(double sx, double sy) => this with { Box = S(Box, sx, sy), Tail = S(Tail, sx, sy), FontSize = S(FontSize, sx, sy) };
    public override Rect Bounds
    {
        get
        {
            var rect = Box;
            rect.Union(Tail);
            return rect;
        }
    }

    public override bool HitTest(Point p) => Box.Contains(p) || Geometry2D.DistanceToSegment(p, Box.BottomLeft + (Box.BottomRight - Box.BottomLeft) / 3, Tail) < 10;
    public override Annotation Offset(Vector delta) => this with { Box = Rect.Offset(Box, delta), Tail = Tail + delta };
    public override Annotation OffsetAt(Point grab, Vector delta) => Offset(delta);

    /// <summary>Alças 0–7 redimensionam a caixa; a 8 move a ponta.</summary>
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Box).Append(Tail).ToArray();

    public override Annotation MoveHandle(int index, Point position) => index < 8
        ? this with { Box = RectHandles.Move(Box, index, position) }
        : this with { Tail = position };

    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { FontSize = SizeMapping.FontSize(size) };

    public static CalloutAnnotation Create(Rect box, Size image, Color color, double fontSize)
    {
        var tail = new Point(box.Left + box.Width * 0.25, box.Bottom + Math.Max(28, box.Height * 0.6));
        if (tail.Y > image.Height)
            tail = new Point(tail.X, Math.Max(0, box.Top - Math.Max(28, box.Height * 0.6)));
        return new CalloutAnnotation(box, tail, "", color, fontSize);
    }
}

/// <summary>Chave "{" ligando dois pontos, com a ponta do meio voltada para um lado (agrupa itens de uma lista).</summary>
public sealed record BraceAnnotation(Point Start, Point End, Color Color, double Thickness) : Annotation
{
    /// <summary>Quanto a chave se afasta da linha entre as pontas.</summary>
    public double Depth => Math.Max(10, (End - Start).Length * 0.08);

    public override Rect Bounds => Inflate(new Rect(Start, End), Depth + Thickness + 2);
    public override bool HitTest(Point p) => Geometry2D.DistanceToSegment(p, Start, End) <= Depth + Thickness + 4;
    public override Annotation Offset(Vector delta) => this with { Start = Start + delta, End = End + delta };
    public override IReadOnlyList<Point> Handles => new[] { Start, End };
    public override Annotation MoveHandle(int index, Point position) =>
        index == 0 ? this with { Start = position } : this with { End = position };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
    public override Annotation Scale(double sx, double sy) => this with { Start = S(Start, sx, sy), End = S(End, sx, sy), Thickness = S(Thickness, sx, sy) };
}

/// <summary>Carimbo: um símbolo pronto (certo, errado, alerta, estrela...) num tamanho escolhido.</summary>
public sealed record StampAnnotation(Point Center, string Glyph, Color Color, double Size) : Annotation
{
    public static readonly string[] Glyphs = { "✔", "✖", "!", "?", "★", "♥", "➜", "☺" };

    public override Rect Bounds => new(Center.X - Size / 2, Center.Y - Size / 2, Size, Size);
    public override Annotation Offset(Vector delta) => this with { Center = Center + delta };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Bounds);

    /// <summary>Redimensionar pelo canto mantém o carimbo quadrado e centrado.</summary>
    public override Annotation MoveHandle(int index, Point position)
    {
        var rect = RectHandles.Move(Bounds, index, position);
        double size = Math.Max(12, Math.Max(rect.Width, rect.Height));
        return this with { Center = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), Size = size };
    }

    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Size = SizeMapping.StampSize(size) };
    public override Annotation Scale(double sx, double sy) => this with { Center = S(Center, sx, sy), Size = S(Size, sx, sy) };
}

/// <summary>Outra imagem colada por cima da captura (logotipo, ícone, recorte de outra tela).</summary>
public sealed record ImageAnnotation(Rect Rect, System.Windows.Media.Imaging.BitmapSource Picture) : Annotation
{
    public override Rect Bounds => Rect;
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override IReadOnlyList<Point> Handles => RectHandles.Of(Rect);
    public override Annotation MoveHandle(int index, Point position) => this with { Rect = RectHandles.Move(Rect, index, position) };
    public override Annotation Scale(double sx, double sy) => this with { Rect = S(Rect, sx, sy) };

    /// <summary>Encaixa a imagem com no máximo metade do tamanho da captura, centrada em <paramref name="center"/>.</summary>
    public static ImageAnnotation Create(System.Windows.Media.Imaging.BitmapSource picture, Point center, Size canvas)
    {
        double scale = Math.Min(1, Math.Min(canvas.Width / 2 / picture.PixelWidth, canvas.Height / 2 / picture.PixelHeight));
        double width = picture.PixelWidth * scale, height = picture.PixelHeight * scale;
        return new ImageAnnotation(new Rect(center.X - width / 2, center.Y - height / 2, width, height), picture);
    }
}

/// <summary>Converte o controle único de "espessura" (1 a 12) no tamanho de cada tipo de anotação.</summary>
public static class SizeMapping
{
    public static double FontSize(double size) => 10 + size * 3;
    public static double StepDiameter(double size) => 18 + size * 3;
    public static double StampSize(double size) => 20 + size * 6;
}

public static class Geometry2D
{
    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9)
            return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    /// <summary>Texto legível sobre a cor de fundo dada.</summary>
    public static Color ContrastText(Color background) =>
        0.299 * background.R + 0.587 * background.G + 0.114 * background.B > 150 ? Color.FromRgb(0x11, 0x18, 0x27) : Colors.White;
}
