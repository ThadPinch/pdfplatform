using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>
/// An optional content group (layer): the OCG dictionary registered in the catalog's
/// /OCProperties (group list, panel order, and default-on state).
/// </summary>
public sealed class PdfOptionalContentGroup
{
    private PdfOptionalContentGroup(string name, CosDictionary dictionary, CosIndirectReference reference)
    {
        Name = name;
        Dictionary = dictionary;
        Reference = reference;
    }

    public string Name { get; }

    public CosDictionary Dictionary { get; }

    public CosIndirectReference Reference { get; }

    public static PdfOptionalContentGroup Create(PdfDocument document, string name)
    {
        var dictionary = new CosDictionary();
        dictionary.Put(CosNames.Type, CosNames.OCG);
        dictionary.Put(CosNames.Name, new CosString(name));
        var reference = document.AddObject(dictionary);

        var catalog = document.Catalog;
        var ocProperties = catalog.GetAsDictionary(CosNames.OCProperties);
        if (ocProperties == null)
        {
            ocProperties = new CosDictionary();
            catalog.Put(CosNames.OCProperties, ocProperties);
        }

        var ocgs = ocProperties.GetAsArray(CosNames.OCGs);
        if (ocgs == null)
        {
            ocgs = new CosArray();
            ocProperties.Put(CosNames.OCGs, ocgs);
        }

        ocgs.Add(reference);

        var defaultConfig = ocProperties.GetAsDictionary(CosNames.D);
        if (defaultConfig == null)
        {
            defaultConfig = new CosDictionary();
            ocProperties.Put(CosNames.D, defaultConfig);
        }

        var order = defaultConfig.GetAsArray(new CosName("Order"));
        if (order == null)
        {
            order = new CosArray();
            defaultConfig.Put(new CosName("Order"), order);
        }

        order.Add(reference);

        var onArray = defaultConfig.GetAsArray(new CosName("ON"));
        if (onArray == null)
        {
            onArray = new CosArray();
            defaultConfig.Put(new CosName("ON"), onArray);
        }

        onArray.Add(reference);

        return new PdfOptionalContentGroup(name, dictionary, reference);
    }
}
