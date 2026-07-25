using System.Text;
using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.IO;

public sealed class PdfParseException : Exception
{
    public PdfParseException(string message)
        : base(message)
    {
    }

    public PdfParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Parses a complete PDF file: header, cross-reference chain (classic tables, xref streams, and
/// hybrids), object streams, and on-demand indirect objects, with a scan-based recovery path for
/// files whose xref information is missing or wrong.
/// </summary>
public sealed class PdfFileParser : ICosResolver
{
    private static readonly byte[] HeaderMarker = Encoding.ASCII.GetBytes("%PDF-");
    private static readonly byte[] StartXrefMarker = Encoding.ASCII.GetBytes("startxref");
    private static readonly byte[] EndStreamMarker = Encoding.ASCII.GetBytes("endstream");
    private static readonly byte[] TrailerMarker = Encoding.ASCII.GetBytes("trailer");

    private readonly struct XrefEntry
    {
        public long Offset { get; init; }
        public int StreamObjectNumber { get; init; }
        public bool InObjectStream { get; init; }
    }

    private readonly byte[] _data;
    private readonly Dictionary<int, XrefEntry> _xref = new();
    private readonly Dictionary<int, CosObject> _cache = new();
    private readonly HashSet<int> _loading = new();
    private readonly HashSet<int> _loadedObjectStreams = new();
    private readonly CosDictionary _trailer = new();
    private Dictionary<int, long>? _scanTable;
    private string _version = "1.4";
    private int _nextNewObjectNumber = -1;

    public PdfFileParser(byte[] data)
    {
        _data = data;
    }

    public string Version => _version;

    public CosDictionary Trailer => _trailer;

    /// <summary>True when the xref chain was unusable and the object table was rebuilt by scanning.</summary>
    public bool UsedRecoveryScan { get; private set; }

    /// <summary>The reason the xref chain was abandoned, when <see cref="UsedRecoveryScan"/> is true.</summary>
    public string? RecoveryReason { get; private set; }

    public bool IsEncrypted => _trailer.ContainsKey(CosNames.Encrypt);

    public int MaxObjectNumber
    {
        get
        {
            var max = 0;
            foreach (var key in _xref.Keys)
            {
                if (key > max)
                {
                    max = key;
                }
            }

            if (_trailer.GetAsInt(CosNames.Size) is int size && size - 1 > max)
            {
                max = size - 1;
            }

            if (_nextNewObjectNumber - 1 > max)
            {
                max = _nextNewObjectNumber - 1;
            }

            return max;
        }
    }

    public void Parse()
    {
        ReadVersion();

        try
        {
            ParseXrefChain(FindStartXref());
        }
        catch (Exception ex)
        {
            RecoveryReason = ex.Message;
        }

        if (GetCatalog() == null)
        {
            UsedRecoveryScan = true;
            RecoveryReason ??= "The cross-reference chain did not yield a resolvable /Root.";
            RebuildByScan();
            if (GetCatalog() == null)
            {
                throw new PdfParseException("Could not locate the document catalog; the file does not appear to be a usable PDF.");
            }
        }
    }

    public CosDictionary? GetCatalog() => _trailer.GetAsDictionary(CosNames.Root);

    public CosObject ResolveReference(CosIndirectReference reference) => GetObject(reference.ObjectNumber);

    public CosObject GetObject(int objectNumber)
    {
        if (_cache.TryGetValue(objectNumber, out var cached))
        {
            return cached;
        }

        if (objectNumber <= 0 || !_loading.Add(objectNumber))
        {
            return CosNull.Instance;
        }

        try
        {
            var obj = LoadObject(objectNumber);
            _cache[objectNumber] = obj;
            AttachReference(obj, objectNumber);
            return obj;
        }
        finally
        {
            _loading.Remove(objectNumber);
        }
    }

    /// <summary>Registers a new object in the table under the next free number and returns its reference.</summary>
    public CosIndirectReference AddObject(CosObject obj)
    {
        if (_nextNewObjectNumber < 0)
        {
            _nextNewObjectNumber = MaxObjectNumber + 1;
        }

        var number = _nextNewObjectNumber++;
        _cache[number] = obj;
        var reference = new CosIndirectReference(number, 0) { Resolver = this };
        if (obj is not (CosNull or CosBoolean))
        {
            obj.IndirectReference = reference;
        }

        return reference;
    }

    private CosObject LoadObject(int objectNumber)
    {
        long? knownOffset = null;
        if (_xref.TryGetValue(objectNumber, out var entry))
        {
            if (entry.InObjectStream)
            {
                LoadObjectStreamMembers(entry.StreamObjectNumber);
                return _cache.TryGetValue(objectNumber, out var member) ? member : CosNull.Instance;
            }

            knownOffset = entry.Offset;
            var fromXref = TryParseObjectAt(entry.Offset, objectNumber);
            if (fromXref != null)
            {
                return fromXref;
            }
        }

        // The xref offset was missing or wrong; fall back to a full-file scan.
        var scanOffset = LookupScanOffset(objectNumber);
        if (scanOffset != null && scanOffset != knownOffset)
        {
            var fromScan = TryParseObjectAt(scanOffset.Value, objectNumber);
            if (fromScan != null)
            {
                return fromScan;
            }
        }

        return CosNull.Instance;
    }

    private CosObject? TryParseObjectAt(long offset, int expectedNumber)
    {
        try
        {
            var (number, _, obj) = ParseIndirectObjectAt(offset);
            return number == expectedNumber ? obj : null;
        }
        catch
        {
            return null;
        }
    }

    private void AttachReference(CosObject obj, int objectNumber)
    {
        if (obj is CosNull or CosBoolean)
        {
            return;
        }

        obj.IndirectReference = new CosIndirectReference(objectNumber, 0) { Resolver = this };
    }

    // ---- Header / startxref ----

    private void ReadVersion()
    {
        var index = Find(HeaderMarker, 0, Math.Min(_data.Length, 1024));
        if (index < 0)
        {
            return;
        }

        var sb = new StringBuilder(4);
        var i = index + HeaderMarker.Length;
        while (i < _data.Length && ((char)_data[i] is >= '0' and <= '9' or '.'))
        {
            sb.Append((char)_data[i++]);
        }

        if (sb.Length > 0)
        {
            _version = sb.ToString();
        }
    }

    private long FindStartXref()
    {
        var tailStart = Math.Max(0, _data.Length - 2048);
        var index = FindLast(StartXrefMarker, tailStart, _data.Length);
        if (index < 0)
        {
            throw new PdfParseException("startxref marker not found.");
        }

        var lexer = new PdfLexer(_data, index + StartXrefMarker.Length);
        var token = lexer.NextToken();
        if (token.Kind != PdfTokenKind.Integer)
        {
            throw new PdfParseException("startxref is not followed by an offset.");
        }

        return token.IntValue;
    }

    // ---- Xref chain ----

    private void ParseXrefChain(long startOffset)
    {
        var visited = new HashSet<long>();
        var offset = startOffset;

        while (offset >= 0 && offset < _data.Length && visited.Add(offset))
        {
            var parser = CreateParser((int)offset);
            CosDictionary section;

            if (parser.Peek().IsKeyword("xref"))
            {
                section = ParseClassicXrefSection(parser);
                MergeTrailer(section);
                if (section.GetAsLong(CosNames.XRefStm) is long hybridOffset && visited.Add(hybridOffset))
                {
                    var hybrid = ParseXrefStreamAt(hybridOffset);
                    if (hybrid != null)
                    {
                        MergeTrailer(hybrid);
                    }
                }
            }
            else
            {
                section = ParseXrefStreamAt(offset) ?? throw new PdfParseException($"No cross-reference section at offset {offset}.");
                MergeTrailer(section);
            }

            offset = section.GetAsLong(CosNames.Prev) ?? -1;
        }
    }

    private CosDictionary ParseClassicXrefSection(CosObjectParser parser)
    {
        parser.Next(); // xref keyword
        var trailer = new CosDictionary();

        while (true)
        {
            var token = parser.Peek();
            if (token.Kind == PdfTokenKind.Integer)
            {
                var start = (int)parser.Next().IntValue;
                var countToken = parser.Next();
                if (countToken.Kind != PdfTokenKind.Integer)
                {
                    break;
                }

                var count = (int)countToken.IntValue;
                for (var i = 0; i < count; i++)
                {
                    var offsetToken = parser.Next();
                    var generationToken = parser.Next();
                    var kindToken = parser.Next();
                    if (offsetToken.Kind != PdfTokenKind.Integer
                        || generationToken.Kind != PdfTokenKind.Integer
                        || kindToken.Kind != PdfTokenKind.Keyword)
                    {
                        return trailer;
                    }

                    if (kindToken.Text == "n")
                    {
                        AddXrefEntry(start + i, new XrefEntry { Offset = offsetToken.IntValue });
                    }
                }
            }
            else if (token.IsKeyword("trailer"))
            {
                parser.Next();
                if (parser.ParseObject() is CosDictionary dict)
                {
                    trailer = dict;
                }

                break;
            }
            else
            {
                break;
            }
        }

        return trailer;
    }

    private CosDictionary? ParseXrefStreamAt(long offset)
    {
        try
        {
            var (_, _, obj) = ParseIndirectObjectAt(offset);
            if (obj is not CosStream stream)
            {
                return null;
            }

            var data = stream.GetDecodedBytes();
            var widths = stream.GetAsArray(CosNames.W);
            if (widths == null || widths.Count < 3)
            {
                return stream;
            }

            var w1 = widths.GetAsInt(0) ?? 0;
            var w2 = widths.GetAsInt(1) ?? 0;
            var w3 = widths.GetAsInt(2) ?? 0;
            var entryLength = w1 + w2 + w3;
            if (entryLength <= 0)
            {
                return stream;
            }

            var size = stream.GetAsInt(CosNames.Size) ?? 0;
            var ranges = new List<(int Start, int Count)>();
            var index = stream.GetAsArray(CosNames.Index);
            if (index != null)
            {
                for (var i = 0; i + 1 < index.Count; i += 2)
                {
                    var start = index.GetAsInt(i);
                    var count = index.GetAsInt(i + 1);
                    if (start != null && count != null)
                    {
                        ranges.Add((start.Value, count.Value));
                    }
                }
            }
            else
            {
                ranges.Add((0, size));
            }

            var position = 0;
            foreach (var (start, count) in ranges)
            {
                for (var i = 0; i < count; i++)
                {
                    if (position + entryLength > data.Length)
                    {
                        return stream;
                    }

                    var type = w1 == 0 ? 1 : ReadBigEndian(data, position, w1);
                    var field2 = ReadBigEndian(data, position + w1, w2);
                    var field3 = w3 == 0 ? 0 : ReadBigEndian(data, position + w1 + w2, w3);
                    position += entryLength;

                    var objectNumber = start + i;
                    if (type == 1)
                    {
                        AddXrefEntry(objectNumber, new XrefEntry { Offset = field2 });
                    }
                    else if (type == 2)
                    {
                        AddXrefEntry(objectNumber, new XrefEntry
                        {
                            InObjectStream = true,
                            StreamObjectNumber = (int)field2
                        });
                    }

                    _ = field3;
                }
            }

            return stream;
        }
        catch
        {
            return null;
        }
    }

    private void AddXrefEntry(int objectNumber, XrefEntry entry)
    {
        // First entry wins: the chain is walked newest-to-oldest.
        if (objectNumber > 0 && !_xref.ContainsKey(objectNumber))
        {
            _xref[objectNumber] = entry;
        }
    }

    private void MergeTrailer(CosDictionary section)
    {
        foreach (var (key, value) in section)
        {
            if (!_trailer.ContainsKey(key))
            {
                _trailer.Put(key, value);
            }
        }
    }

    // ---- Indirect objects ----

    private (int Number, int Generation, CosObject Object) ParseIndirectObjectAt(long offset)
    {
        if (offset < 0 || offset >= _data.Length)
        {
            throw new PdfParseException($"Object offset {offset} is outside the file.");
        }

        var parser = CreateParser((int)offset);
        var numberToken = parser.Next();
        var generationToken = parser.Next();
        var objToken = parser.Next();
        if (numberToken.Kind != PdfTokenKind.Integer
            || generationToken.Kind != PdfTokenKind.Integer
            || !objToken.IsKeyword("obj"))
        {
            throw new PdfParseException($"No indirect object at offset {offset}.");
        }

        var obj = parser.ParseObject();

        var next = parser.Peek();
        if (next.IsKeyword("stream") && obj is CosDictionary dict)
        {
            parser.Next();
            obj = ReadStreamData(dict, next.Position + "stream".Length);
        }

        return ((int)numberToken.IntValue, (int)generationToken.IntValue, obj);
    }

    private CosStream ReadStreamData(CosDictionary dict, int afterKeyword)
    {
        var dataStart = afterKeyword;
        if (dataStart < _data.Length && _data[dataStart] == (byte)'\r')
        {
            dataStart++;
        }

        if (dataStart < _data.Length && _data[dataStart] == (byte)'\n')
        {
            dataStart++;
        }

        var dataEnd = -1;
        var length = ResolveStreamLength(dict);
        if (length >= 0 && dataStart + length <= _data.Length && IsEndStreamNear(dataStart + (int)length))
        {
            dataEnd = dataStart + (int)length;
        }
        else
        {
            // /Length missing or wrong; recover by scanning for the endstream keyword.
            var marker = Find(EndStreamMarker, dataStart, _data.Length);
            dataEnd = marker < 0 ? _data.Length : marker;
            if (dataEnd > dataStart && _data[dataEnd - 1] == (byte)'\n')
            {
                dataEnd--;
            }

            if (dataEnd > dataStart && _data[dataEnd - 1] == (byte)'\r')
            {
                dataEnd--;
            }
        }

        if (dataEnd < dataStart)
        {
            dataEnd = dataStart;
        }

        var raw = new byte[dataEnd - dataStart];
        Array.Copy(_data, dataStart, raw, 0, raw.Length);
        return new CosStream(dict, raw);
    }

    private long ResolveStreamLength(CosDictionary dict)
    {
        var raw = dict.GetRaw(CosNames.Length);
        if (raw is CosNumber direct)
        {
            return direct.LongValue;
        }

        if (raw is CosIndirectReference reference)
        {
            try
            {
                if (reference.Resolve() is CosNumber resolved)
                {
                    return resolved.LongValue;
                }
            }
            catch
            {
                // Treat as unknown length.
            }
        }

        return -1;
    }

    private bool IsEndStreamNear(int position)
    {
        var i = position;
        var slack = 0;
        while (i < _data.Length && slack < 4 && PdfLexer.IsWhitespace(_data[i]))
        {
            i++;
            slack++;
        }

        return Matches(EndStreamMarker, i);
    }

    // ---- Object streams ----

    private void LoadObjectStreamMembers(int streamObjectNumber)
    {
        if (!_loadedObjectStreams.Add(streamObjectNumber))
        {
            return;
        }

        if (GetObject(streamObjectNumber) is not CosStream stream)
        {
            return;
        }

        byte[] data;
        try
        {
            data = stream.GetDecodedBytes();
        }
        catch
        {
            return;
        }

        var count = stream.GetAsInt(CosNames.N) ?? 0;
        var first = stream.GetAsInt(CosNames.First) ?? 0;

        var headerParser = new CosObjectParser(new PdfLexer(data), this);
        var members = new List<(int Number, int Offset)>(count);
        for (var i = 0; i < count; i++)
        {
            var numberToken = headerParser.Next();
            var offsetToken = headerParser.Next();
            if (numberToken.Kind != PdfTokenKind.Integer || offsetToken.Kind != PdfTokenKind.Integer)
            {
                break;
            }

            members.Add(((int)numberToken.IntValue, (int)offsetToken.IntValue));
        }

        foreach (var (number, offset) in members)
        {
            if (number <= 0 || _cache.ContainsKey(number))
            {
                continue;
            }

            // Older object streams can carry stale copies of objects superseded by a later
            // incremental update. Only accept members the xref attributes to this stream.
            if (_xref.TryGetValue(number, out var entry)
                && (!entry.InObjectStream || entry.StreamObjectNumber != streamObjectNumber))
            {
                continue;
            }

            var position = first + offset;
            if (position < 0 || position >= data.Length)
            {
                continue;
            }

            var parser = new CosObjectParser(new PdfLexer(data, position), this);
            var obj = parser.ParseObject();
            _cache[number] = obj;
            AttachReference(obj, number);
        }
    }

    // ---- Recovery ----

    private long? LookupScanOffset(int objectNumber)
    {
        _scanTable ??= BuildScanTable();
        return _scanTable.TryGetValue(objectNumber, out var offset) ? offset : null;
    }

    private void RebuildByScan()
    {
        _scanTable ??= BuildScanTable();
        _cache.Clear();
        _loadedObjectStreams.Clear();

        foreach (var (number, offset) in _scanTable)
        {
            _xref[number] = new XrefEntry { Offset = offset };
        }

        // Newest trailer wins; scan backwards so the first dictionary we merge is the newest.
        var searchEnd = _data.Length;
        while (_trailer.GetRaw(CosNames.Root) == null && searchEnd > 0)
        {
            var index = FindLast(TrailerMarker, 0, searchEnd);
            if (index < 0)
            {
                break;
            }

            searchEnd = index;
            try
            {
                var parser = CreateParser(index + TrailerMarker.Length);
                if (parser.ParseObject() is CosDictionary dict && dict.GetRaw(CosNames.Root) != null)
                {
                    MergeTrailer(dict);
                }
            }
            catch
            {
                // Keep scanning earlier trailers.
            }
        }

        if (GetCatalog() != null)
        {
            return;
        }

        // No usable trailer at all: hunt for a catalog object directly, newest first.
        foreach (var number in _scanTable.Keys.OrderByDescending(n => _scanTable![n]))
        {
            var obj = GetObject(number);
            if (obj is CosDictionary dict and not CosStream
                && CosNames.Catalog.Equals(dict.GetAsName(CosNames.Type)))
            {
                _trailer.Put(CosNames.Root, new CosIndirectReference(number, 0) { Resolver = this });
                return;
            }
        }
    }

    private Dictionary<int, long> BuildScanTable()
    {
        var table = new Dictionary<int, long>();

        for (var i = 0; i + 2 < _data.Length; i++)
        {
            if (_data[i] != (byte)'o' || _data[i + 1] != (byte)'b' || _data[i + 2] != (byte)'j')
            {
                continue;
            }

            if (i + 3 < _data.Length && PdfLexer.IsRegular(_data[i + 3]))
            {
                continue;
            }

            var j = i - 1;
            if (j < 0 || !PdfLexer.IsWhitespace(_data[j]))
            {
                continue;
            }

            while (j >= 0 && PdfLexer.IsWhitespace(_data[j]))
            {
                j--;
            }

            var generationEnd = j;
            while (j >= 0 && IsDigit(_data[j]))
            {
                j--;
            }

            if (generationEnd == j)
            {
                continue;
            }

            if (j < 0 || !PdfLexer.IsWhitespace(_data[j]))
            {
                continue;
            }

            while (j >= 0 && PdfLexer.IsWhitespace(_data[j]))
            {
                j--;
            }

            var numberEnd = j;
            while (j >= 0 && IsDigit(_data[j]))
            {
                j--;
            }

            if (numberEnd == j)
            {
                continue;
            }

            var numberStart = j + 1;
            var objectNumber = ParseIntAt(numberStart, numberEnd);
            if (objectNumber > 0)
            {
                // Later occurrences win: appended incremental updates are newer.
                table[objectNumber] = numberStart;
            }
        }

        return table;
    }

    private int ParseIntAt(int start, int endInclusive)
    {
        long value = 0;
        for (var i = start; i <= endInclusive; i++)
        {
            value = value * 10 + (_data[i] - (byte)'0');
            if (value > int.MaxValue)
            {
                return -1;
            }
        }

        return (int)value;
    }

    // ---- Byte helpers ----

    private CosObjectParser CreateParser(int offset) => new(new PdfLexer(_data, offset), this);

    private static bool IsDigit(byte b) => b is >= (byte)'0' and <= (byte)'9';

    private static long ReadBigEndian(byte[] data, int offset, int width)
    {
        long value = 0;
        for (var i = 0; i < width; i++)
        {
            value = value << 8 | data[offset + i];
        }

        return value;
    }

    private bool Matches(byte[] needle, int position)
    {
        if (position < 0 || position + needle.Length > _data.Length)
        {
            return false;
        }

        for (var i = 0; i < needle.Length; i++)
        {
            if (_data[position + i] != needle[i])
            {
                return false;
            }
        }

        return true;
    }

    private int Find(byte[] needle, int start, int end)
    {
        var limit = Math.Min(end, _data.Length) - needle.Length;
        for (var i = Math.Max(0, start); i <= limit; i++)
        {
            if (Matches(needle, i))
            {
                return i;
            }
        }

        return -1;
    }

    private int FindLast(byte[] needle, int start, int end)
    {
        var from = Math.Min(end, _data.Length) - needle.Length;
        for (var i = from; i >= Math.Max(0, start); i--)
        {
            if (Matches(needle, i))
            {
                return i;
            }
        }

        return -1;
    }
}
