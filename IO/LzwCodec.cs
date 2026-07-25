using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.IO;

/// <summary>PDF LZWDecode: TIFF-style LZW with 9-12 bit codes and the EarlyChange convention.</summary>
public static class LzwCodec
{
    private const int ClearCode = 256;
    private const int EndCode = 257;
    private const int FirstFreeCode = 258;
    private const int MaxCodeWidth = 12;

    public static byte[] Decode(byte[] data, CosDictionary? decodeParms)
    {
        if (data.Length == 0)
        {
            return data;
        }

        var earlyChange = decodeParms?.GetAsInt(CosNames.EarlyChange) ?? 1;
        using var output = new MemoryStream(data.Length * 3);

        var table = new List<byte[]?>(4096);
        void ResetTable()
        {
            table.Clear();
            for (var i = 0; i < 256; i++)
            {
                table.Add(new[] { (byte)i });
            }

            table.Add(null); // clear
            table.Add(null); // end
        }

        ResetTable();
        var codeWidth = 9;
        byte[]? previous = null;

        var bitPosition = 0;
        var totalBits = data.Length * 8;

        int ReadCode(int width)
        {
            if (bitPosition + width > totalBits)
            {
                return -1;
            }

            var value = 0;
            for (var i = 0; i < width; i++)
            {
                var bit = (data[bitPosition >> 3] >> (7 - (bitPosition & 7))) & 1;
                value = value << 1 | bit;
                bitPosition++;
            }

            return value;
        }

        while (true)
        {
            var code = ReadCode(codeWidth);
            if (code < 0 || code == EndCode)
            {
                break;
            }

            if (code == ClearCode)
            {
                ResetTable();
                codeWidth = 9;
                previous = null;
                continue;
            }

            byte[] entry;
            if (code < table.Count && table[code] is { } known)
            {
                entry = known;
            }
            else if (previous != null)
            {
                // The KwKwK case: the code being defined right now.
                entry = new byte[previous.Length + 1];
                previous.CopyTo(entry, 0);
                entry[^1] = previous[0];
            }
            else
            {
                break; // corrupt stream
            }

            output.Write(entry, 0, entry.Length);

            if (previous != null && table.Count < 4096)
            {
                var added = new byte[previous.Length + 1];
                previous.CopyTo(added, 0);
                added[^1] = entry[0];
                table.Add(added);
            }

            previous = entry;

            if (codeWidth < MaxCodeWidth && table.Count + earlyChange - 1 >= 1 << codeWidth)
            {
                codeWidth++;
            }
        }

        return FlateCodec.ApplyPredictor(output.ToArray(), decodeParms);
    }
}
