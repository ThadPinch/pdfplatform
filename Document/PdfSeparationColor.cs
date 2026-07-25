using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>
/// A Separation (spot) color space over a DeviceCMYK alternate with a type-2 exponential tint
/// transform from paper white (tint 0) to the given CMYK (tint 1).
/// </summary>
public sealed class PdfSeparationColor
{
    private PdfSeparationColor(string name, CosIndirectReference colorSpaceReference)
    {
        Name = name;
        ColorSpaceReference = colorSpaceReference;
    }

    public string Name { get; }

    public CosIndirectReference ColorSpaceReference { get; }

    public static PdfSeparationColor Create(PdfDocument document, string colorantName, float c, float m, float y, float k)
    {
        var tintFunction = new CosDictionary();
        tintFunction.Put(CosNames.FunctionType, new CosNumber(2));
        tintFunction.Put(CosNames.Domain, NumberArray(0, 1));
        tintFunction.Put(CosNames.Range, NumberArray(0, 1, 0, 1, 0, 1, 0, 1));
        tintFunction.Put(CosNames.C0, NumberArray(0, 0, 0, 0));
        tintFunction.Put(CosNames.C1, NumberArray(c, m, y, k));
        tintFunction.Put(CosNames.N, new CosNumber(1));
        var functionReference = document.AddObject(tintFunction);

        var colorSpace = new CosArray();
        colorSpace.Add(CosNames.Separation);
        colorSpace.Add(new CosName(colorantName));
        colorSpace.Add(new CosName("DeviceCMYK"));
        colorSpace.Add(functionReference);
        var colorSpaceReference = document.AddObject(colorSpace);

        return new PdfSeparationColor(colorantName, colorSpaceReference);
    }

    private static CosArray NumberArray(params double[] values)
    {
        var array = new CosArray();
        foreach (var value in values)
        {
            array.Add(new CosNumber(value));
        }

        return array;
    }
}
