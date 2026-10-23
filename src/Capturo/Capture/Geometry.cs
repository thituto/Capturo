namespace Capturo.Capture;

/// <summary>Ponto em pixels físicos, no espaço da tela virtual (todos os monitores).</summary>
public readonly record struct PxPoint(int X, int Y);

/// <summary>Retângulo em pixels físicos, no espaço da tela virtual.</summary>
public readonly record struct PxRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(PxPoint p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public bool IntersectsWith(PxRect other) => !Intersect(other).IsEmpty;

    /// <summary>Retângulo entre dois cantos, incluindo os dois pixels das pontas.</summary>
    public static PxRect FromCorners(PxPoint a, PxPoint b)
    {
        int left = Math.Min(a.X, b.X);
        int top = Math.Min(a.Y, b.Y);
        return new PxRect(left, top, Math.Abs(a.X - b.X) + 1, Math.Abs(a.Y - b.Y) + 1);
    }

    public static PxRect FromEdges(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    public PxRect Intersect(PxRect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : default;
    }

    public PxRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public override string ToString() => $"{Width} × {Height}";
}
