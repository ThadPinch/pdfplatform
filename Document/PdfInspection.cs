using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>Read-only inspection helpers for color spaces and fonts (preflight-style reporting).</summary>
public static class PdfInspection
{
    /// <summary>
    /// Human-readable color-space description: device names as-is, ICCBased with its component
    /// count spelled out, Separation with its colorant, Indexed with its base space.
    /// </summary>
    public static string DescribeColorSpace(CosObject? colorSpace)
    {
        colorSpace = colorSpace?.Resolve();
        switch (colorSpace)
        {
            case null:
                return "Unknown";
            case CosName name:
                return name.Value;
            case CosArray array when array.Count > 0:
                var family = array.GetAsName(0)?.Value ?? "Unknown";
                if (family == "ICCBased" && array.Get(1) is CosStream icc)
                {
                    return (icc.GetAsInt(CosNames.N) ?? 0) switch
                    {
                        1 => "ICCBased (Gray)",
                        3 => "ICCBased (RGB)",
                        4 => "ICCBased (CMYK)",
                        _ => "ICCBased"
                    };
                }

                if (family == "Separation")
                {
                    return $"Separation ({array.GetAsName(1)?.Value ?? "?"})";
                }

                if (family == "Indexed")
                {
                    return $"Indexed ({DescribeColorSpace(array.Get(1))})";
                }

                return family;
            default:
                return "Unknown";
        }
    }

    /// <summary>
    /// Collects spot colorant names from a color-space entry: Separation colorants (except the
    /// reserved All/None), DeviceN colorants that are not process inks, recursing through Indexed.
    /// </summary>
    public static void CollectSpotColorNames(CosObject? colorSpace, ISet<string> names)
    {
        if (colorSpace?.Resolve() is not CosArray array || array.Count < 2)
        {
            return;
        }

        var family = array.GetAsName(0);
        if (CosNames.Separation.Equals(family))
        {
            var name = array.GetAsName(1)?.Value;
            if (!string.IsNullOrEmpty(name) && name != "All" && name != "None")
            {
                names.Add(name);
            }
        }
        else if (CosNames.DeviceN.Equals(family) && array.Get(1) is CosArray colorants)
        {
            for (var i = 0; i < colorants.Count; i++)
            {
                var name = colorants.GetAsName(i)?.Value;
                if (!string.IsNullOrEmpty(name) && name != "Cyan" && name != "Magenta"
                    && name != "Yellow" && name != "Black" && name != "None")
                {
                    names.Add(name);
                }
            }
        }
        else if (CosNames.Indexed.Equals(family))
        {
            CollectSpotColorNames(array.Get(1), names);
        }
    }

    /// <summary>Resolves a font's descriptor, looking through /DescendantFonts[0] for Type0 fonts.</summary>
    public static CosDictionary? ResolveFontDescriptor(CosDictionary fontDictionary)
    {
        var descriptor = fontDictionary.GetAsDictionary(CosNames.FontDescriptor);
        if (descriptor != null)
        {
            return descriptor;
        }

        var descendants = fontDictionary.GetAsArray(CosNames.DescendantFonts);
        return descendants is { Count: > 0 }
            ? descendants.GetAsDictionary(0)?.GetAsDictionary(CosNames.FontDescriptor)
            : null;
    }

    /// <summary>True when the font's descriptor carries an embedded font file.</summary>
    public static bool IsFontEmbedded(CosDictionary fontDictionary)
    {
        var descriptor = ResolveFontDescriptor(fontDictionary);
        return descriptor != null
               && (descriptor.ContainsKey(CosNames.FontFile)
                   || descriptor.ContainsKey(CosNames.FontFile2)
                   || descriptor.ContainsKey(CosNames.FontFile3));
    }
}
