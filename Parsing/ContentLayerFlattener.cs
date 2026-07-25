using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.IO;

namespace FrontEndSuite.PdfPlatform.Parsing;

/// <summary>
/// Rewrites a content stream to bake optional-content visibility: marked-content blocks
/// (BDC /OC ... EMC) belonging to hidden groups are removed entirely, and the BDC/EMC wrappers
/// of visible groups are stripped so the content can no longer be toggled off. Everything else
/// is copied byte-for-byte via source spans, so untouched content round-trips exactly.
/// </summary>
public static class ContentLayerFlattener
{
    /// <summary>
    /// <paramref name="isHidden"/> receives the resolved BDC properties (a dictionary, or
    /// whatever /Properties resolves to) and decides whether that block should be dropped.
    /// </summary>
    public static byte[] FlattenLayers(byte[] content, CosDictionary? resources, Func<CosObject?, bool> isHidden)
    {
        var lexer = new PdfLexer(content);
        var output = new MemoryStream(content.Length);

        var spanStart = 0;            // start of the pending source span (end of last handled operator)
        var operandStart = -1;        // position of the first operand token since the last operator
        var dropDepth = 0;            // > 0 while inside a hidden block (tracks nesting)
        var unwrapStack = new Stack<bool>(); // for visible blocks: does the matching EMC get dropped?

        void Emit(int from, int to)
        {
            if (dropDepth == 0 && to > from)
            {
                output.Write(content, from, to - from);
            }
        }

        while (true)
        {
            var token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                Emit(spanStart, content.Length);
                break;
            }

            if (token.Kind != PdfTokenKind.Keyword || token.Text is "true" or "false" or "null")
            {
                if (operandStart < 0)
                {
                    operandStart = token.Position;
                }

                continue;
            }

            var opEnd = lexer.Position;

            switch (token.Text)
            {
                case "BDC":
                case "BMC":
                {
                    if (dropDepth > 0)
                    {
                        dropDepth++;
                        break;
                    }

                    var (role, properties) = ParseMarkedContentOperands(content, operandStart, token.Position, resources);
                    if (CosNames.OC.Equals(role) && isHidden(properties))
                    {
                        // Hidden block: emit everything before it, then start dropping.
                        Emit(spanStart, operandStart < 0 ? token.Position : operandStart);
                        dropDepth = 1;
                    }
                    else if (CosNames.OC.Equals(role))
                    {
                        // Visible optional content: keep the content, strip the wrapper.
                        Emit(spanStart, operandStart < 0 ? token.Position : operandStart);
                        unwrapStack.Push(true);
                    }
                    else
                    {
                        // Unrelated marked content: keep as-is.
                        unwrapStack.Push(false);
                        Emit(spanStart, opEnd);
                    }

                    break;
                }

                case "EMC":
                {
                    if (dropDepth > 0)
                    {
                        dropDepth--;
                        if (dropDepth == 0)
                        {
                            // Everything up to and including this EMC is dropped.
                        }
                    }
                    else if (unwrapStack.Count > 0 && unwrapStack.Pop())
                    {
                        Emit(spanStart, operandStart < 0 ? token.Position : operandStart);
                        // EMC itself dropped.
                    }
                    else
                    {
                        Emit(spanStart, opEnd);
                    }

                    break;
                }

                case "BI":
                {
                    // Inline image: skip to its EI and treat BI..EI as one opaque span.
                    var end = SkipInlineImage(content, lexer);
                    Emit(spanStart, end);
                    opEnd = end;
                    break;
                }

                default:
                    Emit(spanStart, opEnd);
                    break;
            }

            spanStart = opEnd;
            operandStart = -1;
        }

        return output.ToArray();
    }

    private static (CosName? Role, CosObject? Properties) ParseMarkedContentOperands(
        byte[] content,
        int operandStart,
        int operatorPosition,
        CosDictionary? resources)
    {
        if (operandStart < 0 || operandStart >= operatorPosition)
        {
            return (null, null);
        }

        var parser = new CosObjectParser(new PdfLexer(content, operandStart), null);
        var operands = new List<CosObject>();
        while (parser.Peek().Position < operatorPosition && parser.Peek().Kind != PdfTokenKind.EndOfFile
               && operands.Count < 8)
        {
            operands.Add(parser.ParseObject());
        }

        var role = operands.Count > 0 ? operands[0] as CosName : null;
        CosObject? properties = null;
        if (operands.Count > 1)
        {
            properties = operands[1] switch
            {
                CosDictionary dict => dict,
                CosName propertyName => resources?.GetAsDictionary(CosNames.Properties)?.Get(propertyName),
                _ => null
            };
        }

        return (role, properties);
    }

    private static int SkipInlineImage(byte[] content, PdfLexer lexer)
    {
        // Consume dict tokens through ID, then scan for whitespace-delimited EI.
        while (true)
        {
            var token = lexer.NextToken();
            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                return content.Length;
            }

            if (token.IsKeyword("ID"))
            {
                break;
            }
        }

        var position = lexer.Position;
        if (position < content.Length && PdfLexer.IsWhitespace(content[position]))
        {
            position++;
        }

        for (var i = position; i + 1 < content.Length; i++)
        {
            if (content[i] == (byte)'E' && content[i + 1] == (byte)'I'
                && i > position && PdfLexer.IsWhitespace(content[i - 1])
                && (i + 2 >= content.Length || !PdfLexer.IsRegular(content[i + 2])))
            {
                lexer.Position = i + 2;
                return i + 2;
            }
        }

        lexer.Position = content.Length;
        return content.Length;
    }
}
