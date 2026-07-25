namespace FrontEndSuite.PdfPlatform.Fonts;

/// <summary>WinAnsiEncoding (CP1252) conversions between unicode chars and byte codes.</summary>
internal static class WinAnsi
{
    private static readonly Dictionary<char, byte> Reverse = BuildReverse();

    public static int ToUnicode(int code)
    {
        if (code < 0x80 || code > 0x9F)
        {
            return code;
        }

        return code switch
        {
            0x80 => 0x20AC, 0x82 => 0x201A, 0x83 => 0x0192, 0x84 => 0x201E, 0x85 => 0x2026,
            0x86 => 0x2020, 0x87 => 0x2021, 0x88 => 0x02C6, 0x89 => 0x2030, 0x8A => 0x0160,
            0x8B => 0x2039, 0x8C => 0x0152, 0x8E => 0x017D, 0x91 => 0x2018, 0x92 => 0x2019,
            0x93 => 0x201C, 0x94 => 0x201D, 0x95 => 0x2022, 0x96 => 0x2013, 0x97 => 0x2014,
            0x98 => 0x02DC, 0x99 => 0x2122, 0x9A => 0x0161, 0x9B => 0x203A, 0x9C => 0x0153,
            0x9E => 0x017E, 0x9F => 0x0178,
            _ => -1
        };
    }

    /// <summary>Maps a unicode char to its WinAnsi byte, or -1 when unrepresentable.</summary>
    public static int FromUnicode(char c) => Reverse.TryGetValue(c, out var b) ? b : -1;

    private static Dictionary<char, byte> BuildReverse()
    {
        var map = new Dictionary<char, byte>();
        for (var code = 0x20; code <= 0xFF; code++)
        {
            var unicode = ToUnicode(code);
            if (unicode >= 0)
            {
                map[(char)unicode] = (byte)code;
            }
        }

        return map;
    }
}
