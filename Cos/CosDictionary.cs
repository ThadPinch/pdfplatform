using System.Collections;

namespace FrontEndSuite.PdfPlatform.Cos;

public class CosDictionary : CosObject, IEnumerable<KeyValuePair<CosName, CosObject>>
{
    private readonly Dictionary<CosName, CosObject> _items = new();

    public int Count => _items.Count;

    public IReadOnlyCollection<CosName> Keys => _items.Keys;

    /// <summary>Fetches a value with indirect references resolved. Returns null when the key is absent.</summary>
    public CosObject? Get(CosName key) => _items.TryGetValue(key, out var value) ? value.Resolve() : null;

    /// <summary>Fetches the stored value without resolving indirect references.</summary>
    public CosObject? GetRaw(CosName key) => _items.TryGetValue(key, out var value) ? value : null;

    public CosDictionary? GetAsDictionary(CosName key) => Get(key) as CosDictionary;

    public CosStream? GetAsStream(CosName key) => Get(key) as CosStream;

    public CosArray? GetAsArray(CosName key) => Get(key) as CosArray;

    public CosName? GetAsName(CosName key) => Get(key) as CosName;

    public CosString? GetAsString(CosName key) => Get(key) as CosString;

    public CosNumber? GetAsNumber(CosName key) => Get(key) as CosNumber;

    public bool? GetAsBool(CosName key) => (Get(key) as CosBoolean)?.Value;

    public int? GetAsInt(CosName key) => (Get(key) as CosNumber)?.IntValue;

    public long? GetAsLong(CosName key) => (Get(key) as CosNumber)?.LongValue;

    public float? GetAsFloat(CosName key) => (Get(key) as CosNumber)?.FloatValue;

    public void Put(CosName key, CosObject value) => _items[key] = value;

    public bool Remove(CosName key) => _items.Remove(key);

    public bool ContainsKey(CosName key) => _items.ContainsKey(key);

    public void Clear() => _items.Clear();

    public IEnumerator<KeyValuePair<CosName, CosObject>> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"<< {Count} entries >>";
}
