using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Document;

public sealed class PdfPage
{
    private readonly PdfRect? _inheritedMediaBox;
    private readonly PdfRect? _inheritedCropBox;
    private readonly CosDictionary? _inheritedResources;
    private readonly int _inheritedRotation;

    internal PdfPage(
        CosDictionary dictionary,
        int pageNumber,
        PdfRect? inheritedMediaBox,
        PdfRect? inheritedCropBox,
        CosDictionary? inheritedResources,
        int inheritedRotation)
    {
        Dictionary = dictionary;
        PageNumber = pageNumber;
        _inheritedMediaBox = inheritedMediaBox;
        _inheritedCropBox = inheritedCropBox;
        _inheritedResources = inheritedResources;
        _inheritedRotation = inheritedRotation;
    }

    public CosDictionary Dictionary { get; }

    /// <summary>1-based page number.</summary>
    public int PageNumber { get; }

    /// <summary>The owning document; set when the page list is built.</summary>
    public PdfDocument? Document { get; internal set; }

    /// <summary>
    /// True when the box key is literally present in this page's dictionary — no inheritance,
    /// no fallback. This is the "is it actually there?" check preflight depends on.
    /// </summary>
    public bool HasBox(CosName boxName) => Dictionary.ContainsKey(boxName);

    /// <summary>
    /// The box as specified, without spec-defined fallbacks. MediaBox and CropBox honor page-tree
    /// inheritance; the other boxes are only read from the page dictionary itself.
    /// </summary>
    public PdfRect? GetBoxRaw(CosName boxName)
    {
        var own = PdfRect.FromCosArray(Dictionary.GetAsArray(boxName));
        if (own != null)
        {
            return own;
        }

        if (CosNames.MediaBox.Equals(boxName))
        {
            return _inheritedMediaBox;
        }

        if (CosNames.CropBox.Equals(boxName))
        {
            return _inheritedCropBox;
        }

        return null;
    }

    /// <summary>Resolved media box; falls back to US Letter when the file specifies none.</summary>
    public PdfRect MediaBox => GetBoxRaw(CosNames.MediaBox) ?? PdfRect.Letter;

    /// <summary>Resolved crop box; falls back to the media box per the PDF spec.</summary>
    public PdfRect CropBox => GetBoxRaw(CosNames.CropBox) ?? MediaBox;

    /// <summary>Resolved trim box; falls back to the crop box per the PDF spec.</summary>
    public PdfRect TrimBox => GetBoxRaw(CosNames.TrimBox) ?? CropBox;

    /// <summary>Resolved bleed box; falls back to the crop box per the PDF spec.</summary>
    public PdfRect BleedBox => GetBoxRaw(CosNames.BleedBox) ?? CropBox;

    /// <summary>Resolved art box; falls back to the crop box per the PDF spec.</summary>
    public PdfRect ArtBox => GetBoxRaw(CosNames.ArtBox) ?? CropBox;

    /// <summary>The page size (the resolved media box), ignoring rotation.</summary>
    public PdfRect Size => MediaBox;

    /// <summary>Page rotation in degrees, normalized to 0/90/180/270.</summary>
    public int Rotation
    {
        get
        {
            var rotation = Dictionary.GetAsInt(CosNames.Rotate) ?? _inheritedRotation;
            rotation %= 360;
            if (rotation < 0)
            {
                rotation += 360;
            }

            return rotation / 90 * 90;
        }
    }

    public CosDictionary? Resources =>
        Dictionary.GetAsDictionary(CosNames.Resources) ?? _inheritedResources;

    public void SetMediaBox(PdfRect box) => Dictionary.Put(CosNames.MediaBox, box.ToCosArray());

    public void SetCropBox(PdfRect box) => Dictionary.Put(CosNames.CropBox, box.ToCosArray());

    public void SetTrimBox(PdfRect box) => Dictionary.Put(CosNames.TrimBox, box.ToCosArray());

    public void SetBleedBox(PdfRect box) => Dictionary.Put(CosNames.BleedBox, box.ToCosArray());

    public void SetArtBox(PdfRect box) => Dictionary.Put(CosNames.ArtBox, box.ToCosArray());

    /// <summary>The page's decoded content, with multiple content streams joined by newlines.</summary>
    public byte[] GetContentBytes()
    {
        var streams = GetContentStreams();
        if (streams.Count == 0)
        {
            return Array.Empty<byte>();
        }

        if (streams.Count == 1)
        {
            return streams[0].GetDecodedBytes();
        }

        using var ms = new MemoryStream();
        foreach (var stream in streams)
        {
            var bytes = stream.GetDecodedBytes();
            ms.Write(bytes, 0, bytes.Length);
            ms.WriteByte((byte)'\n');
        }

        return ms.ToArray();
    }

    public IReadOnlyList<CosStream> GetContentStreams()
    {
        var contents = Dictionary.Get(CosNames.Contents);
        var streams = new List<CosStream>();
        if (contents is CosStream single)
        {
            streams.Add(single);
        }
        else if (contents is CosArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array.GetAsStream(i) is { } stream)
                {
                    streams.Add(stream);
                }
            }
        }

        return streams;
    }

    /// <summary>
    /// Creates a new empty content stream that renders before the page's existing content.
    /// Write raw operators into it with AppendString/AppendData.
    /// </summary>
    public CosStream AddContentStreamBefore() => SpliceContentStream(prepend: true);

    /// <summary>Creates a new empty content stream that renders after the page's existing content.</summary>
    public CosStream AddContentStreamAfter() => SpliceContentStream(prepend: false);

    private CosStream SpliceContentStream(bool prepend)
    {
        var document = Document
            ?? throw new InvalidOperationException("Page is not attached to a document.");

        var stream = new CosStream();
        var reference = document.AddObject(stream);

        var raw = Dictionary.GetRaw(CosNames.Contents);
        var resolved = raw?.Resolve();
        CosArray array;
        if (resolved is CosArray existing)
        {
            array = existing;
        }
        else
        {
            array = new CosArray();
            if (raw != null && resolved is CosStream)
            {
                array.Add(raw);
            }

            Dictionary.Put(CosNames.Contents, array);
        }

        if (prepend)
        {
            array.Insert(0, reference);
        }
        else
        {
            array.Add(reference);
        }

        return stream;
    }

    /// <summary>Appends an annotation to the page's /Annots array as a new indirect object.</summary>
    public void AddAnnotation(PdfTextAnnotation annotation) => AddAnnotation(annotation.Dictionary);

    public void AddAnnotation(CosDictionary annotation)
    {
        var document = Document
            ?? throw new InvalidOperationException("Page is not attached to a document.");

        var annots = Dictionary.Get(CosNames.Annots) as CosArray;
        if (annots == null)
        {
            annots = new CosArray();
            Dictionary.Put(CosNames.Annots, annots);
        }

        annots.Add(document.EnsureIndirect(annotation));
    }

    public IReadOnlyList<CosDictionary> GetAnnotations()
    {
        var annots = Dictionary.GetAsArray(CosNames.Annots);
        if (annots == null)
        {
            return Array.Empty<CosDictionary>();
        }

        var result = new List<CosDictionary>(annots.Count);
        for (var i = 0; i < annots.Count; i++)
        {
            if (annots.GetAsDictionary(i) is { } annotation)
            {
                result.Add(annotation);
            }
        }

        return result;
    }
}
