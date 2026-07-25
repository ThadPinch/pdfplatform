using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.Geometry;

/// <summary>An axis-aligned rectangle in PDF user space (points, origin bottom-left).</summary>
public readonly struct PdfRect : IEquatable<PdfRect>
{
    public static readonly PdfRect Letter = new(0, 0, 612, 792);

    public PdfRect(float x, float y, float width, float height)
    {
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public float X { get; }
    public float Y { get; }
    public float Width { get; }
    public float Height { get; }

    public float Left => X;
    public float Bottom => Y;
    public float Right => X + Width;
    public float Top => Y + Height;

    /// <summary>Builds a rectangle from two corner points in any order.</summary>
    public static PdfRect FromCorners(float x1, float y1, float x2, float y2)
    {
        var x = Math.Min(x1, x2);
        var y = Math.Min(y1, y2);
        return new PdfRect(x, y, Math.Max(x1, x2) - x, Math.Max(y1, y2) - y);
    }

    /// <summary>Reads a PDF box array [llx lly urx ury], normalizing swapped corners. Null when malformed.</summary>
    public static PdfRect? FromCosArray(CosArray? array)
    {
        if (array == null || array.Count < 4)
        {
            return null;
        }

        var x1 = array.GetAsFloat(0);
        var y1 = array.GetAsFloat(1);
        var x2 = array.GetAsFloat(2);
        var y2 = array.GetAsFloat(3);
        if (x1 == null || y1 == null || x2 == null || y2 == null)
        {
            return null;
        }

        return FromCorners(x1.Value, y1.Value, x2.Value, y2.Value);
    }

    public CosArray ToCosArray() => new(new CosObject[]
    {
        new CosNumber((double)X),
        new CosNumber((double)Y),
        new CosNumber((double)Right),
        new CosNumber((double)Top)
    });

    public bool Contains(float x, float y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public bool Equals(PdfRect other) =>
        X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;

    public override bool Equals(object? obj) => obj is PdfRect other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, Width, Height);

    public static bool operator ==(PdfRect left, PdfRect right) => left.Equals(right);

    public static bool operator !=(PdfRect left, PdfRect right) => !left.Equals(right);

    public override string ToString() => $"[{X:0.##} {Y:0.##} {Right:0.##} {Top:0.##}]";
}
