using System.Globalization;
using System.Text;

namespace FrontEndSuite.PdfPlatform.IO;

public enum PdfTokenKind
{
    EndOfFile,
    Integer,
    Real,
    Name,
    String,
    HexString,
    ArrayOpen,
    ArrayClose,
    DictOpen,
    DictClose,
    Keyword
}

public readonly struct PdfToken
{
    public PdfTokenKind Kind { get; init; }
    public long IntValue { get; init; }
    public double RealValue { get; init; }

    /// <summary>Name value (without slash) or keyword text.</summary>
    public string? Text { get; init; }

    /// <summary>Unescaped string bytes for String/HexString tokens.</summary>
    public byte[]? Bytes { get; init; }

    /// <summary>Byte offset of the token's first character in the source buffer.</summary>
    public int Position { get; init; }

    public bool IsKeyword(string text) => Kind == PdfTokenKind.Keyword && Text == text;

    public override string ToString() => $"{Kind} {Text ?? IntValue.ToString()}";
}

/// <summary>Tokenizer over a raw PDF byte buffer. Lenient: malformed input yields best-effort tokens.</summary>
public sealed class PdfLexer
{
    private readonly byte[] _data;
    private int _position;

    public PdfLexer(byte[] data, int start = 0)
    {
        _data = data;
        _position = Math.Clamp(start, 0, data.Length);
    }

    public byte[] Data => _data;

    public int Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _data.Length);
    }

    public static bool IsWhitespace(byte b) => b is 0 or 9 or 10 or 12 or 13 or 32;

    public static bool IsDelimiter(byte b) =>
        b is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    public static bool IsRegular(byte b) => !IsWhitespace(b) && !IsDelimiter(b);

    public PdfToken NextToken()
    {
        SkipWhitespaceAndComments();
        if (_position >= _data.Length)
        {
            return new PdfToken { Kind = PdfTokenKind.EndOfFile, Position = _data.Length };
        }

        var start = _position;
        var b = _data[_position];

        switch (b)
        {
            case (byte)'[':
                _position++;
                return new PdfToken { Kind = PdfTokenKind.ArrayOpen, Position = start };
            case (byte)']':
                _position++;
                return new PdfToken { Kind = PdfTokenKind.ArrayClose, Position = start };
            case (byte)'<':
                if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'<')
                {
                    _position += 2;
                    return new PdfToken { Kind = PdfTokenKind.DictOpen, Position = start };
                }

                return ReadHexString(start);
            case (byte)'>':
                if (_position + 1 < _data.Length && _data[_position + 1] == (byte)'>')
                {
                    _position += 2;
                    return new PdfToken { Kind = PdfTokenKind.DictClose, Position = start };
                }

                _position++;
                return NextToken();
            case (byte)'(':
                return ReadLiteralString(start);
            case (byte)')':
            case (byte)'{':
            case (byte)'}':
                _position++;
                return NextToken();
            case (byte)'/':
                return ReadName(start);
        }

        if (b is (byte)'+' or (byte)'-' or (byte)'.' || b is >= (byte)'0' and <= (byte)'9')
        {
            return ReadNumber(start);
        }

        return ReadKeyword(start);
    }

    private void SkipWhitespaceAndComments()
    {
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (IsWhitespace(b))
            {
                _position++;
            }
            else if (b == (byte)'%')
            {
                while (_position < _data.Length && _data[_position] != (byte)'\n' && _data[_position] != (byte)'\r')
                {
                    _position++;
                }
            }
            else
            {
                break;
            }
        }
    }

    private PdfToken ReadNumber(int start)
    {
        var sb = new StringBuilder();
        var isReal = false;
        while (_position < _data.Length)
        {
            var b = _data[_position];
            if (b is >= (byte)'0' and <= (byte)'9' || b is (byte)'+' or (byte)'-')
            {
                sb.Append((char)b);
                _position++;
            }
            else if (b == (byte)'.')
            {
                isReal = true;
                sb.Append('.');
                _position++;
            }
            else
            {
                break;
            }
        }

        var text = sb.ToString();
        if (!isReal && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var intValue))
        {
            return new PdfToken { Kind = PdfTokenKind.Integer, IntValue = intValue, RealValue = intValue, Position = start };
        }

        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var realValue);
        return new PdfToken { Kind = PdfTokenKind.Real, RealValue = realValue, IntValue = (long)realValue, Position = start };
    }

    private PdfToken ReadName(int start)
    {
        _position++; // consume '/'
        var bytes = new List<byte>(16);
        while (_position < _data.Length && IsRegular(_data[_position]))
        {
            var b = _data[_position];
            if (b == (byte)'#' && _position + 2 < _data.Length
                && TryHexDigit(_data[_position + 1], out var high)
                && TryHexDigit(_data[_position + 2], out var low))
            {
                bytes.Add((byte)(high << 4 | low));
                _position += 3;
            }
            else
            {
                bytes.Add(b);
                _position++;
            }
        }

        return new PdfToken
        {
            Kind = PdfTokenKind.Name,
            Text = Encoding.UTF8.GetString(bytes.ToArray()),
            Position = start
        };
    }

    private PdfToken ReadKeyword(int start)
    {
        var sb = new StringBuilder(8);
        while (_position < _data.Length && IsRegular(_data[_position]))
        {
            sb.Append((char)_data[_position]);
            _position++;
        }

        if (sb.Length == 0)
        {
            // Defensive: unknown delimiter byte; skip it so the lexer always makes progress.
            _position++;
            return NextToken();
        }

        return new PdfToken { Kind = PdfTokenKind.Keyword, Text = sb.ToString(), Position = start };
    }

    private PdfToken ReadLiteralString(int start)
    {
        _position++; // consume '('
        var bytes = new List<byte>(32);
        var depth = 1;

        while (_position < _data.Length)
        {
            var b = _data[_position++];
            if (b == (byte)'\\')
            {
                if (_position >= _data.Length)
                {
                    break;
                }

                var escape = _data[_position++];
                switch (escape)
                {
                    case (byte)'n':
                        bytes.Add((byte)'\n');
                        break;
                    case (byte)'r':
                        bytes.Add((byte)'\r');
                        break;
                    case (byte)'t':
                        bytes.Add((byte)'\t');
                        break;
                    case (byte)'b':
                        bytes.Add((byte)'\b');
                        break;
                    case (byte)'f':
                        bytes.Add((byte)'\f');
                        break;
                    case (byte)'\r':
                        // Line continuation; swallow an optional following \n.
                        if (_position < _data.Length && _data[_position] == (byte)'\n')
                        {
                            _position++;
                        }

                        break;
                    case (byte)'\n':
                        break;
                    default:
                        if (escape is >= (byte)'0' and <= (byte)'7')
                        {
                            var value = escape - (byte)'0';
                            for (var i = 0; i < 2 && _position < _data.Length; i++)
                            {
                                var digit = _data[_position];
                                if (digit is < (byte)'0' or > (byte)'7')
                                {
                                    break;
                                }

                                value = value * 8 + (digit - (byte)'0');
                                _position++;
                            }

                            bytes.Add((byte)value);
                        }
                        else
                        {
                            bytes.Add(escape);
                        }

                        break;
                }
            }
            else if (b == (byte)'(')
            {
                depth++;
                bytes.Add(b);
            }
            else if (b == (byte)')')
            {
                depth--;
                if (depth == 0)
                {
                    break;
                }

                bytes.Add(b);
            }
            else if (b == (byte)'\r')
            {
                // Any EOL inside a string is recorded as \n per spec.
                bytes.Add((byte)'\n');
                if (_position < _data.Length && _data[_position] == (byte)'\n')
                {
                    _position++;
                }
            }
            else
            {
                bytes.Add(b);
            }
        }

        return new PdfToken { Kind = PdfTokenKind.String, Bytes = bytes.ToArray(), Position = start };
    }

    private PdfToken ReadHexString(int start)
    {
        _position++; // consume '<'
        var bytes = new List<byte>(16);
        var haveNibble = false;
        var nibble = 0;

        while (_position < _data.Length)
        {
            var b = _data[_position++];
            if (b == (byte)'>')
            {
                break;
            }

            if (!TryHexDigit(b, out var value))
            {
                continue;
            }

            if (haveNibble)
            {
                bytes.Add((byte)(nibble << 4 | value));
                haveNibble = false;
            }
            else
            {
                nibble = value;
                haveNibble = true;
            }
        }

        if (haveNibble)
        {
            bytes.Add((byte)(nibble << 4));
        }

        return new PdfToken { Kind = PdfTokenKind.HexString, Bytes = bytes.ToArray(), Position = start };
    }

    private static bool TryHexDigit(byte b, out int value)
    {
        switch (b)
        {
            case >= (byte)'0' and <= (byte)'9':
                value = b - '0';
                return true;
            case >= (byte)'A' and <= (byte)'F':
                value = b - 'A' + 10;
                return true;
            case >= (byte)'a' and <= (byte)'f':
                value = b - 'a' + 10;
                return true;
            default:
                value = 0;
                return false;
        }
    }
}
