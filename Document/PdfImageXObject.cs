using FrontEndSuite.PdfPlatform.Cos;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>
/// An image XObject. JPEG bytes embed as DCTDecode passthrough; every other format is decoded
/// with ImageSharp into raw RGB pixels (flate-compressed at save) plus an SMask when alpha is
/// present. Pixel work stays in ImageSharp by design — this class never decodes DCT/JPX itself.
/// </summary>
public sealed class PdfImageXObject
{
    /// <summary>Wraps an existing image XObject stream.</summary>
    public PdfImageXObject(CosStream stream)
    {
        Stream = stream;
    }

    public CosStream Stream { get; }

    public int Width => Stream.GetAsInt(CosNames.Width) ?? 0;

    public int Height => Stream.GetAsInt(CosNames.Height) ?? 0;

    public int BitsPerComponent => Stream.GetAsInt(CosNames.BitsPerComponent) ?? 8;

    public CosObject? ColorSpace => Stream.Get(CosNames.ColorSpace);

    /// <summary>
    /// File extension implied by the image's filter: jpg (DCTDecode), jp2 (JPXDecode),
    /// or raw for flate/unfiltered pixel data.
    /// </summary>
    public string IdentifyFileExtension()
    {
        foreach (var name in EnumerateFilterNames())
        {
            if (CosNames.DCTDecode.Equals(name))
            {
                return "jpg";
            }

            if (CosNames.JPXDecode.Equals(name))
            {
                return "jp2";
            }

            if (CosNames.CCITTFaxDecode.Equals(name))
            {
                return "tif";
            }

            if (CosNames.JBIG2Decode.Equals(name))
            {
                return "jbig2";
            }
        }

        return "raw";
    }

    /// <summary>
    /// The stored image bytes with any flate wrapping removed: raw pixels for flate/unfiltered
    /// images, the untouched JPEG/JPEG2000 payload for DCT/JPX.
    /// </summary>
    public byte[] GetImageBytes()
    {
        return Stream.TryGetDecodedBytes(out var decoded) ? decoded : Stream.RawData;
    }

    /// <summary>
    /// Rewrites this image's data and geometry in place while preserving /SMask, /Mask, /Intent
    /// and every other key not directly implied by the new data (the optimization-service pattern).
    /// </summary>
    public void ReplaceWithJpeg(byte[] jpegBytes, int width, int height, CosName colorSpace, int bitsPerComponent = 8)
    {
        Stream.RawData = jpegBytes;
        Stream.Put(CosNames.Filter, CosNames.DCTDecode);
        Stream.Remove(CosNames.DecodeParms);
        Stream.Remove(CosNames.DP);
        Stream.Remove(CosNames.Decode);
        Stream.Put(CosNames.Width, new CosNumber(width));
        Stream.Put(CosNames.Height, new CosNumber(height));
        Stream.Put(CosNames.BitsPerComponent, new CosNumber(bitsPerComponent));
        Stream.Put(CosNames.ColorSpace, colorSpace);
        Stream.Put(CosNames.Length, new CosNumber(jpegBytes.Length));
    }

    /// <summary>Same as ReplaceWithJpeg but for raw pixel data, flate-compressed at save time.</summary>
    public void ReplaceWithRawPixels(byte[] pixelData, int width, int height, CosName colorSpace, int bitsPerComponent = 8)
    {
        Stream.SetData(pixelData);
        Stream.Remove(CosNames.Decode);
        Stream.Put(CosNames.Width, new CosNumber(width));
        Stream.Put(CosNames.Height, new CosNumber(height));
        Stream.Put(CosNames.BitsPerComponent, new CosNumber(bitsPerComponent));
        Stream.Put(CosNames.ColorSpace, colorSpace);
    }

    /// <summary>
    /// Embeds encoded image bytes (PNG, JPEG, ...). JPEG passes through untouched as DCTDecode;
    /// anything else is decoded via ImageSharp to DeviceRGB pixels with an SMask when alpha exists.
    /// </summary>
    public static PdfImageXObject Create(PdfDocument document, byte[] imageBytes)
    {
        if (imageBytes.Length > 3 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
        {
            return CreateFromJpeg(imageBytes);
        }

        return CreateFromDecodedPixels(document, imageBytes);
    }

    private static PdfImageXObject CreateFromJpeg(byte[] jpegBytes)
    {
        var (width, height, precision, components, adobeMarker) = ParseJpegHeader(jpegBytes);

        var stream = new CosStream();
        stream.Put(CosNames.Type, CosNames.XObject);
        stream.Put(CosNames.Subtype, CosNames.Image);
        stream.Put(CosNames.Width, new CosNumber(width));
        stream.Put(CosNames.Height, new CosNumber(height));
        stream.Put(CosNames.BitsPerComponent, new CosNumber(precision));
        stream.Put(CosNames.ColorSpace, components switch
        {
            1 => CosNames.DeviceGray,
            4 => new CosName("DeviceCMYK"),
            _ => CosNames.DeviceRGB
        });
        if (components == 4 && adobeMarker)
        {
            // Adobe CMYK JPEGs store inverted values; PDF viewers expect this Decode array.
            stream.Put(CosNames.Decode, new CosArray(new CosObject[]
            {
                new CosNumber(1), new CosNumber(0), new CosNumber(1), new CosNumber(0),
                new CosNumber(1), new CosNumber(0), new CosNumber(1), new CosNumber(0)
            }));
        }

        stream.Put(CosNames.Filter, CosNames.DCTDecode);
        stream.RawData = jpegBytes;
        stream.Put(CosNames.Length, new CosNumber(jpegBytes.Length));
        return new PdfImageXObject(stream);
    }

    private static PdfImageXObject CreateFromDecodedPixels(PdfDocument document, byte[] imageBytes)
    {
        using var image = SixLabors.ImageSharp.Image.Load<Rgba32>(imageBytes);
        var width = image.Width;
        var height = image.Height;

        var rgb = new byte[width * height * 3];
        var alpha = new byte[width * height];
        var hasAlpha = false;

        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < width; x++)
                {
                    var pixel = row[x];
                    var i = (y * width + x) * 3;
                    rgb[i] = pixel.R;
                    rgb[i + 1] = pixel.G;
                    rgb[i + 2] = pixel.B;
                    alpha[y * width + x] = pixel.A;
                    if (pixel.A != 255)
                    {
                        hasAlpha = true;
                    }
                }
            }
        });

        var stream = new CosStream();
        stream.Put(CosNames.Type, CosNames.XObject);
        stream.Put(CosNames.Subtype, CosNames.Image);
        stream.Put(CosNames.Width, new CosNumber(width));
        stream.Put(CosNames.Height, new CosNumber(height));
        stream.Put(CosNames.BitsPerComponent, new CosNumber(8));
        stream.Put(CosNames.ColorSpace, CosNames.DeviceRGB);
        stream.SetData(rgb);

        if (hasAlpha)
        {
            var smask = new CosStream();
            smask.Put(CosNames.Type, CosNames.XObject);
            smask.Put(CosNames.Subtype, CosNames.Image);
            smask.Put(CosNames.Width, new CosNumber(width));
            smask.Put(CosNames.Height, new CosNumber(height));
            smask.Put(CosNames.BitsPerComponent, new CosNumber(8));
            smask.Put(CosNames.ColorSpace, CosNames.DeviceGray);
            smask.SetData(alpha);
            stream.Put(CosNames.SMask, document.AddObject(smask));
        }

        return new PdfImageXObject(stream);
    }

    private static (int Width, int Height, int Precision, int Components, bool AdobeMarker) ParseJpegHeader(byte[] data)
    {
        var adobe = false;
        var i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i] != 0xFF)
            {
                i++;
                continue;
            }

            var marker = data[i + 1];
            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                i += 2;
                continue;
            }

            var segmentLength = data[i + 2] << 8 | data[i + 3];
            if (marker == 0xEE && segmentLength >= 7 && i + 9 < data.Length
                && data[i + 4] == 'A' && data[i + 5] == 'd' && data[i + 6] == 'o' && data[i + 7] == 'b' && data[i + 8] == 'e')
            {
                adobe = true;
            }
            else if (marker is >= 0xC0 and <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC)
            {
                if (i + 9 < data.Length)
                {
                    var precision = data[i + 4];
                    var height = data[i + 5] << 8 | data[i + 6];
                    var width = data[i + 7] << 8 | data[i + 8];
                    var components = data[i + 9];
                    return (width, height, precision, components, adobe);
                }

                break;
            }

            i += 2 + segmentLength;
        }

        throw new InvalidDataException("Could not find a JPEG SOF marker; the data does not look like a valid JPEG.");
    }

    private IEnumerable<CosName> EnumerateFilterNames()
    {
        var filter = Stream.Get(CosNames.Filter);
        if (filter is CosName single)
        {
            yield return single;
        }
        else if (filter is CosArray array)
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array.GetAsName(i) is { } name)
                {
                    yield return name;
                }
            }
        }
    }
}
