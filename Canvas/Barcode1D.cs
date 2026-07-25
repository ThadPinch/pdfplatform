using System.Text;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Canvas;

public enum BarcodeSymbology
{
    /// <summary>Full ASCII, automatic subset A/B/C selection with digit compaction.</summary>
    Code128,

    /// <summary>0-9 A-Z space - . $ / + %, 3:1 wide-to-narrow ratio, optional mod-43 check.</summary>
    Code39,

    /// <summary>12 or 13 digits; the check digit is computed when 12 are given.</summary>
    Ean13,

    /// <summary>11 or 12 digits; encoded as EAN-13 with a leading zero.</summary>
    UpcA,

    /// <summary>Digits only; zero-padded to an even count, 3:1 wide-to-narrow ratio.</summary>
    Interleaved2of5
}

/// <summary>
/// 1D barcode encoder for Code 128, Code 39, EAN-13/UPC-A, and Interleaved 2 of 5. Output is a
/// module array (one module = one narrow-bar width) or a form XObject with one unit per module
/// and the symbology's quiet zones included in the bounding box, matching how
/// <see cref="QrCode.CreateFormXObject"/> behaves so bbox-based scaling is identical.
/// </summary>
public sealed class Barcode1D
{
    // Code 128 patterns for values 0-105, plus the stop pattern at 106, as alternating
    // bar/space widths starting with a bar. Values 0-105 sum to 11 modules, stop to 13.
    private static readonly string[] Code128Patterns =
    {
        "212222", "222122", "222221", "121223", "121322", "131222", "122213", "122312",
        "132212", "221213", "221312", "231212", "112232", "122132", "122231", "113222",
        "123122", "123221", "223211", "221132", "221231", "213212", "223112", "312131",
        "311222", "321122", "321221", "312212", "322112", "322211", "212123", "212321",
        "232121", "111323", "131123", "131321", "112313", "132113", "132311", "211313",
        "231113", "231311", "112133", "112331", "132131", "113123", "113321", "133121",
        "313121", "211331", "231131", "213113", "213311", "213131", "311123", "311321",
        "331121", "312113", "312311", "332111", "314111", "221411", "431111", "111224",
        "111422", "121124", "121421", "141122", "141221", "112214", "112412", "122114",
        "122411", "142112", "142211", "241211", "221114", "413111", "241112", "134111",
        "111242", "121142", "121241", "114212", "124112", "124211", "411212", "421112",
        "421211", "212141", "214121", "412121", "111143", "111341", "131141", "114113",
        "114311", "411113", "411311", "113141", "114131", "311141", "411131", "211412",
        "211214", "211232", "2331112"
    };

    private const int Code128CodeC = 99;
    private const int Code128CodeB = 100;
    private const int Code128CodeA = 101;
    private const int Code128StartA = 103;
    private const int Code128StartB = 104;
    private const int Code128StartC = 105;
    private const int Code128Stop = 106;

    // Code 39: 9 elements per character (5 bars, 4 spaces, starting with a bar), '1' = wide.
    // Every pattern has exactly three wide elements ("3 of 9").
    private const string Code39Charset = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ-. $/+%";

    private static readonly string[] Code39Patterns =
    {
        "000110100", "100100001", "001100001", "101100000", "000110001", "100110000",
        "001110000", "000100101", "100100100", "001100100", "100001001", "001001001",
        "101001000", "000011001", "100011000", "001011000", "000001101", "100001100",
        "001001100", "000011100", "100000011", "001000011", "101000010", "000010011",
        "100010010", "001010010", "000000111", "100000110", "001000110", "000010110",
        "110000001", "011000001", "111000000", "010010001", "110010000", "011010000",
        "010000101", "110000100", "011000100", "010101000", "010100010", "010001010",
        "000101010"
    };

    private const string Code39StartStop = "010010100"; // '*'

    // EAN-13 left-hand odd-parity (L) patterns; G is the mirror, R is the complement of L.
    private static readonly string[] EanLPatterns =
    {
        "0001101", "0011001", "0010011", "0111101", "0100011",
        "0110001", "0101111", "0111011", "0110111", "0001011"
    };

    // Parity of the six left-hand digits, selected by the leading (13th) digit.
    private static readonly string[] EanParity =
    {
        "LLLLLL", "LLGLGG", "LLGGLG", "LLGGGL", "LGLLGG",
        "LGGLLG", "LGGGLL", "LGLGLG", "LGLGGL", "LGGLGL"
    };

    // Interleaved 2 of 5: 5 elements per digit, '1' = wide, exactly two wide per digit.
    private static readonly string[] ItfPatterns =
    {
        "00110", "10001", "01001", "11000", "00101",
        "10100", "01100", "00011", "10010", "01010"
    };

    static bool ValidateTables()
    {
        for (var i = 0; i < Code128Patterns.Length; i++)
        {
            var sum = 0;
            foreach (var ch in Code128Patterns[i])
            {
                sum += ch - '0';
            }

            if (sum != (i == Code128Stop ? 13 : 11))
            {
                throw new InvalidOperationException($"Code 128 pattern table is inconsistent at value {i}.");
            }
        }

        foreach (var pattern in Code39Patterns)
        {
            if (pattern.Length != 9 || pattern.Count(c => c == '1') != 3)
            {
                throw new InvalidOperationException("Code 39 pattern table is inconsistent.");
            }
        }

        foreach (var pattern in ItfPatterns)
        {
            if (pattern.Length != 5 || pattern.Count(c => c == '1') != 2)
            {
                throw new InvalidOperationException("Interleaved 2 of 5 pattern table is inconsistent.");
            }
        }

        foreach (var pattern in EanLPatterns)
        {
            if (pattern.Length != 7)
            {
                throw new InvalidOperationException("EAN pattern table is inconsistent.");
            }
        }

        return true;
    }

    private static readonly bool TablesValidated = ValidateTables();

    private readonly bool[] _modules;

    private Barcode1D(BarcodeSymbology symbology, string content, bool[] modules, int leftQuietZone, int rightQuietZone)
    {
        Symbology = symbology;
        Content = content;
        _modules = modules;
        LeftQuietZoneModules = leftQuietZone;
        RightQuietZoneModules = rightQuietZone;
    }

    public BarcodeSymbology Symbology { get; }

    /// <summary>The encoded value, including any check digit this encoder computed.</summary>
    public string Content { get; }

    /// <summary>Bar/space pattern; true = bar. One module = one narrow-bar width.</summary>
    public IReadOnlyList<bool> Modules => _modules;

    public int ModuleCount => _modules.Length;

    public int LeftQuietZoneModules { get; }

    public int RightQuietZoneModules { get; }

    /// <summary>Total drawn width in modules, quiet zones included.</summary>
    public int WidthWithQuietZones => LeftQuietZoneModules + ModuleCount + RightQuietZoneModules;

    /// <param name="content">The value to encode; see each symbology for accepted characters.</param>
    /// <param name="symbology">The barcode type to produce.</param>
    /// <param name="code39CheckDigit">Code 39 only: append the optional mod-43 check character.</param>
    public static Barcode1D Encode(string content, BarcodeSymbology symbology, bool code39CheckDigit = false)
    {
        if (string.IsNullOrEmpty(content))
        {
            throw new ArgumentException("Barcode content must not be empty.", nameof(content));
        }

        return symbology switch
        {
            BarcodeSymbology.Code128 => EncodeCode128(content),
            BarcodeSymbology.Code39 => EncodeCode39(content, code39CheckDigit),
            BarcodeSymbology.Ean13 => EncodeEan13(content),
            BarcodeSymbology.UpcA => EncodeUpcA(content),
            BarcodeSymbology.Interleaved2of5 => EncodeItf(content),
            _ => throw new ArgumentOutOfRangeException(nameof(symbology))
        };
    }

    /// <summary>
    /// Draws the barcode as a form XObject, one unit per module, with the symbology's quiet
    /// zones included in the bounding box. Scale to the final size when placing it; keep the
    /// horizontal scale an integer multiple of the intended narrow-bar width for clean rendering.
    /// </summary>
    /// <param name="heightModules">Bar height in module units (bar height is independent of bar width).</param>
    public PdfFormXObject CreateFormXObject(PdfDocument document, float heightModules = 50f, float grayLevel = 0f)
    {
        if (heightModules <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(heightModules));
        }

        var ops = new StringBuilder();
        ops.Append(grayLevel.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(" g\n");
        var height = heightModules.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < _modules.Length;)
        {
            if (!_modules[i])
            {
                i++;
                continue;
            }

            var runStart = i;
            while (i < _modules.Length && _modules[i])
            {
                i++;
            }

            ops.Append($"{LeftQuietZoneModules + runStart} 0 {i - runStart} {height} re\n");
        }

        ops.Append("f\n");
        var form = new PdfFormXObject(
            new PdfRect(0, 0, WidthWithQuietZones, heightModules),
            Encoding.ASCII.GetBytes(ops.ToString()));
        document.EnsureIndirect(form.Stream);
        return form;
    }

    // ---- Code 128 ----

    private static Barcode1D EncodeCode128(string content)
    {
        foreach (var c in content)
        {
            if (c > 127)
            {
                throw new ArgumentException($"Code 128 only encodes ASCII 0-127; found '{c}'.", nameof(content));
            }
        }

        var codewords = BuildCode128Codewords(content);

        var sum = codewords[0];
        for (var k = 1; k < codewords.Count; k++)
        {
            sum += codewords[k] * k;
        }

        codewords.Add(sum % 103);
        codewords.Add(Code128Stop);

        var modules = new List<bool>();
        foreach (var codeword in codewords)
        {
            AppendWidthPattern(modules, Code128Patterns[codeword]);
        }

        return new Barcode1D(BarcodeSymbology.Code128, content, modules.ToArray(), 10, 10);
    }

    private static List<int> BuildCode128Codewords(string content)
    {
        var codewords = new List<int>();
        var i = 0;
        char set;

        var startRun = DigitRunLength(content, 0);
        if (startRun >= 4 || (startRun == content.Length && startRun >= 2))
        {
            if (startRun % 2 == 1)
            {
                // Odd run: emit the first digit in subset B, then compact the even remainder.
                codewords.Add(Code128StartB);
                codewords.Add(content[0] - 32);
                i = 1;
                codewords.Add(Code128CodeC);
            }
            else
            {
                codewords.Add(Code128StartC);
            }

            set = 'C';
        }
        else if (content[0] < 32)
        {
            codewords.Add(Code128StartA);
            set = 'A';
        }
        else
        {
            codewords.Add(Code128StartB);
            set = 'B';
        }

        while (i < content.Length)
        {
            if (set == 'C')
            {
                if (i + 1 < content.Length && IsAsciiDigit(content[i]) && IsAsciiDigit(content[i + 1]))
                {
                    codewords.Add((content[i] - '0') * 10 + (content[i + 1] - '0'));
                    i += 2;
                    continue;
                }

                if (content[i] < 32)
                {
                    codewords.Add(Code128CodeA);
                    set = 'A';
                }
                else
                {
                    codewords.Add(Code128CodeB);
                    set = 'B';
                }

                continue;
            }

            var run = DigitRunLength(content, i);
            if (run >= 4 || (run >= 2 && i + run == content.Length))
            {
                if (run % 2 == 1)
                {
                    codewords.Add(content[i] - 32);
                    i++;
                }

                codewords.Add(Code128CodeC);
                set = 'C';
                continue;
            }

            var c = content[i];
            if (set == 'A')
            {
                if (c >= 96)
                {
                    codewords.Add(Code128CodeB);
                    set = 'B';
                    continue;
                }

                codewords.Add(c < 32 ? c + 64 : c - 32);
            }
            else
            {
                if (c < 32)
                {
                    codewords.Add(Code128CodeA);
                    set = 'A';
                    continue;
                }

                codewords.Add(c - 32);
            }

            i++;
        }

        return codewords;
    }

    private static int DigitRunLength(string content, int start)
    {
        var end = start;
        while (end < content.Length && IsAsciiDigit(content[end]))
        {
            end++;
        }

        return end - start;
    }

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    /// <summary>Appends alternating bar/space runs from a width-digit string, starting with a bar.</summary>
    private static void AppendWidthPattern(List<bool> modules, string widths)
    {
        var isBar = true;
        foreach (var w in widths)
        {
            for (var n = 0; n < w - '0'; n++)
            {
                modules.Add(isBar);
            }

            isBar = !isBar;
        }
    }

    // ---- Code 39 ----

    private static Barcode1D EncodeCode39(string content, bool addCheckDigit)
    {
        var normalized = content.ToUpperInvariant();
        foreach (var c in normalized)
        {
            if (Code39Charset.IndexOf(c) < 0)
            {
                throw new ArgumentException($"Code 39 cannot encode '{c}'. Allowed: {Code39Charset}", nameof(content));
            }
        }

        if (addCheckDigit)
        {
            var sum = normalized.Sum(c => Code39Charset.IndexOf(c));
            normalized += Code39Charset[sum % 43];
        }

        var modules = new List<bool>();
        AppendCode39Character(modules, Code39StartStop);
        foreach (var c in normalized)
        {
            modules.Add(false); // inter-character gap, one narrow space
            AppendCode39Character(modules, Code39Patterns[Code39Charset.IndexOf(c)]);
        }

        modules.Add(false);
        AppendCode39Character(modules, Code39StartStop);

        return new Barcode1D(BarcodeSymbology.Code39, normalized, modules.ToArray(), 10, 10);
    }

    private static void AppendCode39Character(List<bool> modules, string pattern)
    {
        for (var e = 0; e < 9; e++)
        {
            var isBar = e % 2 == 0;
            var width = pattern[e] == '1' ? 3 : 1;
            for (var n = 0; n < width; n++)
            {
                modules.Add(isBar);
            }
        }
    }

    // ---- EAN-13 / UPC-A ----

    private static Barcode1D EncodeUpcA(string content)
    {
        RequireDigits(content, "UPC-A");
        if (content.Length is not (11 or 12))
        {
            throw new ArgumentException("UPC-A requires 11 digits, or 12 with the check digit.", nameof(content));
        }

        var ean = EncodeEan13("0" + content);
        return new Barcode1D(BarcodeSymbology.UpcA, ean.Content[1..], ean._modules, 11, 7);
    }

    private static Barcode1D EncodeEan13(string content)
    {
        RequireDigits(content, "EAN-13");
        if (content.Length is not (12 or 13))
        {
            throw new ArgumentException("EAN-13 requires 12 digits, or 13 with the check digit.", nameof(content));
        }

        var check = Ean13CheckDigit(content[..12]);
        if (content.Length == 13 && content[12] - '0' != check)
        {
            throw new ArgumentException(
                $"EAN-13 check digit mismatch: expected {check}, got {content[12]}.", nameof(content));
        }

        var full = content[..12] + (char)('0' + check);
        var parity = EanParity[full[0] - '0'];

        var modules = new List<bool>();
        AppendBinaryPattern(modules, "101");
        for (var d = 1; d <= 6; d++)
        {
            var digit = full[d] - '0';
            var l = EanLPatterns[digit];
            AppendBinaryPattern(modules, parity[d - 1] == 'L' ? l : Mirror(Complement(l)));
        }

        AppendBinaryPattern(modules, "01010");
        for (var d = 7; d <= 12; d++)
        {
            AppendBinaryPattern(modules, Complement(EanLPatterns[full[d] - '0']));
        }

        AppendBinaryPattern(modules, "101");

        return new Barcode1D(BarcodeSymbology.Ean13, full, modules.ToArray(), 11, 7);
    }

    private static int Ean13CheckDigit(string twelveDigits)
    {
        var sum = 0;
        for (var d = 0; d < 12; d++)
        {
            sum += (twelveDigits[d] - '0') * (d % 2 == 0 ? 1 : 3);
        }

        return (10 - sum % 10) % 10;
    }

    private static string Complement(string bits)
    {
        var result = new char[bits.Length];
        for (var i = 0; i < bits.Length; i++)
        {
            result[i] = bits[i] == '0' ? '1' : '0';
        }

        return new string(result);
    }

    private static string Mirror(string bits)
    {
        var result = bits.ToCharArray();
        Array.Reverse(result);
        return new string(result);
    }

    private static void AppendBinaryPattern(List<bool> modules, string bits)
    {
        foreach (var bit in bits)
        {
            modules.Add(bit == '1');
        }
    }

    // ---- Interleaved 2 of 5 ----

    private static Barcode1D EncodeItf(string content)
    {
        RequireDigits(content, "Interleaved 2 of 5");
        var normalized = content.Length % 2 == 0 ? content : "0" + content;

        var modules = new List<bool> { true, false, true, false };
        for (var i = 0; i < normalized.Length; i += 2)
        {
            var bars = ItfPatterns[normalized[i] - '0'];
            var spaces = ItfPatterns[normalized[i + 1] - '0'];
            for (var e = 0; e < 5; e++)
            {
                AppendRun(modules, isBar: true, wide: bars[e] == '1');
                AppendRun(modules, isBar: false, wide: spaces[e] == '1');
            }
        }

        AppendRun(modules, isBar: true, wide: true);
        modules.Add(false);
        modules.Add(true);

        return new Barcode1D(BarcodeSymbology.Interleaved2of5, normalized, modules.ToArray(), 10, 10);
    }

    private static void AppendRun(List<bool> modules, bool isBar, bool wide)
    {
        for (var n = 0; n < (wide ? 3 : 1); n++)
        {
            modules.Add(isBar);
        }
    }

    private static void RequireDigits(string content, string symbologyName)
    {
        foreach (var c in content)
        {
            if (!IsAsciiDigit(c))
            {
                throw new ArgumentException($"{symbologyName} encodes digits only; found '{c}'.", nameof(content));
            }
        }
    }
}
