using System.Text;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.IO;

namespace FrontEndSuite.PdfPlatform.Parsing;

/// <summary>
/// A parsed /ToUnicode CMap: maps character codes (1-4 bytes) to Unicode strings. Used to decode
/// show-operator strings the way a viewer would, so whitespace detection matches reality.
/// </summary>
public sealed class ToUnicodeCMap
{
    private const int MaxEntries = 100_000;

    private readonly Dictionary<(int Length, int Code), string> _map = new();
    private readonly SortedSet<int> _codeLengths = new();

    public static ToUnicodeCMap? Parse(byte[] cmapBytes)
    {
        try
        {
            var cmap = new ToUnicodeCMap();
            cmap.ParseCore(cmapBytes);
            return cmap._map.Count > 0 ? cmap : null;
        }
        catch
        {
            return null;
        }
    }

    public string Decode(byte[] bytes)
    {
        if (_codeLengths.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder(bytes.Length);
        var position = 0;
        var defaultLength = _codeLengths.Max;

        while (position < bytes.Length)
        {
            var matched = false;
            foreach (var length in _codeLengths)
            {
                if (position + length > bytes.Length)
                {
                    continue;
                }

                var code = ReadCode(bytes, position, length);
                if (_map.TryGetValue((length, code), out var text))
                {
                    sb.Append(text);
                    position += length;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                var advance = Math.Min(defaultLength, bytes.Length - position);
                sb.Append('�');
                position += Math.Max(1, advance);
            }
        }

        return sb.ToString();
    }

    private void ParseCore(byte[] data)
    {
        var parser = new CosObjectParser(new PdfLexer(data), null);
        var operands = new List<CosObject>();
        string? mode = null;

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
                mode = token.Text switch
                {
                    "beginbfchar" => "char",
                    "beginbfrange" => "range",
                    "begincodespacerange" => "codespace",
                    _ => null
                };
                operands.Clear();
                continue;
            }

            var obj = parser.ParseObject();
            if (mode == null)
            {
                continue;
            }

            operands.Add(obj);

            if (mode == "char" && operands.Count == 2)
            {
                if (operands[0] is CosString src && operands[1] is CosString dst)
                {
                    AddMapping(src.RawBytes, DecodeUtf16(dst.RawBytes));
                }

                operands.Clear();
            }
            else if (mode == "range" && operands.Count == 3)
            {
                if (operands[0] is CosString lo && operands[1] is CosString hi)
                {
                    AddRange(lo.RawBytes, hi.RawBytes, operands[2]);
                }

                operands.Clear();
            }
            else if (mode == "codespace" && operands.Count == 2)
            {
                if (operands[0] is CosString space && space.RawBytes.Length is >= 1 and <= 4)
                {
                    _codeLengths.Add(space.RawBytes.Length);
                }

                operands.Clear();
            }
        }
    }

    private void AddMapping(byte[] srcBytes, string text)
    {
        if (srcBytes.Length is < 1 or > 4 || _map.Count >= MaxEntries)
        {
            return;
        }

        _codeLengths.Add(srcBytes.Length);
        _map[(srcBytes.Length, ReadCode(srcBytes, 0, srcBytes.Length))] = text;
    }

    private void AddRange(byte[] loBytes, byte[] hiBytes, CosObject destination)
    {
        if (loBytes.Length is < 1 or > 4 || hiBytes.Length != loBytes.Length)
        {
            return;
        }

        var length = loBytes.Length;
        var lo = ReadCode(loBytes, 0, length);
        var hi = ReadCode(hiBytes, 0, length);
        if (hi < lo)
        {
            return;
        }

        var count = Math.Min(hi - lo, 65535);
        if (destination is CosArray targets)
        {
            for (var i = 0; i <= count && i < targets.Count && _map.Count < MaxEntries; i++)
            {
                if (targets.Get(i) is CosString target)
                {
                    _codeLengths.Add(length);
                    _map[(length, lo + i)] = DecodeUtf16(target.RawBytes);
                }
            }
        }
        else if (destination is CosString baseTarget)
        {
            var baseText = DecodeUtf16(baseTarget.RawBytes);
            if (baseText.Length == 0)
            {
                return;
            }

            for (var i = 0; i <= count && _map.Count < MaxEntries; i++)
            {
                // Ranges increment the final code unit, per the CMap convention.
                var text = i == 0
                    ? baseText
                    : baseText[..^1] + (char)(baseText[^1] + i);
                _codeLengths.Add(length);
                _map[(length, lo + i)] = text;
            }
        }
    }

    private static string DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length < 2)
        {
            return bytes.Length == 1 ? ((char)bytes[0]).ToString() : string.Empty;
        }

        return Encoding.BigEndianUnicode.GetString(bytes, 0, bytes.Length & ~1);
    }

    private static int ReadCode(byte[] bytes, int offset, int length)
    {
        var code = 0;
        for (var i = 0; i < length; i++)
        {
            code = code << 8 | bytes[offset + i];
        }

        return code;
    }
}
