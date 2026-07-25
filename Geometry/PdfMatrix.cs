namespace FrontEndSuite.PdfPlatform.Geometry;

/// <summary>A PDF transformation matrix [a b c d e f] (the six configurable entries of a 3x3 matrix).</summary>
public readonly struct PdfMatrix
{
    public static readonly PdfMatrix Identity = new(1, 0, 0, 1, 0, 0);

    public PdfMatrix(float a, float b, float c, float d, float e, float f)
    {
        A = a;
        B = b;
        C = c;
        D = d;
        E = e;
        F = f;
    }

    public float A { get; }
    public float B { get; }
    public float C { get; }
    public float D { get; }
    public float E { get; }
    public float F { get; }

    /// <summary>Returns this × other (apply this transform first, then other).</summary>
    public PdfMatrix Multiply(PdfMatrix m) => new(
        A * m.A + B * m.C,
        A * m.B + B * m.D,
        C * m.A + D * m.C,
        C * m.B + D * m.D,
        E * m.A + F * m.C + m.E,
        E * m.B + F * m.D + m.F);

    public (float X, float Y) Transform(float x, float y) =>
        (A * x + C * y + E, B * x + D * y + F);

    /// <summary>Magnitude of the transformed x unit vector — the placed width of a 1-unit object.</summary>
    public float ScaleMagnitudeX => MathF.Sqrt(A * A + B * B);

    /// <summary>Magnitude of the transformed y unit vector — the placed height of a 1-unit object.</summary>
    public float ScaleMagnitudeY => MathF.Sqrt(C * C + D * D);

    public static PdfMatrix Translate(float dx, float dy) => new(1, 0, 0, 1, dx, dy);

    public static PdfMatrix Scale(float sx, float sy) => new(sx, 0, 0, sy, 0, 0);

    public override string ToString() => $"[{A:0.###} {B:0.###} {C:0.###} {D:0.###} {E:0.###} {F:0.###}]";
}
