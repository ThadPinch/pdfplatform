using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;

namespace FrontEndSuite.PdfPlatform.Fonts;

/// <summary>
/// One of the standard 14 fonts: metrics from bundled AFM data, WinAnsiEncoding, never embedded.
/// </summary>
public sealed class StandardFont : PdfFont
{
    public static readonly StandardFont Helvetica = new("Helvetica", StandardFontMetrics.HelveticaWidths, StandardFontMetrics.HelveticaAscender, StandardFontMetrics.HelveticaDescender);
    public static readonly StandardFont HelveticaBold = new("Helvetica-Bold", StandardFontMetrics.HelveticaBoldWidths, StandardFontMetrics.HelveticaBoldAscender, StandardFontMetrics.HelveticaBoldDescender);
    public static readonly StandardFont HelveticaOblique = new("Helvetica-Oblique", StandardFontMetrics.HelveticaObliqueWidths, StandardFontMetrics.HelveticaObliqueAscender, StandardFontMetrics.HelveticaObliqueDescender);
    public static readonly StandardFont HelveticaBoldOblique = new("Helvetica-BoldOblique", StandardFontMetrics.HelveticaBoldObliqueWidths, StandardFontMetrics.HelveticaBoldObliqueAscender, StandardFontMetrics.HelveticaBoldObliqueDescender);
    public static readonly StandardFont TimesRoman = new("Times-Roman", StandardFontMetrics.TimesRomanWidths, StandardFontMetrics.TimesRomanAscender, StandardFontMetrics.TimesRomanDescender);
    public static readonly StandardFont TimesBold = new("Times-Bold", StandardFontMetrics.TimesBoldWidths, StandardFontMetrics.TimesBoldAscender, StandardFontMetrics.TimesBoldDescender);
    public static readonly StandardFont TimesItalic = new("Times-Italic", StandardFontMetrics.TimesItalicWidths, StandardFontMetrics.TimesItalicAscender, StandardFontMetrics.TimesItalicDescender);
    public static readonly StandardFont TimesBoldItalic = new("Times-BoldItalic", StandardFontMetrics.TimesBoldItalicWidths, StandardFontMetrics.TimesBoldItalicAscender, StandardFontMetrics.TimesBoldItalicDescender);
    public static readonly StandardFont Courier = new("Courier", StandardFontMetrics.CourierWidths, StandardFontMetrics.CourierAscender, StandardFontMetrics.CourierDescender);
    public static readonly StandardFont CourierBold = new("Courier-Bold", StandardFontMetrics.CourierBoldWidths, StandardFontMetrics.CourierBoldAscender, StandardFontMetrics.CourierBoldDescender);
    public static readonly StandardFont CourierOblique = new("Courier-Oblique", StandardFontMetrics.CourierObliqueWidths, StandardFontMetrics.CourierObliqueAscender, StandardFontMetrics.CourierObliqueDescender);
    public static readonly StandardFont CourierBoldOblique = new("Courier-BoldOblique", StandardFontMetrics.CourierBoldObliqueWidths, StandardFontMetrics.CourierBoldObliqueAscender, StandardFontMetrics.CourierBoldObliqueDescender);

    private readonly short[] _widths;

    private StandardFont(string name, short[] widths, int ascender, int descender)
    {
        Name = name;
        _widths = widths;
        Ascender = ascender;
        Descender = descender;
    }

    public override string Name { get; }

    public override int Ascender { get; }

    public override int Descender { get; }

    public override float GetWidth(string text, float fontSize)
    {
        var total = 0;
        foreach (var c in text)
        {
            var code = WinAnsi.FromUnicode(c);
            if (code >= 32)
            {
                total += _widths[code - 32];
            }
        }

        return total * fontSize / 1000f;
    }

    internal override CosString EncodeText(string text)
    {
        var bytes = new List<byte>(text.Length);
        foreach (var c in text)
        {
            var code = WinAnsi.FromUnicode(c);
            if (code >= 0)
            {
                bytes.Add((byte)code);
            }
        }

        return new CosString(bytes.ToArray(), isHex: false);
    }

    protected override CosObject BuildDictionary(PdfDocument document)
    {
        var dict = new CosDictionary();
        dict.Put(CosNames.Type, CosNames.Font);
        dict.Put(CosNames.Subtype, new CosName("Type1"));
        dict.Put(CosNames.BaseFont, new CosName(Name));
        dict.Put(CosNames.Encoding, new CosName("WinAnsiEncoding"));
        return dict;
    }
}
