using System.Runtime.CompilerServices;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;

namespace FrontEndSuite.PdfPlatform.Fonts;

/// <summary>
/// A font usable for measuring and drawing text. Implementations: the standard 14 (metrics only,
/// never embedded) and embedded TrueType/OpenType fonts (Identity-H).
/// </summary>
public abstract class PdfFont
{
    private readonly ConditionalWeakTable<PdfDocument, CosIndirectReference> _dictionaries = new();

    /// <summary>The /BaseFont name.</summary>
    public abstract string Name { get; }

    /// <summary>Typographic ascender in 1000-unit em space.</summary>
    public abstract int Ascender { get; }

    /// <summary>Typographic descender in 1000-unit em space (negative).</summary>
    public abstract int Descender { get; }

    /// <summary>
    /// Ascender used for line layout, matching iText's conventions: typo ascender × 1.2 for the
    /// standard fonts, usWinAscent for embedded TrueType/OpenType fonts.
    /// </summary>
    public virtual float LayoutAscender => Ascender * 1.2f;

    /// <summary>Layout descender (negative), matching iText: typo × 1.2 or -usWinDescent.</summary>
    public virtual float LayoutDescender => Descender * 1.2f;

    /// <summary>Measures the advance width of the text at the given size, in points.</summary>
    public abstract float GetWidth(string text, float fontSize);

    /// <summary>Encodes text into the string form the show operators need for this font.</summary>
    internal abstract CosString EncodeText(string text);

    /// <summary>Builds the complete font dictionary (and any embedded streams) in the document.</summary>
    protected abstract CosObject BuildDictionary(PdfDocument document);

    /// <summary>The font dictionary for this document, created on first use.</summary>
    internal CosIndirectReference GetDictionary(PdfDocument document)
    {
        if (_dictionaries.TryGetValue(document, out var existing))
        {
            return existing;
        }

        var reference = document.EnsureIndirect(BuildDictionary(document));
        _dictionaries.Add(document, reference);
        return reference;
    }
}

public enum TextHorizontalAlignment
{
    Left,
    Center,
    Right
}
