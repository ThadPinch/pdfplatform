using System.Buffers.Binary;
using System.Text;
using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Document;

namespace FrontEndSuite.PdfPlatform.Fonts;

/// <summary>
/// An embedded TrueType or OpenType font, written as a Type0/CIDFontType2 (or CFF-based
/// OpenType) composite font with Identity-H encoding. The full font file is embedded
/// (no subsetting); the serializer flate-compresses it at save time.
/// </summary>
public sealed class TrueTypeFont : PdfFont
{
    private readonly byte[] _fontBytes;
    private readonly bool _isCff;
    private readonly int _unitsPerEm;
    private readonly ushort[] _advanceWidths;
    private readonly Dictionary<int, int> _charToGlyph;
    private readonly int _numGlyphs;
    private readonly (int XMin, int YMin, int XMax, int YMax) _bbox;
    private readonly float _italicAngle;
    private readonly int _capHeight;
    private readonly bool _isFixedPitch;
    private readonly int _winAscent;
    private readonly int _winDescent;

    private TrueTypeFont(
        byte[] fontBytes,
        bool isCff,
        string name,
        int unitsPerEm,
        ushort[] advanceWidths,
        Dictionary<int, int> charToGlyph,
        int numGlyphs,
        int ascender,
        int descender,
        (int, int, int, int) bbox,
        float italicAngle,
        int capHeight,
        bool isFixedPitch,
        int winAscent,
        int winDescent)
    {
        _winAscent = winAscent;
        _winDescent = winDescent;
        _fontBytes = fontBytes;
        _isCff = isCff;
        Name = name;
        _unitsPerEm = unitsPerEm;
        _advanceWidths = advanceWidths;
        _charToGlyph = charToGlyph;
        _numGlyphs = numGlyphs;
        Ascender = Scale(ascender);
        Descender = Scale(descender);
        _bbox = bbox;
        _italicAngle = italicAngle;
        _capHeight = capHeight;
        _isFixedPitch = isFixedPitch;
    }

    public override string Name { get; }

    public override int Ascender { get; }

    public override int Descender { get; }

    /// <summary>iText lays out TrueType text with the Windows metrics, not typo × 1.2.</summary>
    public override float LayoutAscender => _winAscent > 0 ? Scale(_winAscent) : Ascender * 1.2f;

    public override float LayoutDescender => _winDescent > 0 ? -Scale(_winDescent) : Descender * 1.2f;

    public static TrueTypeFont Parse(byte[] fontBytes)
    {
        var span = fontBytes.AsSpan();
        if (span.Length < 12)
        {
            throw new InvalidDataException("Font file is too small.");
        }

        var version = BinaryPrimitives.ReadUInt32BigEndian(span);
        var isCff = version == 0x4F54544F; // 'OTTO'
        if (version is not (0x00010000 or 0x4F54544F or 0x74727565))
        {
            throw new InvalidDataException("Not a TrueType or OpenType font (bad sfnt version).");
        }

        var numTables = BinaryPrimitives.ReadUInt16BigEndian(span[4..]);
        var tables = new Dictionary<string, (int Offset, int Length)>();
        for (var i = 0; i < numTables; i++)
        {
            var record = span[(12 + i * 16)..];
            var tag = Encoding.ASCII.GetString(record[..4]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(record[12..]);
            tables[tag] = (offset, length);
        }

        if (!tables.TryGetValue("head", out var head) || !tables.TryGetValue("hhea", out var hhea)
            || !tables.TryGetValue("hmtx", out var hmtx) || !tables.TryGetValue("maxp", out var maxp)
            || !tables.TryGetValue("cmap", out var cmap))
        {
            throw new InvalidDataException("Font is missing required tables (head/hhea/hmtx/maxp/cmap).");
        }

        var unitsPerEm = BinaryPrimitives.ReadUInt16BigEndian(span[(head.Offset + 18)..]);
        var xMin = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 36)..]);
        var yMin = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 38)..]);
        var xMax = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 40)..]);
        var yMax = BinaryPrimitives.ReadInt16BigEndian(span[(head.Offset + 42)..]);

        var hheaAscender = (int)BinaryPrimitives.ReadInt16BigEndian(span[(hhea.Offset + 4)..]);
        var hheaDescender = (int)BinaryPrimitives.ReadInt16BigEndian(span[(hhea.Offset + 6)..]);
        var numberOfHMetrics = BinaryPrimitives.ReadUInt16BigEndian(span[(hhea.Offset + 34)..]);
        var numGlyphs = BinaryPrimitives.ReadUInt16BigEndian(span[(maxp.Offset + 4)..]);

        var advances = new ushort[numGlyphs];
        ushort last = 0;
        for (var g = 0; g < numGlyphs; g++)
        {
            if (g < numberOfHMetrics)
            {
                last = BinaryPrimitives.ReadUInt16BigEndian(span[(hmtx.Offset + g * 4)..]);
            }

            advances[g] = last;
        }

        var charToGlyph = ParseCmap(span, cmap.Offset);

        var ascender = hheaAscender;
        var descender = hheaDescender;
        var capHeight = 0;
        var isFixedPitch = false;
        float italicAngle = 0;
        var winAscent = 0;
        var winDescent = 0;

        if (tables.TryGetValue("OS/2", out var os2) && os2.Length >= 78)
        {
            var os2Version = BinaryPrimitives.ReadUInt16BigEndian(span[os2.Offset..]);
            ascender = BinaryPrimitives.ReadInt16BigEndian(span[(os2.Offset + 68)..]);
            descender = BinaryPrimitives.ReadInt16BigEndian(span[(os2.Offset + 70)..]);
            winAscent = BinaryPrimitives.ReadUInt16BigEndian(span[(os2.Offset + 74)..]);
            winDescent = BinaryPrimitives.ReadUInt16BigEndian(span[(os2.Offset + 76)..]);
            if (os2Version >= 2 && os2.Length >= 90)
            {
                capHeight = BinaryPrimitives.ReadInt16BigEndian(span[(os2.Offset + 88)..]);
            }
        }

        if (tables.TryGetValue("post", out var post) && post.Length >= 16)
        {
            var angleFixed = BinaryPrimitives.ReadInt32BigEndian(span[(post.Offset + 4)..]);
            italicAngle = angleFixed / 65536f;
            isFixedPitch = BinaryPrimitives.ReadUInt32BigEndian(span[(post.Offset + 12)..]) != 0;
        }

        var name = ReadPostScriptName(span, tables) ?? "EmbeddedFont";
        if (capHeight == 0)
        {
            capHeight = ascender;
        }

        return new TrueTypeFont(
            fontBytes, isCff, name, unitsPerEm, advances, charToGlyph, numGlyphs,
            ascender, descender, (xMin, yMin, xMax, yMax), italicAngle, capHeight, isFixedPitch,
            winAscent, winDescent);
    }

    public override float GetWidth(string text, float fontSize)
    {
        long total = 0;
        foreach (var c in text)
        {
            total += AdvanceOf(c);
        }

        return total * fontSize / 1000f;
    }

    internal override CosString EncodeText(string text)
    {
        var bytes = new byte[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            var glyph = _charToGlyph.TryGetValue(text[i], out var g) ? g : 0;
            bytes[i * 2] = (byte)(glyph >> 8);
            bytes[i * 2 + 1] = (byte)glyph;
        }

        return new CosString(bytes, isHex: true);
    }

    protected override CosObject BuildDictionary(PdfDocument document)
    {
        // Font program stream
        var fontFile = new CosStream();
        fontFile.SetData(_fontBytes);
        if (_isCff)
        {
            fontFile.Put(CosNames.Subtype, new CosName("OpenType"));
        }
        else
        {
            fontFile.Put(new CosName("Length1"), new CosNumber(_fontBytes.Length));
        }

        var fontFileRef = document.AddObject(fontFile);

        // Descriptor
        var descriptor = new CosDictionary();
        descriptor.Put(CosNames.Type, CosNames.FontDescriptor);
        descriptor.Put(new CosName("FontName"), new CosName(Name));
        var flags = 4; // symbolic
        if (_isFixedPitch)
        {
            flags |= 1;
        }

        if (_italicAngle != 0)
        {
            flags |= 64;
        }

        descriptor.Put(new CosName("Flags"), new CosNumber(flags));
        descriptor.Put(new CosName("FontBBox"), new CosArray(new CosObject[]
        {
            new CosNumber(Scale(_bbox.XMin)), new CosNumber(Scale(_bbox.YMin)),
            new CosNumber(Scale(_bbox.XMax)), new CosNumber(Scale(_bbox.YMax))
        }));
        descriptor.Put(new CosName("ItalicAngle"), new CosNumber((double)_italicAngle));
        descriptor.Put(new CosName("Ascent"), new CosNumber(Ascender));
        descriptor.Put(new CosName("Descent"), new CosNumber(Descender));
        descriptor.Put(new CosName("CapHeight"), new CosNumber(Scale(_capHeight)));
        descriptor.Put(new CosName("StemV"), new CosNumber(80));
        descriptor.Put(_isCff ? CosNames.FontFile3 : CosNames.FontFile2, fontFileRef);
        var descriptorRef = document.AddObject(descriptor);

        // CIDFont (CIDs are glyph ids: Identity mapping)
        var cidFont = new CosDictionary();
        cidFont.Put(CosNames.Type, CosNames.Font);
        cidFont.Put(CosNames.Subtype, new CosName(_isCff ? "CIDFontType0" : "CIDFontType2"));
        cidFont.Put(CosNames.BaseFont, new CosName(Name));
        var systemInfo = new CosDictionary();
        systemInfo.Put(new CosName("Registry"), new CosString("Adobe"));
        systemInfo.Put(new CosName("Ordering"), new CosString("Identity"));
        systemInfo.Put(new CosName("Supplement"), new CosNumber(0));
        cidFont.Put(new CosName("CIDSystemInfo"), systemInfo);
        cidFont.Put(CosNames.FontDescriptor, descriptorRef);
        cidFont.Put(new CosName("DW"), new CosNumber(1000));
        cidFont.Put(CosNames.W, BuildWidthsArray());
        if (!_isCff)
        {
            cidFont.Put(new CosName("CIDToGIDMap"), new CosName("Identity"));
        }

        var cidFontRef = document.AddObject(cidFont);

        // ToUnicode CMap so text remains extractable
        var toUnicode = new CosStream();
        toUnicode.SetData(BuildToUnicode());
        var toUnicodeRef = document.AddObject(toUnicode);

        var type0 = new CosDictionary();
        type0.Put(CosNames.Type, CosNames.Font);
        type0.Put(CosNames.Subtype, new CosName("Type0"));
        type0.Put(CosNames.BaseFont, new CosName(Name));
        type0.Put(CosNames.Encoding, new CosName("Identity-H"));
        type0.Put(new CosName("DescendantFonts"), new CosArray(new CosObject[] { cidFontRef }));
        type0.Put(CosNames.ToUnicode, toUnicodeRef);
        return type0;
    }

    private int AdvanceOf(char c)
    {
        var glyph = _charToGlyph.TryGetValue(c, out var g) ? g : 0;
        return glyph < _advanceWidths.Length ? Scale(_advanceWidths[glyph]) : 0;
    }

    private int Scale(int value) => (int)Math.Round(value * 1000.0 / _unitsPerEm);

    private CosArray BuildWidthsArray()
    {
        // W format: startCid [w w w ...] — one run covering all glyphs.
        var widths = new CosArray();
        for (var g = 0; g < _numGlyphs; g++)
        {
            widths.Add(new CosNumber(Scale(_advanceWidths[g])));
        }

        var result = new CosArray();
        result.Add(new CosNumber(0));
        result.Add(widths);
        return result;
    }

    private byte[] BuildToUnicode()
    {
        // Reverse the cmap: glyph -> unicode (first mapping wins).
        var glyphToUnicode = new Dictionary<int, int>();
        foreach (var (unicode, glyph) in _charToGlyph)
        {
            glyphToUnicode.TryAdd(glyph, unicode);
        }

        var sb = new StringBuilder();
        sb.AppendLine("/CIDInit /ProcSet findresource begin");
        sb.AppendLine("12 dict begin");
        sb.AppendLine("begincmap");
        sb.AppendLine("/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def");
        sb.AppendLine("/CMapName /Adobe-Identity-UCS def");
        sb.AppendLine("/CMapType 2 def");
        sb.AppendLine("1 begincodespacerange");
        sb.AppendLine("<0000> <FFFF>");
        sb.AppendLine("endcodespacerange");

        var entries = glyphToUnicode.OrderBy(kv => kv.Key).ToList();
        for (var i = 0; i < entries.Count; i += 100)
        {
            var chunk = entries.Skip(i).Take(100).ToList();
            sb.AppendLine($"{chunk.Count} beginbfchar");
            foreach (var (glyph, unicode) in chunk)
            {
                sb.AppendLine($"<{glyph:X4}> <{unicode:X4}>");
            }

            sb.AppendLine("endbfchar");
        }

        sb.AppendLine("endcmap");
        sb.AppendLine("CMapName currentdict /CMap defineresource pop");
        sb.AppendLine("end");
        sb.AppendLine("end");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    // ---- cmap parsing ----

    private static Dictionary<int, int> ParseCmap(ReadOnlySpan<byte> font, int cmapOffset)
    {
        var numSubtables = BinaryPrimitives.ReadUInt16BigEndian(font[(cmapOffset + 2)..]);
        var best = -1;
        var bestScore = -1;
        for (var i = 0; i < numSubtables; i++)
        {
            var record = font[(cmapOffset + 4 + i * 8)..];
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var encoding = BinaryPrimitives.ReadUInt16BigEndian(record[2..]);
            var offset = (int)BinaryPrimitives.ReadUInt32BigEndian(record[4..]);

            var score = (platform, encoding) switch
            {
                (3, 10) => 5,
                (3, 1) => 4,
                (0, _) => 3,
                (3, 0) => 2, // symbol
                _ => 1
            };
            if (score > bestScore)
            {
                bestScore = score;
                best = cmapOffset + offset;
            }
        }

        var map = new Dictionary<int, int>();
        if (best < 0)
        {
            return map;
        }

        var format = BinaryPrimitives.ReadUInt16BigEndian(font[best..]);
        switch (format)
        {
            case 4:
                ParseCmapFormat4(font, best, map);
                break;
            case 12:
                ParseCmapFormat12(font, best, map);
                break;
            case 6:
                ParseCmapFormat6(font, best, map);
                break;
            case 0:
                for (var c = 0; c < 256; c++)
                {
                    var glyph = font[best + 6 + c];
                    if (glyph != 0)
                    {
                        map[c] = glyph;
                    }
                }

                break;
        }

        // Symbol cmaps map chars into 0xF000-0xF0FF; alias them onto ASCII.
        if (map.Count > 0 && !map.ContainsKey('A') && map.ContainsKey(0xF041))
        {
            foreach (var (code, glyph) in map.Where(kv => kv.Key is >= 0xF020 and <= 0xF0FF).ToList())
            {
                map.TryAdd(code - 0xF000, glyph);
            }
        }

        return map;
    }

    private static void ParseCmapFormat4(ReadOnlySpan<byte> font, int offset, Dictionary<int, int> map)
    {
        var segCountX2 = BinaryPrimitives.ReadUInt16BigEndian(font[(offset + 6)..]);
        var segCount = segCountX2 / 2;
        var endCodes = offset + 14;
        var startCodes = endCodes + segCountX2 + 2;
        var idDeltas = startCodes + segCountX2;
        var idRangeOffsets = idDeltas + segCountX2;

        for (var seg = 0; seg < segCount; seg++)
        {
            var end = BinaryPrimitives.ReadUInt16BigEndian(font[(endCodes + seg * 2)..]);
            var start = BinaryPrimitives.ReadUInt16BigEndian(font[(startCodes + seg * 2)..]);
            var delta = BinaryPrimitives.ReadInt16BigEndian(font[(idDeltas + seg * 2)..]);
            var rangeOffset = BinaryPrimitives.ReadUInt16BigEndian(font[(idRangeOffsets + seg * 2)..]);

            if (start == 0xFFFF)
            {
                continue;
            }

            for (var c = (int)start; c <= end; c++)
            {
                int glyph;
                if (rangeOffset == 0)
                {
                    glyph = (c + delta) & 0xFFFF;
                }
                else
                {
                    var glyphAddress = idRangeOffsets + seg * 2 + rangeOffset + (c - start) * 2;
                    if (glyphAddress + 1 >= font.Length)
                    {
                        continue;
                    }

                    glyph = BinaryPrimitives.ReadUInt16BigEndian(font[glyphAddress..]);
                    if (glyph != 0)
                    {
                        glyph = (glyph + delta) & 0xFFFF;
                    }
                }

                if (glyph != 0)
                {
                    map[c] = glyph;
                }
            }
        }
    }

    private static void ParseCmapFormat12(ReadOnlySpan<byte> font, int offset, Dictionary<int, int> map)
    {
        var numGroups = (int)BinaryPrimitives.ReadUInt32BigEndian(font[(offset + 12)..]);
        for (var i = 0; i < numGroups; i++)
        {
            var group = font[(offset + 16 + i * 12)..];
            var startChar = (int)BinaryPrimitives.ReadUInt32BigEndian(group);
            var endChar = (int)BinaryPrimitives.ReadUInt32BigEndian(group[4..]);
            var startGlyph = (int)BinaryPrimitives.ReadUInt32BigEndian(group[8..]);
            for (var c = startChar; c <= endChar && c <= 0xFFFF; c++)
            {
                map[c] = startGlyph + (c - startChar);
            }
        }
    }

    private static void ParseCmapFormat6(ReadOnlySpan<byte> font, int offset, Dictionary<int, int> map)
    {
        var firstCode = BinaryPrimitives.ReadUInt16BigEndian(font[(offset + 6)..]);
        var entryCount = BinaryPrimitives.ReadUInt16BigEndian(font[(offset + 8)..]);
        for (var i = 0; i < entryCount; i++)
        {
            var glyph = BinaryPrimitives.ReadUInt16BigEndian(font[(offset + 10 + i * 2)..]);
            if (glyph != 0)
            {
                map[firstCode + i] = glyph;
            }
        }
    }

    private static string? ReadPostScriptName(ReadOnlySpan<byte> font, Dictionary<string, (int Offset, int Length)> tables)
    {
        if (!tables.TryGetValue("name", out var nameTable))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16BigEndian(font[(nameTable.Offset + 2)..]);
        var stringOffset = nameTable.Offset + BinaryPrimitives.ReadUInt16BigEndian(font[(nameTable.Offset + 4)..]);

        string? fallback = null;
        for (var i = 0; i < count; i++)
        {
            var record = font[(nameTable.Offset + 6 + i * 12)..];
            var platform = BinaryPrimitives.ReadUInt16BigEndian(record);
            var nameId = BinaryPrimitives.ReadUInt16BigEndian(record[6..]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(record[8..]);
            var offset = BinaryPrimitives.ReadUInt16BigEndian(record[10..]);
            if (nameId != 6 || stringOffset + offset + length > font.Length)
            {
                continue;
            }

            var bytes = font.Slice(stringOffset + offset, length);
            var value = platform == 3
                ? Encoding.BigEndianUnicode.GetString(bytes)
                : Encoding.ASCII.GetString(bytes);
            value = SanitizeName(value);
            if (value.Length > 0)
            {
                if (platform == 3)
                {
                    return value;
                }

                fallback = value;
            }
        }

        return fallback;
    }

    private static string SanitizeName(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c > ' ' && c < 0x7F && c != '/' && c != '[' && c != ']' && c != '(' && c != ')' && c != '<' && c != '>' && c != '#' && c != '%')
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
