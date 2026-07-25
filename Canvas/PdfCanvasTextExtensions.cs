using FrontEndSuite.PdfPlatform.Fonts;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Canvas;

/// <summary>Positioned and wrapped text drawing built on the raw text operators.</summary>
public static class PdfCanvasTextExtensions
{
    /// <summary>
    /// Draws a single line anchored at (x, y): the anchor is the left end, center, or right end
    /// of the baseline per the alignment. verticalMiddle centers the ascender-descender band on
    /// y instead of putting the baseline there (iText's VerticalAlignment.MIDDLE).
    /// </summary>
    public static PdfCanvas ShowTextAligned(
        this PdfCanvas canvas,
        PdfFont font,
        float fontSize,
        string text,
        float x,
        float y,
        TextHorizontalAlignment alignment,
        bool verticalMiddle = false,
        float rotationRadians = 0f)
    {
        var width = font.GetWidth(text, fontSize);
        var dx = alignment switch
        {
            TextHorizontalAlignment.Center => -width / 2f,
            TextHorizontalAlignment.Right => -width,
            _ => 0f
        };
        var dy = verticalMiddle ? -(font.LayoutAscender + font.LayoutDescender) / 2f * fontSize / 1000f : 0f;

        var cos = MathF.Cos(rotationRadians);
        var sin = MathF.Sin(rotationRadians);
        var originX = x + dx * cos - dy * sin;
        var originY = y + dx * sin + dy * cos;

        canvas.BeginText();
        canvas.SetFontAndSize(font, fontSize);
        canvas.SetTextMatrix(cos, sin, -sin, cos, originX, originY);
        canvas.ShowText(text);
        canvas.EndText();
        return canvas;
    }

    /// <summary>
    /// Draws word-wrapped text inside a rectangle: greedy wrap on spaces (splitting overlong
    /// words), first baseline one ascender below the top, lines that would extend below the
    /// bottom dropped — matching iText's Canvas + Paragraph behavior for a single box.
    /// </summary>
    public static PdfCanvas DrawWrappedText(
        this PdfCanvas canvas,
        PdfFont font,
        float fontSize,
        string text,
        PdfRect rect,
        TextHorizontalAlignment alignment,
        float multipliedLeading = 1f)
    {
        var lines = WrapLines(font, fontSize, text, rect.Width);
        if (lines.Count == 0)
        {
            return canvas;
        }

        // iText semantics: the line box is (layout ascender - layout descender) tall, and
        // multiplied leading scales that box, not the font size.
        var ascent = font.LayoutAscender * fontSize / 1000f;
        var descent = font.LayoutDescender * fontSize / 1000f; // negative
        var leading = (ascent - descent) * multipliedLeading;
        var baseline = rect.Top - ascent;

        canvas.BeginText();
        canvas.SetFontAndSize(font, fontSize);
        foreach (var line in lines)
        {
            if (baseline + descent < rect.Bottom - 0.01f)
            {
                break; // line would overflow the box
            }

            var lineWidth = font.GetWidth(line, fontSize);
            var x = alignment switch
            {
                TextHorizontalAlignment.Center => rect.X + (rect.Width - lineWidth) / 2f,
                TextHorizontalAlignment.Right => rect.Right - lineWidth,
                _ => rect.X
            };
            canvas.SetTextMatrix(1, 0, 0, 1, x, baseline);
            canvas.ShowText(line);
            baseline -= leading;
        }

        canvas.EndText();
        return canvas;
    }

    public static List<string> WrapLines(PdfFont font, float fontSize, string text, float maxWidth)
    {
        var lines = new List<string>();
        foreach (var paragraph in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                lines.Add(string.Empty);
                continue;
            }

            var current = "";
            foreach (var word in paragraph.Split(' '))
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (font.GetWidth(candidate, fontSize) <= maxWidth || current.Length == 0 && word.Length == 0)
                {
                    current = candidate;
                    continue;
                }

                if (current.Length > 0)
                {
                    lines.Add(current);
                    current = "";
                }

                // Word alone still too wide: split by characters.
                if (font.GetWidth(word, fontSize) > maxWidth)
                {
                    var piece = "";
                    foreach (var c in word)
                    {
                        if (piece.Length > 0 && font.GetWidth(piece + c, fontSize) > maxWidth)
                        {
                            lines.Add(piece);
                            piece = "";
                        }

                        piece += c;
                    }

                    current = piece;
                }
                else
                {
                    current = word;
                }
            }

            lines.Add(current);
        }

        return lines;
    }
}
