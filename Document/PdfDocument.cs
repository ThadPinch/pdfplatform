using FrontEndSuite.PdfPlatform.Cos;
using FrontEndSuite.PdfPlatform.Geometry;
using FrontEndSuite.PdfPlatform.IO;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>
/// A parsed PDF document. Read-side API for Phase 1; mutation and serialization arrive in Phase 2.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private const int MaxPageTreeDepth = 256;

    private readonly PdfFileParser _parser;
    private List<PdfPage>? _pages;

    private PdfDocument(PdfFileParser parser)
    {
        _parser = parser;
    }

    public static PdfDocument Load(byte[] bytes)
    {
        var parser = new PdfFileParser(bytes);
        parser.Parse();
        return new PdfDocument(parser);
    }

    public static PdfDocument Load(Stream stream)
    {
        if (stream is MemoryStream ms && ms.TryGetBuffer(out var buffer)
            && buffer.Offset == 0 && buffer.Count == buffer.Array!.Length)
        {
            return Load(buffer.Array);
        }

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return Load(copy.ToArray());
    }

    public static PdfDocument LoadFile(string path) => Load(File.ReadAllBytes(path));

    /// <summary>Creates a new empty document (no pages) ready for AddPage and Save.</summary>
    public static PdfDocument Create()
    {
        using var ms = new MemoryStream();
        void W(string s)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(s);
            ms.Write(bytes, 0, bytes.Length);
        }

        W("%PDF-1.7\n");
        var catalogOffset = ms.Position;
        W("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        var pagesOffset = ms.Position;
        W("2 0 obj\n<< /Type /Pages /Kids [ ] /Count 0 >>\nendobj\n");
        var xrefOffset = ms.Position;
        W("xref\n0 3\n");
        W("0000000000 65535 f\r\n");
        W($"{catalogOffset:D10} 00000 n\r\n");
        W($"{pagesOffset:D10} 00000 n\r\n");
        W($"trailer\n<< /Size 3 /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF");
        return Load(ms.ToArray());
    }

    /// <summary>The PDF version, from the header or the catalog /Version override.</summary>
    public string Version
    {
        get
        {
            var catalogVersion = Catalog.GetAsName(CosNames.Version)?.Value;
            if (catalogVersion != null
                && string.CompareOrdinal(catalogVersion, _parser.Version) > 0)
            {
                return catalogVersion;
            }

            return _parser.Version;
        }
    }

    /// <summary>
    /// True when the file declares encryption. This library does not decrypt; strings and streams
    /// of an encrypted file are not usable beyond structural inspection.
    /// </summary>
    public bool IsEncrypted => _parser.IsEncrypted;

    public CosDictionary Trailer => _parser.Trailer;

    /// <summary>True when the file's xref data was broken and the object table was rebuilt by scanning.</summary>
    public bool UsedRecoveryScan => _parser.UsedRecoveryScan;

    /// <summary>Why recovery was needed, when <see cref="UsedRecoveryScan"/> is true.</summary>
    public string? RecoveryReason => _parser.RecoveryReason;

    public CosDictionary Catalog =>
        _parser.GetCatalog() ?? throw new PdfParseException("Document catalog is missing.");

    public CosDictionary? Info => Trailer.GetAsDictionary(CosNames.Info);

    /// <summary>Highest object number in the document's object table.</summary>
    public int MaxObjectNumber => _parser.MaxObjectNumber;

    /// <summary>
    /// Fetches an indirect object by number from the flat object table. Returns null when the
    /// number is unused — callers walking 1..MaxObjectNumber must null-check.
    /// </summary>
    public CosObject? GetObject(int objectNumber)
    {
        var obj = _parser.GetObject(objectNumber);
        return obj is CosNull ? null : obj;
    }

    public IReadOnlyList<PdfPage> Pages => _pages ??= BuildPages();

    public int PageCount => Pages.Count;

    /// <summary>Fetches a page by 1-based page number.</summary>
    public PdfPage GetPage(int pageNumber)
    {
        if (pageNumber < 1 || pageNumber > Pages.Count)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageNumber), pageNumber, $"Document has {Pages.Count} page(s).");
        }

        return Pages[pageNumber - 1];
    }

    /// <summary>Registers a new object in the object table and returns its indirect reference.</summary>
    public CosIndirectReference AddObject(CosObject obj) => _parser.AddObject(obj);

    /// <summary>Returns the object's indirect reference, registering it as a new object if needed.</summary>
    public CosIndirectReference EnsureIndirect(CosObject obj) => obj.IndirectReference ?? AddObject(obj);

    /// <summary>Appends a new page (at the root of the page tree) with the given media box.</summary>
    public PdfPage AddPage(PdfRect mediaBox)
    {
        var pagesNode = Catalog.GetAsDictionary(CosNames.Pages)
            ?? throw new PdfParseException("Document catalog has no /Pages tree.");

        var kids = pagesNode.GetAsArray(CosNames.Kids);
        if (kids == null)
        {
            kids = new CosArray();
            pagesNode.Put(CosNames.Kids, kids);
        }

        var page = new CosDictionary();
        page.Put(CosNames.Type, CosNames.Page);
        page.Put(CosNames.MediaBox, mediaBox.ToCosArray());
        page.Put(CosNames.Parent, (CosObject?)pagesNode.IndirectReference ?? pagesNode);

        kids.Add(AddObject(page));
        pagesNode.Put(CosNames.Count, new CosNumber((pagesNode.GetAsInt(CosNames.Count) ?? 0) + 1));
        _pages = null;
        return GetPage(PageCount);
    }

    /// <summary>Appends an already-copied page dictionary to the root page tree (used by PdfImporter).</summary>
    internal PdfPage AppendImportedPage(CosDictionary pageDictionary)
    {
        var pagesNode = Catalog.GetAsDictionary(CosNames.Pages)
            ?? throw new PdfParseException("Document catalog has no /Pages tree.");

        var kids = pagesNode.GetAsArray(CosNames.Kids);
        if (kids == null)
        {
            kids = new CosArray();
            pagesNode.Put(CosNames.Kids, kids);
        }

        pageDictionary.Put(CosNames.Parent, (CosObject?)pagesNode.IndirectReference ?? pagesNode);
        kids.Add(EnsureIndirect(pageDictionary));
        pagesNode.Put(CosNames.Count, new CosNumber((pagesNode.GetAsInt(CosNames.Count) ?? 0) + 1));
        _pages = null;
        return GetPage(PageCount);
    }

    /// <summary>The document information dictionary, created (and linked in the trailer) on demand.</summary>
    public CosDictionary GetOrCreateInfo()
    {
        if (Info is { } existing)
        {
            return existing;
        }

        var info = new CosDictionary();
        Trailer.Put(CosNames.Info, AddObject(info));
        return info;
    }

    public void SetCreator(string value) => GetOrCreateInfo().Put(CosNames.Creator, new CosString(value));

    public void SetProducer(string value) => GetOrCreateInfo().Put(CosNames.Producer, new CosString(value));

    /// <summary>
    /// Serializes the document to a fresh PDF file. Unreachable objects (superseded generations,
    /// old xref structures) are dropped and the rest renumbered compactly.
    /// </summary>
    public byte[] Save(PdfSaveOptions? options = null)
    {
        if (IsEncrypted)
        {
            throw new NotSupportedException("Encrypted PDFs cannot be rewritten; decryption is not supported.");
        }

        return PdfSerializer.Serialize(Catalog, Info, Version, options);
    }

    /// <summary>Serializes directly to the stream; classic mode streams without buffering the file.</summary>
    public void Save(Stream destination, PdfSaveOptions? options = null)
    {
        if (IsEncrypted)
        {
            throw new NotSupportedException("Encrypted PDFs cannot be rewritten; decryption is not supported.");
        }

        PdfSerializer.Serialize(Catalog, Info, Version, destination, options);
    }

    public void Dispose()
    {
        // All state is managed memory; present for parity with using-based call sites.
    }

    // ---- Page tree ----

    private List<PdfPage> BuildPages()
    {
        var pages = new List<PdfPage>();
        var root = Catalog.GetAsDictionary(CosNames.Pages);
        if (root != null)
        {
            WalkPageTree(root, pages, new HashSet<CosDictionary>(), null, null, null, 0, 0);
        }

        if (pages.Count == 0)
        {
            ScanForOrphanPages(pages);
        }

        return pages;
    }

    private void WalkPageTree(
        CosDictionary node,
        List<PdfPage> pages,
        HashSet<CosDictionary> visited,
        PdfRect? mediaBox,
        PdfRect? cropBox,
        CosDictionary? resources,
        int rotation,
        int depth)
    {
        if (depth > MaxPageTreeDepth || !visited.Add(node))
        {
            return;
        }

        mediaBox = PdfRect.FromCosArray(node.GetAsArray(CosNames.MediaBox)) ?? mediaBox;
        cropBox = PdfRect.FromCosArray(node.GetAsArray(CosNames.CropBox)) ?? cropBox;
        resources = node.GetAsDictionary(CosNames.Resources) ?? resources;
        rotation = node.GetAsInt(CosNames.Rotate) ?? rotation;

        var kids = node.GetAsArray(CosNames.Kids);
        if (kids == null)
        {
            if (IsPageLeaf(node))
            {
                pages.Add(new PdfPage(node, pages.Count + 1, mediaBox, cropBox, resources, rotation)
                {
                    Document = this
                });
            }

            return;
        }

        for (var i = 0; i < kids.Count; i++)
        {
            if (kids.GetAsDictionary(i) is { } kid)
            {
                WalkPageTree(kid, pages, visited, mediaBox, cropBox, resources, rotation, depth + 1);
            }
        }
    }

    private static bool IsPageLeaf(CosDictionary node)
    {
        var type = node.GetAsName(CosNames.Type);
        if (type != null)
        {
            return CosNames.Page.Equals(type);
        }

        return node.ContainsKey(CosNames.Contents) || node.ContainsKey(CosNames.MediaBox);
    }

    /// <summary>
    /// Recovery for files whose page tree is broken: collect /Type /Page objects from the flat
    /// object table in object-number order, resolving inheritance by climbing /Parent links.
    /// </summary>
    private void ScanForOrphanPages(List<PdfPage> pages)
    {
        var max = MaxObjectNumber;
        for (var number = 1; number <= max; number++)
        {
            if (GetObject(number) is not CosDictionary dict || dict is CosStream)
            {
                continue;
            }

            if (!CosNames.Page.Equals(dict.GetAsName(CosNames.Type)))
            {
                continue;
            }

            var mediaBox = InheritThroughParents(dict, d => PdfRect.FromCosArray(d.GetAsArray(CosNames.MediaBox)));
            var cropBox = InheritThroughParents(dict, d => PdfRect.FromCosArray(d.GetAsArray(CosNames.CropBox)));
            var resources = InheritThroughParents(dict, d => d.GetAsDictionary(CosNames.Resources));
            var rotation = InheritThroughParents(dict, d => d.GetAsInt(CosNames.Rotate)) ?? 0;
            pages.Add(new PdfPage(dict, pages.Count + 1, mediaBox, cropBox, resources, rotation)
            {
                Document = this
            });
        }
    }

    private static T? InheritThroughParents<T>(CosDictionary page, Func<CosDictionary, T?> selector)
    {
        var node = page;
        for (var depth = 0; node != null && depth < MaxPageTreeDepth; depth++)
        {
            var value = selector(node);
            if (value != null)
            {
                return value;
            }

            node = node.GetAsDictionary(CosNames.Parent);
        }

        return default;
    }
}
