using System.Security.Cryptography;
using System.Text;
using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.Document;

/// <summary>
/// Copies object graphs from other documents into a target document, deduplicating shared
/// objects: importing the same source page (or two pages sharing fonts/images) reuses the
/// already-copied objects instead of duplicating them. Keep one importer per target document
/// for the whole operation to get the deduplication.
///
/// With <c>dedupIdenticalContent</c> enabled, deduplication also works across *different*
/// source documents by structural content hash: byte-identical streams (template content,
/// images, embedded fonts) land in the target once and every importing page references the
/// shared copy — assembling thousands of records built from one template stays close to
/// template-size + per-record overlays instead of multiplying the template.
/// </summary>
public sealed class PdfImporter
{
    private readonly PdfDocument _target;
    private readonly bool _dedupIdenticalContent;
    private readonly Dictionary<CosObject, CosObject> _copies = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<CosDictionary, PdfFormXObject> _importedPages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, CosStream> _streamsByHash = new(StringComparer.Ordinal);
    private readonly Dictionary<CosObject, string?> _hashMemo = new(ReferenceEqualityComparer.Instance);

    public PdfImporter(PdfDocument target, bool dedupIdenticalContent = false)
    {
        _target = target;
        _dedupIdenticalContent = dedupIdenticalContent;
    }

    /// <summary>
    /// Imports a page from another document as a form XObject: the page's content becomes the
    /// form's content, its resources are deep-copied, and the bounding box is the page's crop
    /// box (matching iText's CopyAsFormXObject).
    /// </summary>
    public PdfFormXObject ImportPageAsForm(PdfPage sourcePage)
    {
        if (_importedPages.TryGetValue(sourcePage.Dictionary, out var existing))
        {
            return existing;
        }

        var resources = sourcePage.Resources is { } sourceResources
            ? Copy(sourceResources)
            : new CosDictionary();

        var form = new PdfFormXObject(sourcePage.CropBox, sourcePage.GetContentBytes(), _target.EnsureIndirect(resources));
        _target.EnsureIndirect(form.Stream);
        _importedPages[sourcePage.Dictionary] = form;
        return form;
    }

    /// <summary>Deep-copies an object graph into the target document, deduplicating via this importer.</summary>
    public CosObject Copy(CosObject source)
    {
        switch (source)
        {
            case CosIndirectReference reference:
            {
                var resolved = reference.Resolve();
                if (resolved is CosNull)
                {
                    return CosNull.Instance;
                }

                var copy = CopyShared(resolved);
                return (CosObject?)copy.IndirectReference ?? _target.AddObject(copy);
            }

            case CosStream:
            case CosDictionary:
            case CosArray:
                return CopyShared(source);

            default:
                return source; // primitives are immutable; sharing across documents is safe
        }
    }

    /// <summary>
    /// Imports a whole page into the target document's page tree (the CopyPagesTo pattern),
    /// materializing inherited attributes so the page is self-contained in the new tree.
    /// </summary>
    public PdfPage ImportPage(PdfPage sourcePage)
    {
        var copy = new CosDictionary();
        foreach (var (key, value) in sourcePage.Dictionary)
        {
            if (CosNames.Parent.Equals(key))
            {
                continue; // the target tree provides the parent
            }

            copy.Put(key, Copy(value));
        }

        if (!copy.ContainsKey(CosNames.Resources) && sourcePage.Resources is { } inheritedResources)
        {
            copy.Put(CosNames.Resources, Copy(inheritedResources));
        }

        if (!copy.ContainsKey(CosNames.MediaBox))
        {
            copy.Put(CosNames.MediaBox, sourcePage.MediaBox.ToCosArray());
        }

        if (!copy.ContainsKey(CosNames.Rotate) && sourcePage.Rotation != 0)
        {
            copy.Put(CosNames.Rotate, new CosNumber(sourcePage.Rotation));
        }

        return _target.AppendImportedPage(copy);
    }

    private CosObject CopyShared(CosObject source)
    {
        if (_copies.TryGetValue(source, out var existing))
        {
            return existing;
        }

        switch (source)
        {
            case CosStream stream:
            {
                string? hash = null;
                if (_dedupIdenticalContent)
                {
                    hash = StructuralHash(stream, new HashSet<CosObject>(ReferenceEqualityComparer.Instance));
                    if (hash != null && _streamsByHash.TryGetValue(hash, out var identical))
                    {
                        _copies[source] = identical;
                        return identical;
                    }
                }

                var copy = new CosStream();
                _copies[source] = copy;
                _target.AddObject(copy);
                foreach (var (key, value) in stream)
                {
                    copy.Put(key, Copy(value));
                }

                copy.RawData = stream.RawData;
                if (hash != null)
                {
                    _streamsByHash[hash] = copy;
                }

                return copy;
            }

            case CosDictionary dictionary:
            {
                var copy = new CosDictionary();
                _copies[source] = copy;
                if (dictionary.IndirectReference != null)
                {
                    _target.AddObject(copy);
                }

                foreach (var (key, value) in dictionary)
                {
                    copy.Put(key, Copy(value));
                }

                return copy;
            }

            case CosArray array:
            {
                var copy = new CosArray();
                _copies[source] = copy;
                if (array.IndirectReference != null)
                {
                    _target.AddObject(copy);
                }

                foreach (var item in array)
                {
                    copy.Add(Copy(item));
                }

                return copy;
            }

            default:
                return source;
        }
    }

    /// <summary>
    /// A structural fingerprint of an object and everything it references: byte-identical
    /// subgraphs (template content, images, fonts) get equal hashes regardless of which source
    /// document they were parsed from. Returns null when the subgraph is cyclic.
    /// </summary>
    private string? StructuralHash(CosObject obj, HashSet<CosObject> visiting)
    {
        obj = obj.Resolve();
        switch (obj)
        {
            case CosNull:
                return "z";
            case CosBoolean b:
                return b.Value ? "t" : "f";
            case CosNumber n:
                return "n" + n.DoubleValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case CosName name:
                return "/" + name.Value;
            case CosString s:
                return "s" + Convert.ToHexString(SHA256.HashData(s.RawBytes));
        }

        if (_hashMemo.TryGetValue(obj, out var memoized))
        {
            return memoized;
        }

        if (!visiting.Add(obj))
        {
            return null; // cycle: skip dedup for this subgraph
        }

        string? result;
        switch (obj)
        {
            case CosStream stream:
            {
                var sb = new StringBuilder("S{");
                result = HashDictionaryEntries(stream, sb, visiting)
                    ? sb.Append('#').Append(Convert.ToHexString(SHA256.HashData(stream.RawData))).Append('}').ToString()
                    : null;
                break;
            }

            case CosDictionary dictionary:
            {
                var sb = new StringBuilder("D{");
                result = HashDictionaryEntries(dictionary, sb, visiting) ? sb.Append('}').ToString() : null;
                break;
            }

            case CosArray array:
            {
                var sb = new StringBuilder("A[");
                result = "A[";
                foreach (var item in array)
                {
                    var itemHash = StructuralHash(item, visiting);
                    if (itemHash == null)
                    {
                        result = null;
                        break;
                    }

                    sb.Append(itemHash).Append(',');
                }

                if (result != null)
                {
                    result = sb.Append(']').ToString();
                }

                break;
            }

            default:
                result = null;
                break;
        }

        // Long composite keys get collapsed so the dedup dictionary stays small.
        if (result is { Length: > 128 })
        {
            result = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(result)));
        }

        visiting.Remove(obj);
        _hashMemo[obj] = result;
        return result;
    }

    private bool HashDictionaryEntries(CosDictionary dictionary, StringBuilder sb, HashSet<CosObject> visiting)
    {
        foreach (var key in dictionary.Keys.OrderBy(k => k.Value, StringComparer.Ordinal))
        {
            if (CosNames.Length.Equals(key))
            {
                continue; // implied by the data
            }

            var valueHash = StructuralHash(dictionary.GetRaw(key)!, visiting);
            if (valueHash == null)
            {
                return false;
            }

            sb.Append('/').Append(key.Value).Append('=').Append(valueHash).Append(';');
        }

        return true;
    }
}
