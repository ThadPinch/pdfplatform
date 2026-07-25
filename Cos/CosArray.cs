using System.Collections;

namespace FrontEndSuite.PdfPlatform.Cos;

public sealed class CosArray : CosObject, IEnumerable<CosObject>
{
    private readonly List<CosObject> _items = new();

    public CosArray()
    {
    }

    public CosArray(IEnumerable<CosObject> items)
    {
        _items.AddRange(items);
    }

    public int Count => _items.Count;

    /// <summary>Fetches an element with indirect references resolved. Out-of-range indexes return CosNull.</summary>
    public CosObject Get(int index) =>
        index >= 0 && index < _items.Count ? _items[index].Resolve() : CosNull.Instance;

    /// <summary>Fetches the stored element without resolving indirect references.</summary>
    public CosObject? GetRaw(int index) => index >= 0 && index < _items.Count ? _items[index] : null;

    public CosDictionary? GetAsDictionary(int index) => Get(index) as CosDictionary;

    public CosStream? GetAsStream(int index) => Get(index) as CosStream;

    public CosArray? GetAsArray(int index) => Get(index) as CosArray;

    public CosName? GetAsName(int index) => Get(index) as CosName;

    public CosString? GetAsString(int index) => Get(index) as CosString;

    public CosNumber? GetAsNumber(int index) => Get(index) as CosNumber;

    public int? GetAsInt(int index) => (Get(index) as CosNumber)?.IntValue;

    public float? GetAsFloat(int index) => (Get(index) as CosNumber)?.FloatValue;

    public void Add(CosObject item) => _items.Add(item);

    public void Insert(int index, CosObject item) => _items.Insert(index, item);

    public void Set(int index, CosObject item) => _items[index] = item;

    public bool Remove(CosObject item) => _items.Remove(item);

    public IEnumerator<CosObject> GetEnumerator() => _items.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() => $"[ {Count} items ]";
}
