using System.Globalization;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Canvas;

/// <summary>
/// Writes content-stream operators into a target stream, registering XObjects into the supplied
/// resources dictionary. Operators go straight through in call order, so raw writes to the same
/// stream (AppendString) interleave predictably — including deliberately unbalanced q/Q, which
/// this canvas permits by design.
/// </summary>
public sealed class PdfCanvas
{
    private readonly CosStream _stream;
    private readonly CosDictionary _resources;
    private readonly PdfDocument _document;

    public PdfCanvas(CosStream stream, CosDictionary resources, PdfDocument document)
    {
        _stream = stream;
        _resources = resources;
        _document = document;
    }

    public CosStream Stream => _stream;

    public Document.PdfDocument Document => _document;

    public PdfCanvas SaveState() => Op("q");

    public PdfCanvas RestoreState() => Op("Q");

    public PdfCanvas ConcatMatrix(double a, double b, double c, double d, double e, double f) =>
        Op($"{F(a)} {F(b)} {F(c)} {F(d)} {F(e)} {F(f)} cm");

    public PdfCanvas ConcatMatrix(PdfMatrix matrix) =>
        ConcatMatrix(matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);

    public PdfCanvas Rectangle(double x, double y, double width, double height) =>
        Op($"{F(x)} {F(y)} {F(width)} {F(height)} re");

    public PdfCanvas Rectangle(PdfRect rect) => Rectangle(rect.X, rect.Y, rect.Width, rect.Height);

    public PdfCanvas MoveTo(double x, double y) => Op($"{F(x)} {F(y)} m");

    public PdfCanvas LineTo(double x, double y) => Op($"{F(x)} {F(y)} l");

    public PdfCanvas Stroke() => Op("S");

    public PdfCanvas ClosePathStroke() => Op("s");

    public PdfCanvas Fill() => Op("f");

    public PdfCanvas FillEvenOdd() => Op("f*");

    public PdfCanvas Clip() => Op("W");

    public PdfCanvas EoClip() => Op("W*");

    public PdfCanvas EndPath() => Op("n");

    public PdfCanvas SetLineWidth(double width) => Op($"{F(width)} w");

    public PdfCanvas SetFillGray(double gray) => Op($"{F(gray)} g");

    public PdfCanvas SetFillRgb(double r, double g, double b) => Op($"{F(r)} {F(g)} {F(b)} rg");

    public PdfCanvas SetFillCmyk(double c, double m, double y, double k) => Op($"{F(c)} {F(m)} {F(y)} {F(k)} k");

    public PdfCanvas SetStrokeGray(double gray) => Op($"{F(gray)} G");

    public PdfCanvas SetStrokeRgb(double r, double g, double b) => Op($"{F(r)} {F(g)} {F(b)} RG");

    public PdfCanvas SetStrokeCmyk(double c, double m, double y, double k) => Op($"{F(c)} {F(m)} {F(y)} {F(k)} K");

    // ---- Optional content ----

    /// <summary>Opens a marked-content block attributed to the layer (/OC ... BDC).</summary>
    public PdfCanvas BeginLayer(Document.PdfOptionalContentGroup layer)
    {
        var properties = _resources.GetAsDictionary(CosNames.Properties);
        if (properties == null)
        {
            properties = new CosDictionary();
            _resources.Put(CosNames.Properties, properties);
        }

        CosName? name = null;
        foreach (var (key, value) in properties)
        {
            if (value is CosIndirectReference existing && existing.Equals(layer.Reference))
            {
                name = key;
                break;
            }
        }

        if (name == null)
        {
            for (var i = 1; ; i++)
            {
                var candidate = new CosName("FesOc" + i);
                if (!properties.ContainsKey(candidate))
                {
                    properties.Put(candidate, layer.Reference);
                    name = candidate;
                    break;
                }
            }
        }

        return Op($"/OC /{name.Value} BDC");
    }

    public PdfCanvas EndLayer() => Op("EMC");

    // ---- Separation color ----

    public PdfCanvas SetStrokeSeparation(Document.PdfSeparationColor color, double tint = 1.0)
    {
        var name = RegisterColorSpace(color.ColorSpaceReference);
        return Op($"/{name.Value} CS {F(tint)} SCN");
    }

    public PdfCanvas SetFillSeparation(Document.PdfSeparationColor color, double tint = 1.0)
    {
        var name = RegisterColorSpace(color.ColorSpaceReference);
        return Op($"/{name.Value} cs {F(tint)} scn");
    }

    private CosName RegisterColorSpace(CosIndirectReference reference)
    {
        var colorSpaces = _resources.GetAsDictionary(CosNames.ColorSpace);
        if (colorSpaces == null)
        {
            colorSpaces = new CosDictionary();
            _resources.Put(CosNames.ColorSpace, colorSpaces);
        }

        foreach (var (key, value) in colorSpaces)
        {
            if (value is CosIndirectReference existing && existing.Equals(reference))
            {
                return key;
            }
        }

        for (var i = 1; ; i++)
        {
            var candidate = new CosName("FesCs" + i);
            if (!colorSpaces.ContainsKey(candidate))
            {
                colorSpaces.Put(candidate, reference);
                return candidate;
            }
        }
    }

    // ---- Text ----

    private Fonts.PdfFont? _currentFont;

    public PdfCanvas BeginText() => Op("BT");

    public PdfCanvas EndText() => Op("ET");

    public PdfCanvas SetFontAndSize(Fonts.PdfFont font, double size)
    {
        _currentFont = font;
        var name = RegisterFont(font);
        return Op($"/{name.Value} {F(size)} Tf");
    }

    public PdfCanvas SetTextMatrix(double a, double b, double c, double d, double e, double f) =>
        Op($"{F(a)} {F(b)} {F(c)} {F(d)} {F(e)} {F(f)} Tm");

    public PdfCanvas MoveText(double x, double y) => Op($"{F(x)} {F(y)} Td");

    public PdfCanvas SetTextLeading(double leading) => Op($"{F(leading)} TL");

    /// <summary>Shows text using the font set by SetFontAndSize (which handles the encoding).</summary>
    public PdfCanvas ShowText(string text)
    {
        var font = _currentFont
            ?? throw new InvalidOperationException("Call SetFontAndSize before ShowText.");
        return Op($"{IO.PdfSerializer.FormatStringToken(font.EncodeText(text))} Tj");
    }

    private CosName RegisterFont(Fonts.PdfFont font)
    {
        var reference = font.GetDictionary(_document);
        var fonts = _resources.GetAsDictionary(CosNames.Font);
        if (fonts == null)
        {
            fonts = new CosDictionary();
            _resources.Put(CosNames.Font, fonts);
        }

        foreach (var (key, value) in fonts)
        {
            if (value is CosIndirectReference existing && existing.Equals(reference))
            {
                return key;
            }
        }

        for (var i = 1; ; i++)
        {
            var name = new CosName("FesF" + i);
            if (!fonts.ContainsKey(name))
            {
                fonts.Put(name, reference);
                return name;
            }
        }
    }

    /// <summary>Escape hatch: writes raw operator text verbatim (caller supplies the trailing newline).</summary>
    public PdfCanvas WriteRaw(string operators)
    {
        _stream.AppendString(operators);
        return this;
    }

    /// <summary>Places a form XObject under the given transformation matrix, wrapped in q/Q.</summary>
    public PdfCanvas AddFormXObject(PdfFormXObject form, double a, double b, double c, double d, double e, double f)
    {
        var name = RegisterXObject(form.Stream, "FesFm");
        return Op($"q {F(a)} {F(b)} {F(c)} {F(d)} {F(e)} {F(f)} cm /{name.Value} Do Q");
    }

    public PdfCanvas AddFormXObject(PdfFormXObject form, PdfMatrix matrix) =>
        AddFormXObject(form, matrix.A, matrix.B, matrix.C, matrix.D, matrix.E, matrix.F);

    /// <summary>Stretches an image to exactly fill the rectangle (matching iText's fitted placement).</summary>
    public PdfCanvas AddImageFittedIntoRectangle(PdfImageXObject image, PdfRect rect)
    {
        var name = RegisterXObject(image.Stream, "FesIm");
        return Op($"q {F(rect.Width)} 0 0 {F(rect.Height)} {F(rect.X)} {F(rect.Y)} cm /{name.Value} Do Q");
    }

    private PdfCanvas Op(string text)
    {
        _stream.AppendString(text + "\n");
        return this;
    }

    private CosName RegisterXObject(CosStream xobjectStream, string prefix)
    {
        var reference = _document.EnsureIndirect(xobjectStream);

        var xobjects = _resources.GetAsDictionary(CosNames.XObject);
        if (xobjects == null)
        {
            xobjects = new CosDictionary();
            _resources.Put(CosNames.XObject, xobjects);
        }

        // Reuse an existing name when this exact object is already registered.
        foreach (var (key, value) in xobjects)
        {
            if (value is CosIndirectReference existing && existing.Equals(reference))
            {
                return key;
            }
        }

        for (var i = 1; ; i++)
        {
            var name = new CosName(prefix + i);
            if (!xobjects.ContainsKey(name))
            {
                xobjects.Put(name, reference);
                return name;
            }
        }
    }

    private static string F(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        return value.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
