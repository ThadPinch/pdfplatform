using System.Text;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;
using FrontEndSuite.PdfPlatform.Geometry;

namespace FrontEndSuite.PdfPlatform.Canvas;

public enum QrErrorCorrection
{
    L = 0,
    M = 1,
    Q = 2,
    H = 3
}

/// <summary>
/// QR code encoder (ISO 18004): numeric/alphanumeric/byte modes, versions 1-20, all four error
/// correction levels, mask chosen by the standard penalty rules. Output is a module matrix or a
/// form XObject with one unit per module (matching iText's BarcodeQRCode.CreateFormXObject).
/// </summary>
public sealed class QrCode
{
    private const string AlphanumericChars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ $%*+-./:";

    // Total codewords per version 1..40.
    private static readonly int[] TotalCodewords =
    {
        26, 44, 70, 100, 134, 172, 196, 242, 292, 346,
        404, 466, 532, 581, 655, 733, 815, 901, 991, 1085,
        1156, 1258, 1364, 1474, 1588, 1706, 1828, 1921, 2051, 2185,
        2323, 2465, 2611, 2761, 2876, 3034, 3196, 3362, 3532, 3706
    };

    // Per version 1..20 and EC level L/M/Q/H: (ecPerBlock, blocks1, data1, blocks2, data2).
    private static readonly (int Ec, int B1, int D1, int B2, int D2)[][] EcBlocks =
    {
        new[] { (7, 1, 19, 0, 0), (10, 1, 16, 0, 0), (13, 1, 13, 0, 0), (17, 1, 9, 0, 0) },
        new[] { (10, 1, 34, 0, 0), (16, 1, 28, 0, 0), (22, 1, 22, 0, 0), (28, 1, 16, 0, 0) },
        new[] { (15, 1, 55, 0, 0), (26, 1, 44, 0, 0), (18, 2, 17, 0, 0), (22, 2, 13, 0, 0) },
        new[] { (20, 1, 80, 0, 0), (18, 2, 32, 0, 0), (26, 2, 24, 0, 0), (16, 4, 9, 0, 0) },
        new[] { (26, 1, 108, 0, 0), (24, 2, 43, 0, 0), (18, 2, 15, 2, 16), (22, 2, 11, 2, 12) },
        new[] { (18, 2, 68, 0, 0), (16, 4, 27, 0, 0), (24, 4, 19, 0, 0), (28, 4, 15, 0, 0) },
        new[] { (20, 2, 78, 0, 0), (18, 4, 31, 0, 0), (18, 2, 14, 4, 15), (26, 4, 13, 1, 14) },
        new[] { (24, 2, 97, 0, 0), (22, 2, 38, 2, 39), (22, 4, 18, 2, 19), (26, 4, 14, 2, 15) },
        new[] { (30, 2, 116, 0, 0), (22, 3, 36, 2, 37), (20, 4, 16, 4, 17), (24, 4, 12, 4, 13) },
        new[] { (18, 2, 68, 2, 69), (26, 4, 43, 1, 44), (24, 6, 19, 2, 20), (28, 6, 15, 2, 16) },
        new[] { (20, 4, 81, 0, 0), (30, 1, 50, 4, 51), (28, 4, 22, 4, 23), (24, 3, 12, 8, 13) },
        new[] { (24, 2, 92, 2, 93), (22, 6, 36, 2, 37), (26, 4, 20, 6, 21), (28, 7, 14, 4, 15) },
        new[] { (26, 4, 107, 0, 0), (22, 8, 37, 1, 38), (24, 8, 20, 4, 21), (22, 12, 11, 4, 12) },
        new[] { (30, 3, 115, 1, 116), (24, 4, 40, 5, 41), (20, 11, 16, 5, 17), (24, 11, 12, 5, 13) },
        new[] { (22, 5, 87, 1, 88), (24, 5, 41, 5, 42), (30, 5, 24, 7, 25), (24, 11, 12, 7, 13) },
        new[] { (24, 5, 98, 1, 99), (28, 7, 45, 3, 46), (24, 15, 19, 2, 20), (30, 3, 15, 13, 16) },
        new[] { (28, 1, 107, 5, 108), (28, 10, 46, 1, 47), (28, 1, 22, 15, 23), (28, 2, 14, 17, 15) },
        new[] { (30, 5, 120, 1, 121), (26, 9, 43, 4, 44), (28, 17, 22, 1, 23), (28, 2, 14, 19, 15) },
        new[] { (28, 3, 113, 4, 114), (26, 3, 44, 11, 45), (26, 17, 21, 4, 22), (26, 9, 13, 16, 14) },
        new[] { (28, 3, 107, 5, 108), (26, 3, 41, 13, 42), (30, 15, 24, 5, 25), (28, 15, 15, 10, 16) },
        new[] { (28, 4, 116, 4, 117), (26, 17, 42, 0, 0), (28, 17, 22, 6, 23), (30, 19, 16, 6, 17) },
        new[] { (28, 2, 111, 7, 112), (28, 17, 46, 0, 0), (30, 7, 24, 16, 25), (24, 34, 13, 0, 0) },
        new[] { (30, 4, 121, 5, 122), (28, 4, 47, 14, 48), (30, 11, 24, 14, 25), (30, 16, 15, 14, 16) },
        new[] { (30, 6, 117, 4, 118), (28, 6, 45, 14, 46), (30, 11, 24, 16, 25), (30, 30, 16, 2, 17) },
        new[] { (26, 8, 106, 4, 107), (28, 8, 47, 13, 48), (30, 7, 24, 22, 25), (30, 22, 15, 13, 16) },
        new[] { (28, 10, 114, 2, 115), (28, 19, 46, 4, 47), (28, 28, 22, 6, 23), (30, 33, 16, 4, 17) },
        new[] { (30, 8, 122, 4, 123), (28, 22, 45, 3, 46), (30, 8, 23, 26, 24), (30, 12, 15, 28, 16) },
        new[] { (30, 3, 117, 10, 118), (28, 3, 45, 23, 46), (30, 4, 24, 31, 25), (30, 11, 15, 31, 16) },
        new[] { (30, 7, 116, 7, 117), (28, 21, 45, 7, 46), (30, 1, 23, 37, 24), (30, 19, 15, 26, 16) },
        new[] { (30, 5, 115, 10, 116), (28, 19, 47, 10, 48), (30, 15, 24, 25, 25), (30, 23, 15, 25, 16) },
        new[] { (30, 13, 115, 3, 116), (28, 2, 46, 29, 47), (30, 42, 24, 1, 25), (30, 23, 15, 28, 16) },
        new[] { (30, 17, 115, 0, 0), (28, 10, 46, 23, 47), (30, 10, 24, 35, 25), (30, 19, 15, 35, 16) },
        new[] { (30, 17, 115, 1, 116), (28, 14, 46, 21, 47), (30, 29, 24, 19, 25), (30, 11, 15, 46, 16) },
        new[] { (30, 13, 115, 6, 116), (28, 14, 46, 23, 47), (30, 44, 24, 7, 25), (30, 59, 16, 1, 17) },
        new[] { (30, 12, 121, 7, 122), (28, 12, 47, 26, 48), (30, 39, 24, 14, 25), (30, 22, 15, 41, 16) },
        new[] { (30, 6, 121, 14, 122), (28, 6, 47, 34, 48), (30, 46, 24, 10, 25), (30, 2, 15, 64, 16) },
        new[] { (30, 17, 122, 4, 123), (28, 29, 46, 14, 47), (30, 49, 24, 10, 25), (30, 24, 15, 46, 16) },
        new[] { (30, 4, 122, 18, 123), (28, 13, 46, 32, 47), (30, 48, 24, 14, 25), (30, 42, 15, 32, 16) },
        new[] { (30, 20, 117, 4, 118), (28, 40, 47, 7, 48), (30, 43, 24, 22, 25), (30, 10, 15, 67, 16) },
        new[] { (30, 19, 118, 6, 119), (28, 18, 47, 31, 48), (30, 34, 24, 34, 25), (30, 20, 15, 61, 16) }
    };

    static bool ValidateTables()
    {
        for (var v = 1; v <= 40; v++)
        {
            for (var level = 0; level < 4; level++)
            {
                var (ec, b1, d1, b2, d2) = EcBlocks[v - 1][level];
                if (b1 * d1 + b2 * d2 + (b1 + b2) * ec != TotalCodewords[v - 1])
                {
                    throw new InvalidOperationException($"QR EC table is inconsistent at version {v} level {level}.");
                }
            }
        }

        return true;
    }

    private static readonly bool TablesValidated = ValidateTables();

    private static readonly int[][] AlignmentPositions =
    {
        Array.Empty<int>(),
        new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
        new[] { 6, 30, 54 }, new[] { 6, 32, 58 }, new[] { 6, 34, 62 },
        new[] { 6, 26, 46, 66 }, new[] { 6, 26, 48, 70 }, new[] { 6, 26, 50, 74 },
        new[] { 6, 30, 54, 78 }, new[] { 6, 30, 56, 82 }, new[] { 6, 30, 58, 86 }, new[] { 6, 34, 62, 90 },
        new[] { 6, 28, 50, 72, 94 }, new[] { 6, 26, 50, 74, 98 }, new[] { 6, 30, 54, 78, 102 },
        new[] { 6, 28, 54, 80, 106 }, new[] { 6, 32, 58, 84, 110 }, new[] { 6, 30, 58, 86, 114 },
        new[] { 6, 34, 62, 90, 118 }, new[] { 6, 26, 50, 74, 98, 122 }, new[] { 6, 30, 54, 78, 102, 126 },
        new[] { 6, 26, 52, 78, 104, 130 }, new[] { 6, 30, 56, 82, 108, 134 }, new[] { 6, 34, 60, 86, 112, 138 },
        new[] { 6, 30, 58, 86, 114, 142 }, new[] { 6, 34, 62, 90, 118, 146 },
        new[] { 6, 30, 54, 78, 102, 126, 150 }, new[] { 6, 24, 50, 76, 102, 128, 154 },
        new[] { 6, 28, 54, 80, 106, 132, 158 }, new[] { 6, 32, 58, 84, 110, 136, 162 },
        new[] { 6, 26, 54, 82, 110, 138, 166 }, new[] { 6, 30, 58, 86, 114, 142, 170 }
    };

    static bool ValidateAlignmentPositions()
    {
        for (var v = 2; v <= 40; v++)
        {
            var positions = AlignmentPositions[v - 1];
            if (positions[^1] != 4 * v + 10)
            {
                throw new InvalidOperationException($"QR alignment table is inconsistent at version {v}.");
            }
        }

        return true;
    }

    private static readonly bool AlignmentValidated = ValidateAlignmentPositions();

    private QrCode(bool[,] modules, int size)
    {
        Modules = modules;
        Size = size;
    }

    /// <summary>Dark-module matrix; [row, column] with row 0 at the top.</summary>
    public bool[,] Modules { get; }

    public int Size { get; }

    public static QrCode Encode(string content, QrErrorCorrection level)
    {
        var mode = SelectMode(content);
        var version = SelectVersion(content, mode, level);
        var dataCodewords = BuildDataCodewords(content, mode, version, level);
        var allCodewords = InterleaveWithEc(dataCodewords, version, level);

        var size = version * 4 + 17;
        var matrix = new int[size, size]; // -1 unset, 0 light, 1 dark — use 2 for unset
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                matrix[r, c] = 2;
            }
        }

        DrawFunctionPatterns(matrix, size, version);

        // Try all masks, keep the one with the lowest penalty.
        bool[,]? best = null;
        var bestPenalty = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var candidate = PlaceData(matrix, size, allCodewords, mask);
            DrawFormatInfo(candidate, size, level, mask);
            if (version >= 7)
            {
                DrawVersionInfo(candidate, size, version);
            }

            var penalty = ComputePenalty(candidate, size);
            if (penalty < bestPenalty)
            {
                bestPenalty = penalty;
                best = candidate;
            }
        }

        return new QrCode(best!, size);
    }

    /// <summary>Size of the drawn form including the standard 4-module quiet zone per side.</summary>
    public int SizeWithQuietZone => Size + 2 * QuietZoneModules;

    private const int QuietZoneModules = 4;

    /// <summary>
    /// Draws the code as a form XObject, one unit per module, with the standard 4-module quiet
    /// zone included in the bounding box — matching iText's BarcodeQRCode.CreateFormXObject so
    /// bbox-based scaling behaves identically.
    /// </summary>
    public PdfFormXObject CreateFormXObject(PdfDocument document, float grayLevel = 0f)
    {
        var ops = new StringBuilder();
        ops.Append(grayLevel.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)).Append(" g\n");
        var full = SizeWithQuietZone;
        for (var row = 0; row < Size; row++)
        {
            for (var col = 0; col < Size; col++)
            {
                if (Modules[row, col])
                {
                    var runStart = col;
                    while (col < Size && Modules[row, col])
                    {
                        col++;
                    }

                    ops.Append($"{runStart + QuietZoneModules} {full - QuietZoneModules - row - 1} {col - runStart} 1 re\n");
                }
            }
        }

        ops.Append("f\n");
        var form = new PdfFormXObject(new PdfRect(0, 0, full, full), Encoding.ASCII.GetBytes(ops.ToString()));
        document.EnsureIndirect(form.Stream);
        return form;
    }

    // ---- Mode / version selection ----

    private enum Mode
    {
        Numeric,
        Alphanumeric,
        Byte
    }

    private static Mode SelectMode(string content)
    {
        if (content.Length > 0 && content.All(char.IsAsciiDigit))
        {
            return Mode.Numeric;
        }

        if (content.Length > 0 && content.All(c => AlphanumericChars.Contains(c)))
        {
            return Mode.Alphanumeric;
        }

        return Mode.Byte;
    }

    private static int CharCountBits(Mode mode, int version) => mode switch
    {
        Mode.Numeric => version <= 9 ? 10 : 12,
        Mode.Alphanumeric => version <= 9 ? 9 : 11,
        _ => version <= 9 ? 8 : 16
    };

    private static int SelectVersion(string content, Mode mode, QrErrorCorrection level)
    {
        // Match the zxing port's selection: the mode-encoded body rounded up to bytes, plus a
        // reserved 3 bytes for mode/length header. Occasionally one version larger than strictly
        // necessary — kept for parity with the iText-generated codes this replaces.
        var length = mode == Mode.Byte ? Encoding.Latin1.GetByteCount(content) : content.Length;
        var bodyBits = mode switch
        {
            Mode.Numeric => length / 3 * 10 + (length % 3 == 1 ? 4 : length % 3 == 2 ? 7 : 0),
            Mode.Alphanumeric => length / 2 * 11 + length % 2 * 6,
            _ => length * 8
        };
        var bodyBytes = (bodyBits + 7) / 8;

        for (var version = 1; version <= 40; version++)
        {
            if (DataCodewordCount(version, level) >= bodyBytes + 3)
            {
                return version;
            }
        }

        throw new InvalidOperationException("QR payload is too large (exceeds version 40 capacity).");
    }

    private static int DataCodewordCount(int version, QrErrorCorrection level)
    {
        var (ec, b1, d1, b2, d2) = EcBlocks[version - 1][(int)level];
        _ = ec;
        return b1 * d1 + b2 * d2;
    }

    // ---- Data encoding ----

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = new();
        private int _bitCount;

        public int BitCount => _bitCount;

        public void Write(int value, int bits)
        {
            for (var i = bits - 1; i >= 0; i--)
            {
                var bit = (value >> i) & 1;
                if (_bitCount % 8 == 0)
                {
                    _bytes.Add(0);
                }

                if (bit != 0)
                {
                    _bytes[^1] |= (byte)(0x80 >> (_bitCount % 8));
                }

                _bitCount++;
            }
        }

        public byte[] ToPaddedCodewords(int capacity)
        {
            // Terminator (up to 4 zero bits), pad to byte, then alternate pad codewords.
            var result = new List<byte>(_bytes);
            var bits = _bitCount;
            var terminator = Math.Min(4, capacity * 8 - bits);
            bits += terminator;
            while (result.Count < (bits + 7) / 8)
            {
                result.Add(0);
            }

            while (result.Count < capacity)
            {
                result.Add(0xEC);
                if (result.Count < capacity)
                {
                    result.Add(0x11);
                }
            }

            return result.Take(capacity).ToArray();
        }
    }

    private static byte[] BuildDataCodewords(string content, Mode mode, int version, QrErrorCorrection level)
    {
        var writer = new BitWriter();
        writer.Write(mode switch { Mode.Numeric => 1, Mode.Alphanumeric => 2, _ => 4 }, 4);

        if (mode == Mode.Byte)
        {
            var bytes = Encoding.Latin1.GetBytes(content);
            writer.Write(bytes.Length, CharCountBits(mode, version));
            foreach (var b in bytes)
            {
                writer.Write(b, 8);
            }
        }
        else if (mode == Mode.Numeric)
        {
            writer.Write(content.Length, CharCountBits(mode, version));
            for (var i = 0; i < content.Length; i += 3)
            {
                var chunk = content.Substring(i, Math.Min(3, content.Length - i));
                writer.Write(int.Parse(chunk), chunk.Length == 3 ? 10 : chunk.Length == 2 ? 7 : 4);
            }
        }
        else
        {
            writer.Write(content.Length, CharCountBits(mode, version));
            for (var i = 0; i < content.Length; i += 2)
            {
                var first = AlphanumericChars.IndexOf(content[i]);
                if (i + 1 < content.Length)
                {
                    writer.Write(first * 45 + AlphanumericChars.IndexOf(content[i + 1]), 11);
                }
                else
                {
                    writer.Write(first, 6);
                }
            }
        }

        return writer.ToPaddedCodewords(DataCodewordCount(version, level));
    }

    // ---- Reed-Solomon ----

    private static readonly byte[] GfExp = new byte[512];
    private static readonly byte[] GfLog = new byte[256];

    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            GfExp[i] = (byte)x;
            GfLog[x] = (byte)i;
            x <<= 1;
            if (x >= 256)
            {
                x ^= 0x11D;
            }
        }

        for (var i = 255; i < 512; i++)
        {
            GfExp[i] = GfExp[i - 255];
        }
    }

    /// <summary>Generator polynomial of the given degree, coefficients highest-degree first (g[0] = 1).</summary>
    private static byte[] RsGenerator(int degree)
    {
        // Multiply out (x - α^0)(x - α^1)...(x - α^(degree-1)).
        var poly = new byte[] { 1 };
        for (var d = 0; d < degree; d++)
        {
            var next = new byte[poly.Length + 1];
            for (var i = 0; i < poly.Length; i++)
            {
                next[i] ^= poly[i];                       // x * p
                next[i + 1] ^= GfMul(poly[i], GfExp[d]);  // α^d * p
            }

            poly = next;
        }

        return poly;
    }

    private static byte GfMul(byte a, byte b) =>
        a == 0 || b == 0 ? (byte)0 : GfExp[GfLog[a] + GfLog[b]];

    private static byte[] RsEncode(byte[] data, int ecCount)
    {
        var generator = RsGenerator(ecCount); // length ecCount + 1, generator[0] == 1
        var remainder = new byte[ecCount];
        foreach (var b in data)
        {
            var factor = (byte)(b ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecCount - 1);
            remainder[ecCount - 1] = 0;
            if (factor == 0)
            {
                continue;
            }

            for (var i = 0; i < ecCount; i++)
            {
                remainder[i] ^= GfMul(generator[i + 1], factor);
            }
        }

        return remainder;
    }

    private static byte[] InterleaveWithEc(byte[] data, int version, QrErrorCorrection level)
    {
        var (ecPerBlock, b1, d1, b2, d2) = EcBlocks[version - 1][(int)level];

        var blocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;
        for (var i = 0; i < b1; i++)
        {
            var block = data.Skip(offset).Take(d1).ToArray();
            offset += d1;
            blocks.Add(block);
            ecBlocks.Add(RsEncode(block, ecPerBlock));
        }

        for (var i = 0; i < b2; i++)
        {
            var block = data.Skip(offset).Take(d2).ToArray();
            offset += d2;
            blocks.Add(block);
            ecBlocks.Add(RsEncode(block, ecPerBlock));
        }

        var result = new List<byte>();
        var maxData = Math.Max(d1, d2);
        for (var i = 0; i < maxData; i++)
        {
            foreach (var block in blocks)
            {
                if (i < block.Length)
                {
                    result.Add(block[i]);
                }
            }
        }

        for (var i = 0; i < ecPerBlock; i++)
        {
            foreach (var ec in ecBlocks)
            {
                result.Add(ec[i]);
            }
        }

        return result.ToArray();
    }

    // ---- Matrix construction ----

    private static void DrawFunctionPatterns(int[,] matrix, int size, int version)
    {
        DrawFinder(matrix, 0, 0);
        DrawFinder(matrix, size - 7, 0);
        DrawFinder(matrix, 0, size - 7);

        // Separators
        for (var i = 0; i < 8; i++)
        {
            Set(matrix, 7, i, 0);
            Set(matrix, i, 7, 0);
            Set(matrix, 7, size - 1 - i, 0);
            Set(matrix, i, size - 8, 0);
            Set(matrix, size - 8, i, 0);
            Set(matrix, size - 1 - i, 7, 0);
        }

        // Timing
        for (var i = 8; i < size - 8; i++)
        {
            var value = i % 2 == 0 ? 1 : 0;
            Set(matrix, 6, i, value);
            Set(matrix, i, 6, value);
        }

        // Alignment patterns: all center combinations except the three finder corners.
        var positions = AlignmentPositions[version - 1];
        for (var i = 0; i < positions.Length; i++)
        {
            for (var j = 0; j < positions.Length; j++)
            {
                var isFinderCorner = (i == 0 && j == 0)
                                     || (i == 0 && j == positions.Length - 1)
                                     || (i == positions.Length - 1 && j == 0);
                if (isFinderCorner)
                {
                    continue;
                }

                var row = positions[i];
                var col = positions[j];
                for (var dr = -2; dr <= 2; dr++)
                {
                    for (var dc = -2; dc <= 2; dc++)
                    {
                        var dark = Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1;
                        Set(matrix, row + dr, col + dc, dark ? 1 : 0);
                    }
                }
            }
        }

        // Dark module + reserved format/version areas (marked 0; format overwritten later)
        Set(matrix, size - 8, 8, 1);
        for (var i = 0; i < 9; i++)
        {
            Reserve(matrix, 8, i);
            Reserve(matrix, i, 8);
        }

        for (var i = 0; i < 8; i++)
        {
            Reserve(matrix, 8, size - 1 - i);
            Reserve(matrix, size - 8 + i, 8);
        }

        if (version >= 7)
        {
            for (var i = 0; i < 6; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    Reserve(matrix, size - 11 + j, i);
                    Reserve(matrix, i, size - 11 + j);
                }
            }
        }
    }

    private static void DrawFinder(int[,] matrix, int row, int col)
    {
        for (var r = 0; r < 7; r++)
        {
            for (var c = 0; c < 7; c++)
            {
                var dark = r == 0 || r == 6 || c == 0 || c == 6 || (r is >= 2 and <= 4 && c is >= 2 and <= 4);
                matrix[row + r, col + c] = dark ? 1 : 0;
            }
        }
    }

    private static void Set(int[,] matrix, int row, int col, int value)
    {
        if (row >= 0 && col >= 0 && row < matrix.GetLength(0) && col < matrix.GetLength(1))
        {
            matrix[row, col] = value;
        }
    }

    private static void Reserve(int[,] matrix, int row, int col)
    {
        if (matrix[row, col] == 2)
        {
            matrix[row, col] = 0;
        }
    }

    private static bool[,] PlaceData(int[,] template, int size, byte[] codewords, int mask)
    {
        var result = new bool[size, size];
        var isFunction = new bool[size, size];
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (template[r, c] != 2)
                {
                    result[r, c] = template[r, c] == 1;
                    isFunction[r, c] = true;
                }
            }
        }

        var bitIndex = 0;
        var totalBits = codewords.Length * 8;
        var upward = true;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6)
            {
                right = 5; // skip the vertical timing column
            }

            for (var vert = 0; vert < size; vert++)
            {
                var row = upward ? size - 1 - vert : vert;
                for (var horizontal = 0; horizontal < 2; horizontal++)
                {
                    var col = right - horizontal;
                    if (isFunction[row, col])
                    {
                        continue;
                    }

                    var bit = false;
                    if (bitIndex < totalBits)
                    {
                        bit = (codewords[bitIndex / 8] >> (7 - bitIndex % 8) & 1) != 0;
                        bitIndex++;
                    }

                    if (MaskBit(mask, row, col))
                    {
                        bit = !bit;
                    }

                    result[row, col] = bit;
                }
            }

            upward = !upward;
        }

        return result;
    }

    private static bool MaskBit(int mask, int row, int col) => mask switch
    {
        0 => (row + col) % 2 == 0,
        1 => row % 2 == 0,
        2 => col % 3 == 0,
        3 => (row + col) % 3 == 0,
        4 => (row / 2 + col / 3) % 2 == 0,
        5 => row * col % 2 + row * col % 3 == 0,
        6 => (row * col % 2 + row * col % 3) % 2 == 0,
        _ => ((row + col) % 2 + row * col % 3) % 2 == 0
    };

    private static void DrawFormatInfo(bool[,] matrix, int size, QrErrorCorrection level, int mask)
    {
        var levelBits = level switch
        {
            QrErrorCorrection.L => 1,
            QrErrorCorrection.M => 0,
            QrErrorCorrection.Q => 3,
            _ => 2
        };

        var data = levelBits << 3 | mask;
        var rem = data;
        for (var i = 0; i < 10; i++)
        {
            rem = rem << 1 ^ (rem >> 9) * 0x537;
        }

        var bits = (data << 10 | rem) ^ 0x5412;

        // First copy, around the top-left finder: bits 0-5 go down column 8,
        // bits 6-8 turn the corner, bits 9-14 run left along row 8.
        for (var i = 0; i <= 5; i++)
        {
            matrix[i, 8] = GetBit(bits, i);
        }

        matrix[7, 8] = GetBit(bits, 6);
        matrix[8, 8] = GetBit(bits, 7);
        matrix[8, 7] = GetBit(bits, 8);
        for (var i = 9; i < 15; i++)
        {
            matrix[8, 14 - i] = GetBit(bits, i);
        }

        // Second copy: bits 0-7 right-to-left along row 8, bits 8-14 down column 8 at the bottom.
        for (var i = 0; i < 8; i++)
        {
            matrix[8, size - 1 - i] = GetBit(bits, i);
        }

        for (var i = 8; i < 15; i++)
        {
            matrix[size - 15 + i, 8] = GetBit(bits, i);
        }

        matrix[size - 8, 8] = true; // dark module
    }

    private static void DrawVersionInfo(bool[,] matrix, int size, int version)
    {
        var rem = version;
        for (var i = 0; i < 12; i++)
        {
            rem = rem << 1 ^ (rem >> 11) * 0x1F25;
        }

        var bits = version << 12 | rem;
        for (var i = 0; i < 18; i++)
        {
            var bit = GetBit(bits, i);
            matrix[size - 11 + i % 3, i / 3] = bit;
            matrix[i / 3, size - 11 + i % 3] = bit;
        }
    }

    private static bool GetBit(int value, int index) => (value >> index & 1) != 0;

    // ---- Penalty scoring (standard rules N1=3 N2=3 N3=40 N4=10) ----

    private static int ComputePenalty(bool[,] matrix, int size)
    {
        var penalty = 0;

        // Rule 1: runs of 5+ same-color modules
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < size; i++)
            {
                var runColor = false;
                var runLength = 0;
                for (var j = 0; j < size; j++)
                {
                    var value = pass == 0 ? matrix[i, j] : matrix[j, i];
                    if (j == 0 || value != runColor)
                    {
                        if (runLength >= 5)
                        {
                            penalty += 3 + (runLength - 5);
                        }

                        runColor = value;
                        runLength = 1;
                    }
                    else
                    {
                        runLength++;
                    }
                }

                if (runLength >= 5)
                {
                    penalty += 3 + (runLength - 5);
                }
            }
        }

        // Rule 2: 2x2 blocks of same color
        for (var r = 0; r < size - 1; r++)
        {
            for (var c = 0; c < size - 1; c++)
            {
                var v = matrix[r, c];
                if (v == matrix[r, c + 1] && v == matrix[r + 1, c] && v == matrix[r + 1, c + 1])
                {
                    penalty += 3;
                }
            }
        }

        // Rule 3 as the zxing port counts it: a 1011101 core with four light modules on either
        // side scores 40 once (not twice when both sides are light).
        for (var pass = 0; pass < 2; pass++)
        {
            for (var i = 0; i < size; i++)
            {
                for (var j = 0; j + 6 < size; j++)
                {
                    bool At(int k) => pass == 0 ? matrix[i, k] : matrix[k, i];
                    if (!(At(j) && !At(j + 1) && At(j + 2) && At(j + 3) && At(j + 4) && !At(j + 5) && At(j + 6)))
                    {
                        continue;
                    }

                    var lightBefore = j >= 4 && !At(j - 1) && !At(j - 2) && !At(j - 3) && !At(j - 4);
                    var lightAfter = j + 10 < size && !At(j + 7) && !At(j + 8) && !At(j + 9) && !At(j + 10);
                    if (lightBefore || lightAfter)
                    {
                        penalty += 40;
                    }
                }
            }
        }

        // Rule 4: dark module proportion
        var dark = 0;
        for (var r = 0; r < size; r++)
        {
            for (var c = 0; c < size; c++)
            {
                if (matrix[r, c])
                {
                    dark++;
                }
            }
        }

        // Rule 4 exactly as the (old) zxing port computes it, for mask-choice parity.
        var total = size * size;
        var darkRatio = (double)dark / total;
        penalty += Math.Abs((int)(darkRatio * 100 - 50)) / 5 * 10;
        return penalty;
    }
}
