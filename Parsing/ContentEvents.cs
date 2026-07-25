using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Parsing;

/// <summary>
/// Receives render events while a content stream is replayed. Implement only the methods you
/// need; the defaults ignore the event.
/// </summary>
public interface IContentListener
{
    void OnImage(ImageRenderEvent e)
    {
    }

    void OnText(TextRenderEvent e)
    {
    }

    void OnPath(PathRenderEvent e)
    {
    }
}

/// <summary>A marked-content scope (BMC/BDC) an event occurred inside.</summary>
public sealed class MarkedContentTag
{
    public required CosName Role { get; init; }

    /// <summary>The BDC properties: a dictionary, or the resolved /Properties resource entry.</summary>
    public CosObject? Properties { get; init; }
}

public sealed class ImageRenderEvent
{
    public required PdfImageXObject Image { get; init; }

    /// <summary>The CTM at placement; maps the image's unit square to page space.</summary>
    public required PdfMatrix Ctm { get; init; }

    /// <summary>Enclosing marked-content scopes, most recent first.</summary>
    public required IReadOnlyList<MarkedContentTag> Tags { get; init; }

    /// <summary>The image's object number when it is an indirect object; null for inline images.</summary>
    public CosIndirectReference? ImageReference => Image.Stream.IndirectReference;

    public float PlacedWidthPoints => Ctm.ScaleMagnitudeX;

    public float PlacedHeightPoints => Ctm.ScaleMagnitudeY;

    public float DpiX => PlacedWidthPoints > 0.01f ? Image.Width / (PlacedWidthPoints / 72f) : 0f;

    public float DpiY => PlacedHeightPoints > 0.01f ? Image.Height / (PlacedHeightPoints / 72f) : 0f;
}

public sealed class TextRenderEvent
{
    /// <summary>
    /// Best-effort decoded text: Latin-1 for simple fonts, big-endian code pairs for Type0.
    /// Reliable for "is there visible text" checks, not for extraction.
    /// </summary>
    public required string Text { get; init; }

    /// <summary>
    /// The text matrix (Tm chain) alone, without the CTM — matching iText's
    /// TextRenderInfo.GetTextMatrix semantics that existing preflight logic was written against.
    /// </summary>
    public required PdfMatrix TextMatrix { get; init; }

    /// <summary>The text matrix composed with the CTM: true text-space → page-space transform.</summary>
    public required PdfMatrix TextToPageMatrix { get; init; }

    public required float FontSize { get; init; }

    /// <summary>Tr render mode; 3 = invisible (typically OCR layers).</summary>
    public required int RenderMode { get; init; }

    public CosDictionary? Font { get; init; }

    public required IReadOnlyList<MarkedContentTag> Tags { get; init; }
}

[Flags]
public enum PathPaintOperation
{
    NoOp = 0,
    Stroke = 1,
    Fill = 2
}

public sealed class PathRenderEvent
{
    public required PdfPath Path { get; init; }

    public required PathPaintOperation Operation { get; init; }

    /// <summary>The current line width in user space (before CTM scaling).</summary>
    public required float LineWidth { get; init; }

    public required PdfMatrix Ctm { get; init; }

    public required IReadOnlyList<MarkedContentTag> Tags { get; init; }
}

public readonly record struct PdfPoint(float X, float Y);

public sealed class PdfPath
{
    public PdfPath(IReadOnlyList<PdfSubpath> subpaths)
    {
        Subpaths = subpaths;
    }

    public IReadOnlyList<PdfSubpath> Subpaths { get; }
}

public sealed class PdfSubpath
{
    private const int BezierSegments = 12;

    internal readonly record struct Segment(bool IsCurve, PdfPoint Control1, PdfPoint Control2, PdfPoint End);

    private readonly List<Segment> _segments = new();

    internal PdfSubpath(PdfPoint start)
    {
        Start = start;
    }

    public PdfPoint Start { get; }

    public bool IsClosed { get; internal set; }

    internal PdfPoint CurrentPoint => _segments.Count > 0 ? _segments[^1].End : Start;

    internal void AddLine(PdfPoint end) => _segments.Add(new Segment(false, default, default, end));

    internal void AddCurve(PdfPoint control1, PdfPoint control2, PdfPoint end) =>
        _segments.Add(new Segment(true, control1, control2, end));

    /// <summary>The subpath as a polyline, with Bézier segments flattened.</summary>
    public IReadOnlyList<PdfPoint> GetPiecewiseLinearApproximation()
    {
        var points = new List<PdfPoint> { Start };
        var current = Start;
        foreach (var segment in _segments)
        {
            if (!segment.IsCurve)
            {
                points.Add(segment.End);
            }
            else
            {
                for (var i = 1; i <= BezierSegments; i++)
                {
                    var t = i / (float)BezierSegments;
                    var u = 1f - t;
                    var x = u * u * u * current.X
                            + 3f * u * u * t * segment.Control1.X
                            + 3f * u * t * t * segment.Control2.X
                            + t * t * t * segment.End.X;
                    var y = u * u * u * current.Y
                            + 3f * u * u * t * segment.Control1.Y
                            + 3f * u * t * t * segment.Control2.Y
                            + t * t * t * segment.End.Y;
                    points.Add(new PdfPoint(x, y));
                }
            }

            current = segment.End;
        }

        return points;
    }
}
