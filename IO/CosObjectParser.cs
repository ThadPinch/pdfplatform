using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.IO;

/// <summary>
/// Recursive-descent parser producing COS objects from a token stream. Lenient by design:
/// malformed constructs terminate the enclosing container instead of throwing.
/// </summary>
public sealed class CosObjectParser
{
    private const int MaxDepth = 200;

    private readonly PdfLexer _lexer;
    private readonly ICosResolver? _resolver;
    private readonly List<PdfToken> _lookahead = new();

    public CosObjectParser(PdfLexer lexer, ICosResolver? resolver)
    {
        _lexer = lexer;
        _resolver = resolver;
    }

    /// <summary>The underlying lexer, for callers that need raw positioning (inline image data).</summary>
    internal PdfLexer Lexer => _lexer;

    /// <summary>Drops buffered lookahead after the lexer position was moved externally.</summary>
    internal void DiscardLookahead() => _lookahead.Clear();

    public PdfToken Peek(int ahead = 0)
    {
        while (_lookahead.Count <= ahead)
        {
            _lookahead.Add(_lexer.NextToken());
        }

        return _lookahead[ahead];
    }

    public PdfToken Next()
    {
        var token = Peek();
        if (token.Kind != PdfTokenKind.EndOfFile)
        {
            _lookahead.RemoveAt(0);
        }

        return token;
    }

    public CosObject ParseObject(int depth = 0)
    {
        if (depth > MaxDepth)
        {
            Next();
            return CosNull.Instance;
        }

        var token = Peek();
        switch (token.Kind)
        {
            case PdfTokenKind.Integer:
                if (Peek(1).Kind == PdfTokenKind.Integer && Peek(2).IsKeyword("R"))
                {
                    var numberToken = Next();
                    var generationToken = Next();
                    Next(); // R
                    return new CosIndirectReference((int)numberToken.IntValue, (int)generationToken.IntValue)
                    {
                        Resolver = _resolver
                    };
                }

                Next();
                return new CosNumber(token.IntValue);

            case PdfTokenKind.Real:
                Next();
                return new CosNumber(token.RealValue);

            case PdfTokenKind.Name:
                Next();
                return new CosName(token.Text!);

            case PdfTokenKind.String:
                Next();
                return new CosString(token.Bytes!, isHex: false);

            case PdfTokenKind.HexString:
                Next();
                return new CosString(token.Bytes!, isHex: true);

            case PdfTokenKind.ArrayOpen:
                return ParseArray(depth);

            case PdfTokenKind.DictOpen:
                return ParseDictionary(depth);

            case PdfTokenKind.Keyword:
                Next();
                return token.Text switch
                {
                    "true" => CosBoolean.True,
                    "false" => CosBoolean.False,
                    _ => CosNull.Instance
                };

            case PdfTokenKind.EndOfFile:
                return CosNull.Instance;

            default:
                // Stray closer; consume and treat as null.
                Next();
                return CosNull.Instance;
        }
    }

    private CosArray ParseArray(int depth)
    {
        Next(); // [
        var array = new CosArray();
        while (true)
        {
            var token = Peek();
            if (token.Kind == PdfTokenKind.ArrayClose)
            {
                Next();
                break;
            }

            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Kind == PdfTokenKind.Keyword && token.Text is not ("true" or "false" or "null"))
            {
                // Malformed array running into obj/endobj/stream; stop without consuming.
                break;
            }

            if (token.Kind == PdfTokenKind.DictClose)
            {
                Next();
                continue;
            }

            array.Add(ParseObject(depth + 1));
        }

        return array;
    }

    private CosDictionary ParseDictionary(int depth)
    {
        Next(); // <<
        var dictionary = new CosDictionary();
        while (true)
        {
            var token = Peek();
            if (token.Kind == PdfTokenKind.DictClose)
            {
                Next();
                break;
            }

            if (token.Kind == PdfTokenKind.EndOfFile)
            {
                break;
            }

            if (token.Kind == PdfTokenKind.Name)
            {
                var key = new CosName(Next().Text!);
                var value = ParseObject(depth + 1);
                dictionary.Put(key, value);
            }
            else if (token.Kind == PdfTokenKind.Keyword)
            {
                // Malformed dictionary running into obj/endobj/stream; stop without consuming.
                break;
            }
            else
            {
                Next();
            }
        }

        return dictionary;
    }
}
