using FrontEndSuite.PdfPlatform.Canvas;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Fonts;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Layout;

/// <summary>
/// A small flow-layout engine covering the document shapes the suite generates from scratch:
/// paragraphs, percent-column tables (headers, colspan, borders, padding, background, nesting),
/// images, horizontal rules, and URI links — with page breaks and repeated table headers.
/// Not a general text layout system; it implements what the invoice-style documents need.
/// </summary>
public sealed class FlowDocument
{
    private readonly PdfDocument _pdf;
    private readonly PdfRect _pageSize;
    private readonly float _marginTop;
    private readonly float _marginRight;
    private readonly float _marginBottom;
    private readonly float _marginLeft;

    private PdfCanvas? _canvas;
    private PdfPage? _page;
    private float _cursorY;

    public FlowDocument(PdfDocument pdf, PdfRect pageSize, float marginTop, float marginRight, float marginBottom, float marginLeft)
    {
        _pdf = pdf;
        _pageSize = pageSize;
        _marginTop = marginTop;
        _marginRight = marginRight;
        _marginBottom = marginBottom;
        _marginLeft = marginLeft;
    }

    public float ContentWidth => _pageSize.Width - _marginLeft - _marginRight;

    private float BottomLimit => _marginBottom;

    public void Add(FlowElement element)
    {
        EnsurePage();
        if (element is FlowTable table)
        {
            AddTable(table);
            return;
        }

        _cursorY -= element.MarginTop;
        var height = element.Measure(ContentWidth);
        if (_cursorY - height < BottomLimit && height <= _pageSize.Height - _marginTop - _marginBottom)
        {
            NewPage();
        }

        element.Draw(Context(), _marginLeft, _cursorY, ContentWidth);
        _cursorY -= height + element.MarginBottom;
    }

    private void AddTable(FlowTable table)
    {
        _cursorY -= table.MarginTop;
        var widths = table.ResolveColumnWidths(ContentWidth);

        void DrawRows(IEnumerable<FlowTable.Row> rows)
        {
            foreach (var row in rows)
            {
                var rowHeight = table.MeasureRow(row, widths);
                if (_cursorY - rowHeight < BottomLimit)
                {
                    NewPage();
                    foreach (var headerRow in table.HeaderRows)
                    {
                        var headerHeight = table.MeasureRow(headerRow, widths);
                        table.DrawRow(Context(), headerRow, widths, _marginLeft, _cursorY);
                        _cursorY -= headerHeight;
                    }
                }

                table.DrawRow(Context(), row, widths, _marginLeft, _cursorY);
                _cursorY -= rowHeight;
            }
        }

        DrawRows(table.HeaderRows);
        DrawRows(table.BodyRows);
        _cursorY -= table.MarginBottom;
    }

    public void NewPage()
    {
        _page = _pdf.AddPage(_pageSize);
        var resources = new CosDictionary();
        _page.Dictionary.Put(CosNames.Resources, resources);
        _canvas = new PdfCanvas(_page.AddContentStreamAfter(), resources, _pdf);
        _cursorY = _pageSize.Height - _marginTop;
    }

    private void EnsurePage()
    {
        if (_canvas == null)
        {
            NewPage();
        }
    }

    private FlowContext Context() => new(_pdf, _page!, _canvas!);
}

public sealed record FlowContext(PdfDocument Document, PdfPage Page, PdfCanvas Canvas);

public abstract class FlowElement
{
    public float MarginTop { get; set; }

    public float MarginBottom { get; set; }

    internal abstract float Measure(float width);

    internal abstract void Draw(FlowContext context, float x, float top, float width);
}

/// <summary>A styled run of text within a paragraph.</summary>
public sealed class FlowText
{
    public FlowText(string text)
    {
        Text = text;
    }

    public string Text { get; }

    public PdfFont? Font { get; set; }

    public float? FontSize { get; set; }

    public (float R, float G, float B)? Color { get; set; }

    public bool Underline { get; set; }

    /// <summary>When set, the drawn text becomes a link annotation to this URI.</summary>
    public string? Uri { get; set; }
}

public sealed class FlowParagraph : FlowElement
{
    private readonly List<FlowText> _runs = new();

    public FlowParagraph()
    {
    }

    public FlowParagraph(string text)
    {
        _runs.Add(new FlowText(text));
    }

    public IReadOnlyList<FlowText> Runs => _runs;

    public TextHorizontalAlignment Alignment { get; set; } = TextHorizontalAlignment.Left;

    /// <summary>Line advance as a multiple of the font size (iText's default is 1.35).</summary>
    public float MultipliedLeading { get; set; } = 1.35f;

    public PdfFont? DefaultFont { get; set; }

    public float? DefaultFontSize { get; set; }

    public (float R, float G, float B)? DefaultColor { get; set; }

    public FlowParagraph Add(FlowText run)
    {
        _runs.Add(run);
        return this;
    }

    public FlowParagraph WithColor((float R, float G, float B) color)
    {
        DefaultColor = color;
        return this;
    }

    private (PdfFont Font, float Size, (float R, float G, float B)? Color, bool Underline, string? Uri) Style(int runIndex)
    {
        var run = _runs[runIndex];
        return (
            run.Font ?? DefaultFont ?? StandardFont.Helvetica,
            run.FontSize ?? DefaultFontSize ?? 12f,
            run.Color ?? DefaultColor,
            run.Underline,
            run.Uri);
    }

    private List<string> WrapAll(float width, out PdfFont font, out float size)
    {
        // The suite's paragraphs are single-style; wrap the concatenated text with run 0's style.
        font = StandardFont.Helvetica;
        size = 12f;
        if (_runs.Count == 0)
        {
            return new List<string>();
        }

        var style = Style(0);
        font = style.Font;
        size = style.Size;
        var text = string.Concat(_runs.Select(r => r.Text));
        return PdfCanvasTextExtensions.WrapLines(font, size, text, Math.Max(1f, width));
    }

    internal override float Measure(float width)
    {
        var lines = WrapAll(width, out _, out var size);
        if (lines.Count == 0)
        {
            return 0f;
        }

        return lines.Count * size * MultipliedLeading;
    }

    internal override void Draw(FlowContext context, float x, float top, float width)
    {
        var lines = WrapAll(width, out var font, out var size);
        if (lines.Count == 0)
        {
            return;
        }

        var style = Style(0);
        var lineHeight = size * MultipliedLeading;
        var ascent = font.LayoutAscender * size / 1000f;
        var descent = font.LayoutDescender * size / 1000f;
        var band = ascent - descent;
        var firstBaseline = top - lineHeight + (lineHeight - band) / 2f - descent - (lineHeight - band) / 2f;
        // Simplifies to top - lineHeight - descent; keep explicit for clarity of intent.
        firstBaseline = top - lineHeight - descent;

        var canvas = context.Canvas;
        canvas.SaveState();
        if (style.Color is { } color)
        {
            canvas.SetFillRgb(color.R, color.G, color.B);
            canvas.SetStrokeRgb(color.R, color.G, color.B);
        }

        var baseline = firstBaseline;
        foreach (var line in lines)
        {
            var lineWidth = font.GetWidth(line, size);
            var drawX = Alignment switch
            {
                TextHorizontalAlignment.Center => x + (width - lineWidth) / 2f,
                TextHorizontalAlignment.Right => x + width - lineWidth,
                _ => x
            };

            canvas.BeginText();
            canvas.SetFontAndSize(font, size);
            canvas.SetTextMatrix(1, 0, 0, 1, drawX, baseline);
            canvas.ShowText(line);
            canvas.EndText();

            if (style.Underline)
            {
                canvas.SetLineWidth(Math.Max(0.5f, size / 15f));
                canvas.MoveTo(drawX, baseline - size * 0.12f);
                canvas.LineTo(drawX + lineWidth, baseline - size * 0.12f);
                canvas.Stroke();
            }

            if (!string.IsNullOrEmpty(style.Uri))
            {
                AddLinkAnnotation(context, style.Uri!, drawX, baseline + descent, lineWidth, band);
            }

            baseline -= lineHeight;
        }

        canvas.RestoreState();
    }

    private static void AddLinkAnnotation(FlowContext context, string uri, float x, float y, float width, float height)
    {
        var action = new CosDictionary();
        action.Put(new CosName("S"), new CosName("URI"));
        action.Put(new CosName("URI"), new CosString(uri));

        var annotation = new CosDictionary();
        annotation.Put(CosNames.Type, CosNames.Annot);
        annotation.Put(CosNames.Subtype, new CosName("Link"));
        annotation.Put(CosNames.Rect, new PdfRect(x, y, width, height).ToCosArray());
        annotation.Put(new CosName("Border"), new CosArray(new CosObject[]
        {
            new CosNumber(0), new CosNumber(0), new CosNumber(0)
        }));
        annotation.Put(new CosName("A"), action);
        context.Page.AddAnnotation(annotation);
    }
}

public sealed class FlowImage : FlowElement
{
    private readonly PdfImageXObject _image;
    private readonly float _drawWidth;
    private readonly float _drawHeight;

    public FlowImage(PdfImageXObject image, float drawWidth, float drawHeight)
    {
        _image = image;
        _drawWidth = drawWidth;
        _drawHeight = drawHeight;
    }

    internal override float Measure(float width) => _drawHeight;

    internal override void Draw(FlowContext context, float x, float top, float width)
    {
        context.Canvas.AddImageFittedIntoRectangle(_image, new PdfRect(x, top - _drawHeight, _drawWidth, _drawHeight));
    }
}

public sealed class FlowLineSeparator : FlowElement
{
    public float LineWidth { get; set; } = 0.5f;

    internal override float Measure(float width) => LineWidth;

    internal override void Draw(FlowContext context, float x, float top, float width)
    {
        var canvas = context.Canvas;
        canvas.SaveState();
        canvas.SetStrokeRgb(0, 0, 0);
        canvas.SetLineWidth(LineWidth);
        canvas.MoveTo(x, top - LineWidth / 2f);
        canvas.LineTo(x + width, top - LineWidth / 2f);
        canvas.Stroke();
        canvas.RestoreState();
    }
}

public sealed class FlowCell
{
    private readonly List<FlowElement> _children = new();

    public FlowCell(int colspan = 1)
    {
        Colspan = Math.Max(1, colspan);
    }

    public int Colspan { get; }

    public IReadOnlyList<FlowElement> Children => _children;

    /// <summary>null = the default 0.5pt black grid border; use SetNoBorder or SetBorder to override.</summary>
    public ((float R, float G, float B) Color, float Width)? Border { get; set; }

    public bool NoBorder { get; set; }

    public float Padding { get; set; } = 2f;

    public float? PaddingBottom { get; set; }

    public (float R, float G, float B)? Background { get; set; }

    public TextHorizontalAlignment? TextAlignment { get; set; }

    public PdfFont? DefaultFont { get; set; }

    public float? DefaultFontSize { get; set; }

    public FlowCell Add(FlowElement child)
    {
        _children.Add(child);
        return this;
    }

    internal void ApplyDefaults()
    {
        foreach (var child in _children)
        {
            if (child is FlowParagraph paragraph)
            {
                paragraph.DefaultFont ??= DefaultFont;
                if (paragraph.DefaultFontSize == null && DefaultFontSize != null)
                {
                    paragraph.DefaultFontSize = DefaultFontSize;
                }

                if (TextAlignment != null && paragraph.Alignment == TextHorizontalAlignment.Left)
                {
                    paragraph.Alignment = TextAlignment.Value;
                }
            }
        }
    }
}

public sealed class FlowTable : FlowElement
{
    internal sealed record Row(List<(FlowCell Cell, int ColumnIndex)> Cells);

    private readonly float[] _percentColumns;
    private readonly List<Row> _headerRows = new();
    private readonly List<Row> _bodyRows = new();
    private Row? _openHeaderRow;
    private Row? _openBodyRow;
    private int _headerColumn;
    private int _bodyColumn;

    public FlowTable(float[] percentColumns)
    {
        _percentColumns = percentColumns;
    }

    internal IReadOnlyList<Row> HeaderRows => _headerRows;

    internal IReadOnlyList<Row> BodyRows => _bodyRows;

    public FlowTable AddHeaderCell(FlowCell cell)
    {
        AddToGrid(cell, _headerRows, ref _openHeaderRow, ref _headerColumn);
        return this;
    }

    public FlowTable AddCell(FlowCell cell)
    {
        AddToGrid(cell, _bodyRows, ref _openBodyRow, ref _bodyColumn);
        return this;
    }

    private void AddToGrid(FlowCell cell, List<Row> rows, ref Row? openRow, ref int column)
    {
        cell.ApplyDefaults();
        if (openRow == null || column + cell.Colspan > _percentColumns.Length)
        {
            openRow = new Row(new List<(FlowCell, int)>());
            rows.Add(openRow);
            column = 0;
        }

        openRow.Cells.Add((cell, column));
        column += cell.Colspan;
        if (column >= _percentColumns.Length)
        {
            openRow = null;
            column = 0;
        }
    }

    internal float[] ResolveColumnWidths(float totalWidth)
    {
        var sum = _percentColumns.Sum();
        return _percentColumns.Select(p => totalWidth * p / sum).ToArray();
    }

    private static float CellWidth(float[] widths, int columnIndex, int colspan) =>
        Enumerable.Range(columnIndex, colspan).Sum(i => widths[i]);

    internal float MeasureRow(Row row, float[] widths)
    {
        var height = 0f;
        foreach (var (cell, columnIndex) in row.Cells)
        {
            var innerWidth = CellWidth(widths, columnIndex, cell.Colspan) - cell.Padding * 2f;
            var contentHeight = MeasureCellContent(cell, innerWidth);
            var padBottom = cell.PaddingBottom ?? cell.Padding;
            height = Math.Max(height, contentHeight + cell.Padding + padBottom);
        }

        return height;
    }

    private static float MeasureCellContent(FlowCell cell, float width)
    {
        var height = 0f;
        foreach (var child in cell.Children)
        {
            if (child is FlowTable nested)
            {
                var nestedWidths = nested.ResolveColumnWidths(width);
                height += child.MarginTop + child.MarginBottom;
                foreach (var nestedRow in nested.HeaderRows.Concat(nested.BodyRows))
                {
                    height += nested.MeasureRow(nestedRow, nestedWidths);
                }
            }
            else
            {
                height += child.MarginTop + child.Measure(width) + child.MarginBottom;
            }
        }

        return height;
    }

    internal void DrawRow(FlowContext context, Row row, float[] widths, float tableX, float top)
    {
        var rowHeight = MeasureRow(row, widths);
        foreach (var (cell, columnIndex) in row.Cells)
        {
            var x = tableX + Enumerable.Range(0, columnIndex).Sum(i => widths[i]);
            var cellWidth = CellWidth(widths, columnIndex, cell.Colspan);
            var canvas = context.Canvas;

            if (cell.Background is { } bg)
            {
                canvas.SaveState();
                canvas.SetFillRgb(bg.R, bg.G, bg.B);
                canvas.Rectangle(x, top - rowHeight, cellWidth, rowHeight);
                canvas.Fill();
                canvas.RestoreState();
            }

            if (!cell.NoBorder)
            {
                var (color, borderWidth) = cell.Border ?? ((0f, 0f, 0f), 0.5f);
                canvas.SaveState();
                canvas.SetStrokeRgb(color.R, color.G, color.B);
                canvas.SetLineWidth(borderWidth);
                canvas.Rectangle(x, top - rowHeight, cellWidth, rowHeight);
                canvas.Stroke();
                canvas.RestoreState();
            }

            var childTop = top - cell.Padding;
            var innerX = x + cell.Padding;
            var innerWidth = cellWidth - cell.Padding * 2f;
            foreach (var child in cell.Children)
            {
                childTop -= child.MarginTop;
                if (child is FlowTable nested)
                {
                    var nestedWidths = nested.ResolveColumnWidths(innerWidth);
                    foreach (var nestedRow in nested.HeaderRows.Concat(nested.BodyRows))
                    {
                        var nestedHeight = nested.MeasureRow(nestedRow, nestedWidths);
                        nested.DrawRow(context, nestedRow, nestedWidths, innerX, childTop);
                        childTop -= nestedHeight;
                    }
                }
                else
                {
                    var childHeight = child.Measure(innerWidth);
                    child.Draw(context, innerX, childTop, innerWidth);
                    childTop -= childHeight;
                }

                childTop -= child.MarginBottom;
            }
        }
    }

    internal override float Measure(float width)
    {
        var widths = ResolveColumnWidths(width);
        return _headerRows.Concat(_bodyRows).Sum(row => MeasureRow(row, widths));
    }

    internal override void Draw(FlowContext context, float x, float top, float width)
    {
        // Tables are paginated row-by-row by FlowDocument; direct Draw renders without breaks.
        var widths = ResolveColumnWidths(width);
        foreach (var row in _headerRows.Concat(_bodyRows))
        {
            var height = MeasureRow(row, widths);
            DrawRow(context, row, widths, x, top);
            top -= height;
        }
    }
}
