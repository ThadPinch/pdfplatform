using System.Text;

namespace FrontEndSuite.PdfPlatform.Cos;

public sealed class CosString : CosObject
{
    private static readonly char[] PdfDocMap = BuildPdfDocMap();

    public CosString(byte[] rawBytes, bool isHex)
    {
        RawBytes = rawBytes;
        IsHex = isHex;
    }

    public CosString(string text)
    {
        IsHex = false;
        RawBytes = EncodeText(text);
    }

    /// <summary>The string bytes exactly as stored in the file (after literal/hex unescaping).</summary>
    public byte[] RawBytes { get; }

    public bool IsHex { get; }

    /// <summary>Decoded text: UTF-16BE or UTF-8 when a BOM is present, PDFDocEncoding otherwise.</summary>
    public string Text
    {
        get
        {
            var b = RawBytes;
            if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(b, 2, b.Length - 2);
            }

            if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF)
            {
                return Encoding.UTF8.GetString(b, 3, b.Length - 3);
            }

            var chars = new char[b.Length];
            for (var i = 0; i < b.Length; i++)
            {
                chars[i] = PdfDocMap[b[i]];
            }

            return new string(chars);
        }
    }

    public override string ToString() => Text;

    private static readonly Lazy<Dictionary<char, byte>> PdfDocReverseMap = new(() =>
    {
        var map = new Dictionary<char, byte>();
        for (var i = 255; i >= 0; i--)
        {
            map[PdfDocMap[i]] = (byte)i; // lowest byte wins for duplicate chars
        }

        return map;
    });

    private static byte[] EncodeText(string text)
    {
        // Prefer PDFDocEncoding (single byte per char) and fall back to UTF-16BE with a BOM.
        var reverse = PdfDocReverseMap.Value;
        var docBytes = new byte[text.Length];
        var docEncodable = true;
        for (var i = 0; i < text.Length; i++)
        {
            if (!reverse.TryGetValue(text[i], out docBytes[i]))
            {
                docEncodable = false;
                break;
            }
        }

        if (docEncodable)
        {
            return docBytes;
        }

        var utf16 = Encoding.BigEndianUnicode.GetBytes(text);
        var result = new byte[utf16.Length + 2];
        result[0] = 0xFE;
        result[1] = 0xFF;
        Array.Copy(utf16, 0, result, 2, utf16.Length);
        return result;
    }

    private static char[] BuildPdfDocMap()
    {
        var map = new char[256];
        for (var i = 0; i < 256; i++)
        {
            map[i] = (char)i;
        }

        map[0x18] = '˘'; // breve
        map[0x19] = 'ˇ'; // caron
        map[0x1A] = 'ˆ'; // circumflex
        map[0x1B] = '˙'; // dot accent
        map[0x1C] = '˝'; // double acute
        map[0x1D] = '˛'; // ogonek
        map[0x1E] = '˚'; // ring
        map[0x1F] = '˜'; // small tilde
        map[0x80] = '•'; // bullet
        map[0x81] = '†'; // dagger
        map[0x82] = '‡'; // double dagger
        map[0x83] = '…'; // ellipsis
        map[0x84] = '—'; // em dash
        map[0x85] = '–'; // en dash
        map[0x86] = 'ƒ'; // florin
        map[0x87] = '⁄'; // fraction slash
        map[0x88] = '‹'; // single guillemet left
        map[0x89] = '›'; // single guillemet right
        map[0x8A] = '−'; // minus
        map[0x8B] = '‰'; // per mille
        map[0x8C] = '„'; // low double quote
        map[0x8D] = '“'; // left double quote
        map[0x8E] = '”'; // right double quote
        map[0x8F] = '‘'; // left single quote
        map[0x90] = '’'; // right single quote
        map[0x91] = '‚'; // low single quote
        map[0x92] = '™'; // trademark
        map[0x93] = 'ﬁ'; // fi ligature
        map[0x94] = 'ﬂ'; // fl ligature
        map[0x95] = 'Ł'; // Lslash
        map[0x96] = 'Œ'; // OE
        map[0x97] = 'Š'; // Scaron
        map[0x98] = 'Ÿ'; // Ydieresis
        map[0x99] = 'Ž'; // Zcaron
        map[0x9A] = 'ı'; // dotless i
        map[0x9B] = 'ł'; // lslash
        map[0x9C] = 'œ'; // oe
        map[0x9D] = 'š'; // scaron
        map[0x9E] = 'ž'; // zcaron
        map[0xA0] = '€'; // euro
        return map;
    }
}
