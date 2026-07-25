using System.Text;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Geometry;
using FrontEndSuite.PdfPlatform.IO;

namespace FrontEndSuite.PdfPlatform.Parsing;

/// <summary>
/// Replays a content stream and raises image/text/path render events, tracking the graphics
/// state (CTM, line width, font), text matrices, marked-content scopes, and recursing into
/// form XObjects. Text matrices are not advanced by glyph widths between show operations —
/// translation does not affect the scale components our consumers measure.
/// </summary>
public sealed class ContentStreamProcessor
{
    private const int MaxFormDepth = 12;
    private const int MaxOperands = 64;

    private static readonly MarkedContentTag[] NoTags = Array.Empty<MarkedContentTag>();

    private readonly IContentListener _listener;
    private readonly Stack<GraphicsState> _stateStack = new();
    private readonly List<MarkedContentTag> _tagStack = new();
    private readonly HashSet<CosStream> _formsInProgress = new();

    private GraphicsState _state = new();
    private PdfMatrix _textMatrix = PdfMatrix.Identity;
    private PdfMatrix _textLineMatrix = PdfMatrix.Identity;
    private PathBuilder _path = new();

    public ContentStreamProcessor(IContentListener listener)
    {
        _listener = listener;
    }

    /// <summary>Replays the page's content with its resources. State is reset per call.</summary>
    public void ProcessPage(PdfPage page)
    {
        Reset();
        Execute(page.GetContentBytes(), page.Resources, depth: 0);
    }

    /// <summary>Replays arbitrary content bytes against a resources dictionary.</summary>
    public void ProcessContent(byte[] contentBytes, CosDictionary? resources)
    {
        Reset();
        Execute(contentBytes, resources, depth: 0);
    }

    private void Reset()
    {
        _stateStack.Clear();
        _tagStack.Clear();
        _formsInProgress.Clear();
        _state = new GraphicsState();
        _textMatrix = PdfMatrix.Identity;
        _textLineMatrix = PdfMatrix.Identity;
        _path = new PathBuilder();
    }

    private void Execute(byte[] content, CosDictionary? resources, int depth)
    {
        var parser = new CosObjectParser(new PdfLexer(content), null);
        var operands = new List<CosObject>();

        while (true)
        {
            var token = parser.Peek();
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Kind == PdfTokenKind.Keyword && token.Text is not ("true" or "false" or "null"))
            {
                parser.Next();
                ExecuteOperator(token.Text!, operands, resources, parser, depth);
                operands.Clear();
            }
            else
            {
                operands.Add(parser.ParseObject());
                if (operands.Count > MaxOperands)
                {
                    operands.RemoveAt(0);
                }
            }
        }
    }

    private void ExecuteOperator(string op, List<CosObject> operands, CosDictionary? resources, CosObjectParser parser, int depth)
    {
        switch (op)
        {
            // ---- Graphics state ----
            case "q":
                _stateStack.Push(_state.Clone());
                break;
            case "Q":
                if (_stateStack.Count > 0)
                {
                    _state = _stateStack.Pop();
                }

                break;
            case "cm":
                if (TryGetFloats(operands, 6, out var cm))
                {
                    _state.Ctm = new PdfMatrix(cm[0], cm[1], cm[2], cm[3], cm[4], cm[5]).Multiply(_state.Ctm);
                }

                break;
            case "w":
                if (TryGetFloats(operands, 1, out var lw))
                {
                    _state.LineWidth = lw[0];
                }

                break;
            case "gs":
                ApplyExtGState(operands, resources);
                break;

            // ---- Path construction ----
            case "m":
                if (TryGetFloats(operands, 2, out var mp))
                {
                    _path.MoveTo(mp[0], mp[1]);
                }

                break;
            case "l":
                if (TryGetFloats(operands, 2, out var lp))
                {
                    _path.LineTo(lp[0], lp[1]);
                }

                break;
            case "c":
                if (TryGetFloats(operands, 6, out var c3))
                {
                    _path.CurveTo(new PdfPoint(c3[0], c3[1]), new PdfPoint(c3[2], c3[3]), new PdfPoint(c3[4], c3[5]));
                }

                break;
            case "v":
                if (TryGetFloats(operands, 4, out var cv))
                {
                    _path.CurveTo(_path.CurrentPoint, new PdfPoint(cv[0], cv[1]), new PdfPoint(cv[2], cv[3]));
                }

                break;
            case "y":
                if (TryGetFloats(operands, 4, out var cy))
                {
                    var end = new PdfPoint(cy[2], cy[3]);
                    _path.CurveTo(new PdfPoint(cy[0], cy[1]), end, end);
                }

                break;
            case "h":
                _path.Close();
                break;
            case "re":
                if (TryGetFloats(operands, 4, out var re))
                {
                    _path.Rectangle(re[0], re[1], re[2], re[3]);
                }

                break;

            // ---- Path painting ----
            case "S":
                EmitPath(PathPaintOperation.Stroke, closeFirst: false);
                break;
            case "s":
                EmitPath(PathPaintOperation.Stroke, closeFirst: true);
                break;
            case "f":
            case "F":
            case "f*":
                EmitPath(PathPaintOperation.Fill, closeFirst: false);
                break;
            case "B":
            case "B*":
                EmitPath(PathPaintOperation.Fill | PathPaintOperation.Stroke, closeFirst: false);
                break;
            case "b":
            case "b*":
                EmitPath(PathPaintOperation.Fill | PathPaintOperation.Stroke, closeFirst: true);
                break;
            case "n":
                EmitPath(PathPaintOperation.NoOp, closeFirst: false);
                break;
            case "W":
            case "W*":
                break; // clip changes carry no render event for our consumers

            // ---- Text ----
            case "BT":
                _textMatrix = PdfMatrix.Identity;
                _textLineMatrix = PdfMatrix.Identity;
                break;
            case "ET":
                break;
            case "Tf":
                if (operands.Count >= 2 && operands[^2] is CosName fontName)
                {
                    _state.Font = resources?.GetAsDictionary(CosNames.Font)?.GetAsDictionary(fontName);
                }

                if (operands.Count >= 1 && operands[^1] is CosNumber size)
                {
                    _state.FontSize = size.FloatValue;
                }

                break;
            case "Tr":
                if (TryGetFloats(operands, 1, out var mode))
                {
                    _state.TextRenderMode = (int)mode[0];
                }

                break;
            case "TL":
                if (TryGetFloats(operands, 1, out var leading))
                {
                    _state.Leading = leading[0];
                }

                break;
            case "Td":
                if (TryGetFloats(operands, 2, out var td))
                {
                    TranslateTextLine(td[0], td[1]);
                }

                break;
            case "TD":
                if (TryGetFloats(operands, 2, out var tD))
                {
                    _state.Leading = -tD[1];
                    TranslateTextLine(tD[0], tD[1]);
                }

                break;
            case "Tm":
                if (TryGetFloats(operands, 6, out var tm))
                {
                    _textLineMatrix = new PdfMatrix(tm[0], tm[1], tm[2], tm[3], tm[4], tm[5]);
                    _textMatrix = _textLineMatrix;
                }

                break;
            case "T*":
                TranslateTextLine(0f, -_state.Leading);
                break;
            case "Tj":
                if (operands.Count >= 1 && operands[^1] is CosString text)
                {
                    EmitText(text);
                }

                break;
            case "'":
                TranslateTextLine(0f, -_state.Leading);
                if (operands.Count >= 1 && operands[^1] is CosString quoted)
                {
                    EmitText(quoted);
                }

                break;
            case "\"":
                TranslateTextLine(0f, -_state.Leading);
                if (operands.Count >= 1 && operands[^1] is CosString dquoted)
                {
                    EmitText(dquoted);
                }

                break;
            case "TJ":
                if (operands.Count >= 1 && operands[^1] is CosArray runs)
                {
                    foreach (var item in runs)
                    {
                        if (item is CosString chunk)
                        {
                            EmitText(chunk);
                        }
                    }
                }

                break;

            // ---- XObjects ----
            case "Do":
                if (operands.Count >= 1 && operands[^1] is CosName xobjectName)
                {
                    ExecuteXObject(xobjectName, resources, depth);
                }

                break;
            case "BI":
                HandleInlineImage(parser);
                break;

            // ---- Marked content ----
            case "BMC":
                if (operands.Count >= 1 && operands[^1] is CosName bmcRole)
                {
                    _tagStack.Add(new MarkedContentTag { Role = bmcRole });
                }

                break;
            case "BDC":
                if (operands.Count >= 2 && operands[^2] is CosName bdcRole)
                {
                    var properties = operands[^1] switch
                    {
                        CosDictionary dict => dict,
                        CosName propName => resources?.GetAsDictionary(CosNames.Properties)?.Get(propName),
                        _ => null
                    };
                    _tagStack.Add(new MarkedContentTag { Role = bdcRole, Properties = properties });
                }

                break;
            case "EMC":
                if (_tagStack.Count > 0)
                {
                    _tagStack.RemoveAt(_tagStack.Count - 1);
                }

                break;
        }
    }

    // ---- Event emission ----

    private void EmitPath(PathPaintOperation operation, bool closeFirst)
    {
        if (closeFirst)
        {
            _path.Close();
        }

        _listener.OnPath(new PathRenderEvent
        {
            Path = _path.Build(),
            Operation = operation,
            LineWidth = _state.LineWidth,
            Ctm = _state.Ctm,
            Tags = SnapshotTags()
        });
        _path = new PathBuilder();
    }

    private void EmitText(CosString text)
    {
        _listener.OnText(new TextRenderEvent
        {
            Text = DecodeText(text.RawBytes, _state.Font),
            TextMatrix = _textMatrix,
            TextToPageMatrix = _textMatrix.Multiply(_state.Ctm),
            FontSize = _state.FontSize,
            RenderMode = _state.TextRenderMode,
            Font = _state.Font,
            Tags = SnapshotTags()
        });
    }

    private void ExecuteXObject(CosName name, CosDictionary? resources, int depth)
    {
        var stream = resources?.GetAsDictionary(CosNames.XObject)?.GetAsStream(name);
        if (stream == null)
        {
            return;
        }

        var subtype = stream.GetAsName(CosNames.Subtype);
        if (CosNames.Image.Equals(subtype))
        {
            _listener.OnImage(new ImageRenderEvent
            {
                Image = new PdfImageXObject(stream),
                Ctm = _state.Ctm,
                Tags = SnapshotTags()
            });
            return;
        }

        if (!CosNames.Form.Equals(subtype) || depth >= MaxFormDepth || !_formsInProgress.Add(stream))
        {
            return;
        }

        try
        {
            if (!stream.TryGetDecodedBytes(out var formContent))
            {
                return;
            }

            _stateStack.Push(_state.Clone());
            var savedText = (_textMatrix, _textLineMatrix);
            try
            {
                var matrix = stream.GetAsArray(CosNames.Matrix);
                if (matrix != null && matrix.Count >= 6)
                {
                    _state.Ctm = new PdfMatrix(
                        matrix.GetAsFloat(0) ?? 1, matrix.GetAsFloat(1) ?? 0,
                        matrix.GetAsFloat(2) ?? 0, matrix.GetAsFloat(3) ?? 1,
                        matrix.GetAsFloat(4) ?? 0, matrix.GetAsFloat(5) ?? 0).Multiply(_state.Ctm);
                }

                var formResources = stream.GetAsDictionary(CosNames.Resources) ?? resources;
                Execute(formContent, formResources, depth + 1);
            }
            finally
            {
                _state = _stateStack.Pop();
                (_textMatrix, _textLineMatrix) = savedText;
            }
        }
        finally
        {
            _formsInProgress.Remove(stream);
        }
    }

    private void HandleInlineImage(CosObjectParser parser)
    {
        var dict = new CosDictionary();
        while (true)
        {
            var token = parser.Peek();
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                return;
            }

            if (token.IsKeyword("ID"))
            {
                parser.Next();
                break;
            }

            if (token.Kind == PdfTokenKind.Name)
            {
                var key = ExpandInlineKey(parser.Next().Text!);
                var value = parser.ParseObject();
                dict.Put(key, ExpandInlineValue(value));
            }
            else
            {
                parser.Next();
            }
        }

        var lexer = parser.Lexer;
        var data = lexer.Data;
        var position = lexer.Position;
        if (position < data.Length && PdfLexer.IsWhitespace(data[position]))
        {
            position++;
        }

        var end = -1;
        for (var i = position; i + 1 < data.Length; i++)
        {
            if (data[i] == (byte)'E' && data[i + 1] == (byte)'I'
                && i > position && PdfLexer.IsWhitespace(data[i - 1])
                && (i + 2 >= data.Length || !PdfLexer.IsRegular(data[i + 2])))
            {
                end = i;
                break;
            }
        }

        if (end < 0)
        {
            lexer.Position = data.Length;
            parser.DiscardLookahead();
            return;
        }

        var dataEnd = end - 1; // exclude the whitespace byte before EI
        var raw = new byte[Math.Max(0, dataEnd - position)];
        Array.Copy(data, position, raw, 0, raw.Length);
        lexer.Position = end + 2;
        parser.DiscardLookahead();

        var stream = new CosStream(dict, raw);
        _listener.OnImage(new ImageRenderEvent
        {
            Image = new PdfImageXObject(stream),
            Ctm = _state.Ctm,
            Tags = SnapshotTags()
        });
    }

    private static CosName ExpandInlineKey(string abbreviated) => abbreviated switch
    {
        "W" => CosNames.Width,
        "H" => CosNames.Height,
        "BPC" => CosNames.BitsPerComponent,
        "CS" => CosNames.ColorSpace,
        "F" => CosNames.Filter,
        "IM" => CosNames.ImageMask,
        "D" => CosNames.Decode,
        "DP" => CosNames.DecodeParms,
        "I" => new CosName("Interpolate"),
        _ => new CosName(abbreviated)
    };

    private static CosObject ExpandInlineValue(CosObject value) => value switch
    {
        CosName { Value: "G" } => CosNames.DeviceGray,
        CosName { Value: "RGB" } => CosNames.DeviceRGB,
        CosName { Value: "CMYK" } => new CosName("DeviceCMYK"),
        CosName { Value: "I" } => CosNames.Indexed,
        CosName { Value: "AHx" } => CosNames.ASCIIHexDecode,
        CosName { Value: "A85" } => CosNames.ASCII85Decode,
        CosName { Value: "LZW" } => CosNames.LZWDecode,
        CosName { Value: "Fl" } => CosNames.FlateDecode,
        CosName { Value: "RL" } => CosNames.RunLengthDecode,
        CosName { Value: "CCF" } => CosNames.CCITTFaxDecode,
        CosName { Value: "DCT" } => CosNames.DCTDecode,
        _ => value
    };

    private void ApplyExtGState(List<CosObject> operands, CosDictionary? resources)
    {
        if (operands.Count < 1 || operands[^1] is not CosName name)
        {
            return;
        }

        var gs = resources?.GetAsDictionary(CosNames.ExtGState)?.GetAsDictionary(name);
        if (gs == null)
        {
            return;
        }

        if (gs.GetAsFloat(CosNames.LW) is { } lineWidth)
        {
            _state.LineWidth = lineWidth;
        }

        if (gs.GetAsArray(CosNames.Font) is { Count: >= 2 } fontEntry)
        {
            _state.Font = fontEntry.GetAsDictionary(0);
            _state.FontSize = fontEntry.GetAsFloat(1) ?? _state.FontSize;
        }
    }

    private void TranslateTextLine(float tx, float ty)
    {
        _textLineMatrix = PdfMatrix.Translate(tx, ty).Multiply(_textLineMatrix);
        _textMatrix = _textLineMatrix;
    }

    private IReadOnlyList<MarkedContentTag> SnapshotTags()
    {
        if (_tagStack.Count == 0)
        {
            return NoTags;
        }

        var snapshot = new MarkedContentTag[_tagStack.Count];
        for (var i = 0; i < _tagStack.Count; i++)
        {
            snapshot[i] = _tagStack[_tagStack.Count - 1 - i]; // most recent first
        }

        return snapshot;
    }

    private readonly Dictionary<CosDictionary, FontDecoder> _fontDecoderCache = new();

    private string DecodeText(byte[] bytes, CosDictionary? font)
    {
        if (font == null)
        {
            return Encoding.Latin1.GetString(bytes);
        }

        if (!_fontDecoderCache.TryGetValue(font, out var decoder))
        {
            decoder = FontDecoder.Build(font);
            _fontDecoderCache[font] = decoder;
        }

        return decoder.Decode(bytes);
    }

    /// <summary>
    /// Decodes show-operator strings the way a viewer would: via /ToUnicode when present, via
    /// /Encoding /Differences glyph names for simple fonts, Latin-1 otherwise. Type0 fonts with
    /// no ToUnicode decode to raw big-endian code chars — garbled but non-empty, which is what
    /// text-presence checks need (and how iText reports them too).
    /// </summary>
    private sealed class FontDecoder
    {
        private ToUnicodeCMap? _cmap;
        private bool _undecodableType0;
        private char[]? _simpleMap;

        public static FontDecoder Build(CosDictionary font)
        {
            var decoder = new FontDecoder();
            try
            {
                if (font.GetAsStream(CosNames.ToUnicode) is { } stream
                    && stream.TryGetDecodedBytes(out var data))
                {
                    decoder._cmap = ToUnicodeCMap.Parse(data);
                }
            }
            catch
            {
                // Treated as absent.
            }

            if (decoder._cmap != null)
            {
                return decoder;
            }

            if (font.GetAsName(CosNames.Subtype) is { Value: "Type0" })
            {
                decoder._undecodableType0 = true;
                return decoder;
            }

            if (font.GetAsDictionary(CosNames.Encoding)?.GetAsArray(CosNames.Differences) is { } differences)
            {
                var map = new char[256];
                for (var i = 0; i < 256; i++)
                {
                    map[i] = (char)i;
                }

                var code = 0;
                for (var i = 0; i < differences.Count; i++)
                {
                    var item = differences.Get(i);
                    if (item is CosNumber number)
                    {
                        code = number.IntValue;
                    }
                    else if (item is CosName glyph && code is >= 0 and < 256)
                    {
                        map[code++] = GlyphNameToChar(glyph.Value);
                    }
                }

                decoder._simpleMap = map;
            }

            return decoder;
        }

        public string Decode(byte[] bytes)
        {
            if (_cmap != null)
            {
                return _cmap.Decode(bytes);
            }

            if (_undecodableType0)
            {
                var cids = new char[(bytes.Length + 1) / 2];
                for (var i = 0; i < bytes.Length; i += 2)
                {
                    var low = i + 1 < bytes.Length ? bytes[i + 1] : 0;
                    cids[i / 2] = (char)(bytes[i] << 8 | low);
                }

                return new string(cids);
            }

            if (_simpleMap is { } map)
            {
                var chars = new char[bytes.Length];
                for (var i = 0; i < bytes.Length; i++)
                {
                    chars[i] = map[bytes[i]];
                }

                return new string(chars);
            }

            return Encoding.Latin1.GetString(bytes);
        }

        private static char GlyphNameToChar(string name)
        {
            if (name.Length == 1)
            {
                return name[0];
            }

            if (name.StartsWith("uni", StringComparison.Ordinal) && name.Length >= 7
                && int.TryParse(name.AsSpan(3, 4), System.Globalization.NumberStyles.HexNumber, null, out var uni))
            {
                return (char)uni;
            }

            return name switch
            {
                "space" => ' ',
                "period" => '.',
                "comma" => ',',
                "hyphen" => '-',
                "colon" => ':',
                "semicolon" => ';',
                "slash" => '/',
                "exclam" => '!',
                "question" => '?',
                "quotesingle" => '\'',
                "quotedbl" => '"',
                "parenleft" => '(',
                "parenright" => ')',
                "underscore" => '_',
                "percent" => '%',
                "ampersand" => '&',
                "dollar" => '$',
                "at" => '@',
                "plus" => '+',
                "equal" => '=',
                "asterisk" => '*',
                "numbersign" => '#',
                "zero" => '0',
                "one" => '1',
                "two" => '2',
                "three" => '3',
                "four" => '4',
                "five" => '5',
                "six" => '6',
                "seven" => '7',
                "eight" => '8',
                "nine" => '9',
                _ => '�' // unknown glyph: deliberately non-whitespace
            };
        }
    }

    private static bool TryGetFloats(List<CosObject> operands, int count, out float[] values)
    {
        values = new float[count];
        if (operands.Count < count)
        {
            return false;
        }

        for (var i = 0; i < count; i++)
        {
            if (operands[operands.Count - count + i] is not CosNumber number)
            {
                return false;
            }

            values[i] = number.FloatValue;
        }

        return true;
    }

    private sealed class GraphicsState
    {
        public PdfMatrix Ctm = PdfMatrix.Identity;
        public float LineWidth = 1f;
        public CosDictionary? Font;
        public float FontSize;
        public int TextRenderMode;
        public float Leading;

        public GraphicsState Clone() => (GraphicsState)MemberwiseClone();
    }

    private sealed class PathBuilder
    {
        private readonly List<PdfSubpath> _subpaths = new();
        private PdfSubpath? _current;

        public PdfPoint CurrentPoint => _current?.CurrentPoint ?? default;

        public void MoveTo(float x, float y)
        {
            _current = new PdfSubpath(new PdfPoint(x, y));
            _subpaths.Add(_current);
        }

        public void LineTo(float x, float y)
        {
            EnsureCurrent();
            _current!.AddLine(new PdfPoint(x, y));
        }

        public void CurveTo(PdfPoint control1, PdfPoint control2, PdfPoint end)
        {
            EnsureCurrent();
            _current!.AddCurve(control1, control2, end);
        }

        public void Close()
        {
            if (_current != null)
            {
                _current.IsClosed = true;
            }
        }

        public void Rectangle(float x, float y, float width, float height)
        {
            var rect = new PdfSubpath(new PdfPoint(x, y)) { IsClosed = true };
            rect.AddLine(new PdfPoint(x + width, y));
            rect.AddLine(new PdfPoint(x + width, y + height));
            rect.AddLine(new PdfPoint(x, y + height));
            rect.AddLine(new PdfPoint(x, y));
            _subpaths.Add(rect);
            _current = rect;
        }

        public PdfPath Build() => new(_subpaths.ToArray());

        private void EnsureCurrent()
        {
            if (_current == null)
            {
                MoveTo(0f, 0f);
            }
        }
    }
}
