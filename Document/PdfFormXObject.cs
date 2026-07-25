using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>A form XObject: reusable content wrapped with a bounding box and optional resources.</summary>
public sealed class PdfFormXObject
{
    /// <summary>
    /// Creates a form from raw content-stream bytes. Pass resources as an indirect reference
    /// (via PdfDocument.EnsureIndirect) when the dictionary is shared with a page.
    /// </summary>
    public PdfFormXObject(PdfRect bbox, byte[] contentBytes, CosObject? resources = null)
    {
        Stream = new CosStream();
        Stream.Put(CosNames.Type, CosNames.XObject);
        Stream.Put(CosNames.Subtype, CosNames.Form);
        Stream.Put(CosNames.BBox, bbox.ToCosArray());
        if (resources != null)
        {
            Stream.Put(CosNames.Resources, resources);
        }

        Stream.SetData(contentBytes);
    }

    /// <summary>Wraps an existing form XObject stream.</summary>
    public PdfFormXObject(CosStream stream)
    {
        Stream = stream;
    }

    public CosStream Stream { get; }

    public PdfRect? BBox => PdfRect.FromCosArray(Stream.GetAsArray(CosNames.BBox));
}
