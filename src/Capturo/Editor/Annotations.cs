using System.Collections.Immutable;
using System.Windows;
using System.Windows.Media;

namespace Capturo.Editor;

/// <summary>
/// Anotação imutável, em coordenadas de pixel da imagem. O editor guarda uma lista destas e redesenha a partir dela,
/// o que torna desfazer/refazer trivial e a exportação idêntica ao que aparece na tela.
/// </summary>
public abstract record Annotation
{
    public abstract Rect Bounds { get; }
    public abstract Annotation Offset(Vector delta);
    public virtual Annotation WithColor(Color color) => this;
    public virtual Annotation WithSize(double size) => this;

    protected static Rect Inflate(Rect rect, double amount)
    {
        rect.Inflate(amount, amount);
        return rect;
    }
}

public sealed record ArrowAnnotation(Point Start, Point End, Color Color, double Thickness) : Annotation
{
    public override Rect Bounds => Inflate(new Rect(Start, End), Thickness * 3);
    public override Annotation Offset(Vector delta) => this with { Start = Start + delta, End = End + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record LineAnnotation(Point Start, Point End, Color Color, double Thickness) : Annotation
{
    public override Rect Bounds => Inflate(new Rect(Start, End), Thickness + 4);
    public override Annotation Offset(Vector delta) => this with { Start = Start + delta, End = End + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record RectangleAnnotation(Rect Rect, Color Color, double Thickness) : Annotation
{
    public override Rect Bounds => Inflate(Rect, Thickness);
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record EllipseAnnotation(Rect Rect, Color Color, double Thickness) : Annotation
{
    public override Rect Bounds => Inflate(Rect, Thickness);
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

/// <summary>Traço livre; com <see cref="Highlighter"/> vira marca-texto translúcido e largo.</summary>
public sealed record PenAnnotation(ImmutableArray<Point> Points, Color Color, double Thickness, bool Highlighter) : Annotation
{
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

    public override Annotation Offset(Vector delta) =>
        this with { Points = Points.Select(p => p + delta).ToImmutableArray() };

    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Thickness = size };
}

public sealed record TextAnnotation(Point Origin, string Text, Color Color, double FontSize) : Annotation
{
    public override Rect Bounds
    {
        get
        {
            var lines = Text.Split('\n');
            double width = lines.Max(l => l.Length) * FontSize * 0.6 + 8;
            double height = lines.Length * FontSize * 1.35 + 4;
            return new Rect(Origin.X - 2, Origin.Y - 2, width, height);
        }
    }

    public override Annotation Offset(Vector delta) => this with { Origin = Origin + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { FontSize = SizeMapping.FontSize(size) };
}

/// <summary>Marcador numerado (1, 2, 3...) para indicar a ordem de passos num tutorial.</summary>
public sealed record StepAnnotation(Point Center, int Number, Color Color, double Diameter) : Annotation
{
    public override Rect Bounds => new(Center.X - Diameter / 2, Center.Y - Diameter / 2, Diameter, Diameter);
    public override Annotation Offset(Vector delta) => this with { Center = Center + delta };
    public override Annotation WithColor(Color color) => this with { Color = color };
    public override Annotation WithSize(double size) => this with { Diameter = SizeMapping.StepDiameter(size) };
}

/// <summary>Pixeliza a área para esconder dados sensíveis.</summary>
public sealed record PixelateAnnotation(Rect Rect) : Annotation
{
    public override Rect Bounds => Rect;
    public override Annotation Offset(Vector delta) => this with { Rect = Rect.Offset(Rect, delta) };
}

/// <summary>Converte o controle único de "espessura" (1 a 12) no tamanho de cada tipo de anotação.</summary>
public static class SizeMapping
{
    public static double FontSize(double size) => 10 + size * 3;
    public static double StepDiameter(double size) => 18 + size * 3;
}
