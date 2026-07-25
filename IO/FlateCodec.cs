using System.IO.Compression;
using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.IO;

public static class FlateCodec
{
    /// <summary>Inflates FlateDecode data and applies the PNG/TIFF predictor from the decode parms, if any.</summary>
    public static byte[] Decode(byte[] data, CosDictionary? decodeParms)
    {
        if (data.Length == 0)
        {
            return data;
        }

        return ApplyPredictor(Inflate(data), decodeParms);
    }

    /// <summary>Applies the PNG/TIFF predictor described by the decode parms, if any. Shared with LZW.</summary>
    internal static byte[] ApplyPredictor(byte[] data, CosDictionary? decodeParms)
    {
        var predictor = decodeParms?.GetAsInt(CosNames.Predictor) ?? 1;
        if (predictor <= 1)
        {
            return data;
        }

        var colors = decodeParms?.GetAsInt(CosNames.Colors) ?? 1;
        var bitsPerComponent = decodeParms?.GetAsInt(CosNames.BitsPerComponent) ?? 8;
        var columns = decodeParms?.GetAsInt(CosNames.Columns) ?? 1;

        return predictor == 2
            ? ApplyTiffPredictor(data, colors, bitsPerComponent, columns)
            : ApplyPngPredictor(data, colors, bitsPerComponent, columns);
    }

    /// <summary>Compresses data as zlib-wrapped deflate, the standard FlateDecode encoding.</summary>
    public static byte[] Encode(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflater = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflater.Write(data, 0, data.Length);
        }

        return output.ToArray();
    }

    private static byte[] Inflate(byte[] data)
    {
        var looksZlib = data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8 | data[1]) % 31) == 0;
        if (looksZlib)
        {
            var zlib = TryInflate(data, zlibWrapper: true);
            if (zlib != null)
            {
                return zlib;
            }
        }

        return TryInflate(data, zlibWrapper: false)
            ?? TryInflate(data, zlibWrapper: true)
            ?? throw new InvalidDataException("Unable to inflate stream data.");
    }

    private static byte[]? TryInflate(byte[] data, bool zlibWrapper)
    {
        try
        {
            using var input = new MemoryStream(data, writable: false);
            using Stream inflater = zlibWrapper
                ? new ZLibStream(input, CompressionMode.Decompress)
                : new DeflateStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            var buffer = new byte[64 * 1024];
            try
            {
                int read;
                while ((read = inflater.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                }
            }
            catch (InvalidDataException)
            {
                // Truncated streams are common in damaged files; keep whatever inflated cleanly.
                if (output.Length == 0)
                {
                    return null;
                }
            }

            return output.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static byte[] ApplyPngPredictor(byte[] data, int colors, int bitsPerComponent, int columns)
    {
        var bytesPerPixel = Math.Max(1, (colors * bitsPerComponent + 7) / 8);
        var rowLength = (colors * bitsPerComponent * columns + 7) / 8;
        if (rowLength <= 0)
        {
            return data;
        }

        using var output = new MemoryStream(data.Length);
        var previous = new byte[rowLength];
        var position = 0;

        while (position < data.Length)
        {
            var filter = data[position++];
            var available = Math.Min(rowLength, data.Length - position);
            if (available <= 0)
            {
                break;
            }

            var row = new byte[rowLength];
            Array.Copy(data, position, row, 0, available);
            position += available;

            switch (filter)
            {
                case 0:
                    break;
                case 1:
                    for (var i = bytesPerPixel; i < rowLength; i++)
                    {
                        row[i] = (byte)(row[i] + row[i - bytesPerPixel]);
                    }

                    break;
                case 2:
                    for (var i = 0; i < rowLength; i++)
                    {
                        row[i] = (byte)(row[i] + previous[i]);
                    }

                    break;
                case 3:
                    for (var i = 0; i < rowLength; i++)
                    {
                        var left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                        row[i] = (byte)(row[i] + ((left + previous[i]) >> 1));
                    }

                    break;
                case 4:
                    for (var i = 0; i < rowLength; i++)
                    {
                        var left = i >= bytesPerPixel ? row[i - bytesPerPixel] : 0;
                        var upLeft = i >= bytesPerPixel ? previous[i - bytesPerPixel] : 0;
                        row[i] = (byte)(row[i] + Paeth(left, previous[i], upLeft));
                    }

                    break;
                default:
                    break;
            }

            output.Write(row, 0, rowLength);
            previous = row;
        }

        return output.ToArray();
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc)
        {
            return a;
        }

        return pb <= pc ? b : c;
    }

    private static byte[] ApplyTiffPredictor(byte[] data, int colors, int bitsPerComponent, int columns)
    {
        if (bitsPerComponent != 8)
        {
            return data;
        }

        var rowLength = colors * columns;
        if (rowLength <= 0)
        {
            return data;
        }

        var result = (byte[])data.Clone();
        for (var rowStart = 0; rowStart + rowLength <= result.Length; rowStart += rowLength)
        {
            for (var i = colors; i < rowLength; i++)
            {
                result[rowStart + i] = (byte)(result[rowStart + i] + result[rowStart + i - colors]);
            }
        }

        return result;
    }
}
