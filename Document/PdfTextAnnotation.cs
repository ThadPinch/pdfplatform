using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>A text ("sticky note") annotation. Attach to a page with PdfPage.AddAnnotation.</summary>
public sealed class PdfTextAnnotation
{
    public PdfTextAnnotation(PdfRect rect)
    {
        Dictionary = new CosDictionary();
        Dictionary.Put(CosNames.Type, CosNames.Annot);
        Dictionary.Put(CosNames.Subtype, CosNames.Text);
        Dictionary.Put(CosNames.Rect, rect.ToCosArray());
    }

    public CosDictionary Dictionary { get; }

    public PdfTextAnnotation SetContents(string text)
    {
        Dictionary.Put(CosNames.Contents, new CosString(text));
        return this;
    }

    /// <summary>Sets the annotation color as RGB components in 0..1.</summary>
    public PdfTextAnnotation SetColorRgb(float r, float g, float b)
    {
        Dictionary.Put(CosNames.C, new CosArray(new CosObject[]
        {
            new CosNumber((double)r), new CosNumber((double)g), new CosNumber((double)b)
        }));
        return this;
    }

    public PdfTextAnnotation SetAuthor(string author)
    {
        Dictionary.Put(CosNames.T, new CosString(author));
        return this;
    }

    public PdfTextAnnotation SetSubject(string subject)
    {
        Dictionary.Put(CosNames.Subj, new CosString(subject));
        return this;
    }

    /// <summary>Sets the note icon (e.g. CosNames.Note).</summary>
    public PdfTextAnnotation SetIcon(CosName icon)
    {
        Dictionary.Put(CosNames.Name, icon);
        return this;
    }

    public PdfTextAnnotation SetOpen(bool open)
    {
        Dictionary.Put(CosNames.Open, CosBoolean.Of(open));
        return this;
    }
}
