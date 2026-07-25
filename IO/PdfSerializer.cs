using System.Globalization;
using System.Text;
using FrontEndSuite.PdfPlatform.Cos;

namespace FrontEndSuite.PdfPlatform.IO;

public sealed record PdfSaveOptions
{
    /// <summary>
    /// Pack non-stream objects into object streams and write an xref stream ("full compression").
    /// Matches iText's WriterProperties.SetFullCompressionMode(true).
    /// </summary>
    public bool UseObjectStreams { get; init; }

    /// <summary>Flate-compress streams that have no filter yet (e.g. newly added content streams).</summary>
    public bool CompressNewStreams { get; init; } = true;
}

/// <summary>
/// Writes a document as a fresh PDF file. Objects are collected by reachability from the catalog
/// (dropping superseded generations and structural artifacts like old xref/object streams) and
/// renumbered compactly. Dangling references serialize as null, as the PDF spec prescribes.
/// </summary>
public sealed class PdfSerializer
{
    private const int ObjectsPerObjectStream = 200;
    private const int CompressionThresholdBytes = 32;

    private readonly Dictionary<CosObject, int> _numbers = new(ReferenceEqualityComparer.Instance);
    private readonly List<CosObject> _objects = new();
    private readonly PdfSaveOptions _options;

    private PdfSerializer(PdfSaveOptions options)
    {
        _options = options;
    }

    public static byte[] Serialize(CosDictionary catalog, CosDictionary? info, string version, PdfSaveOptions? options = null)
    {
        using var ms = new MemoryStream();
        Serialize(catalog, info, version, ms, options);
        return ms.ToArray();
    }

    /// <summary>Serializes directly to a stream; classic mode never buffers the whole file.</summary>
    public static void Serialize(CosDictionary catalog, CosDictionary? info, string version, Stream output, PdfSaveOptions? options = null)
    {
        new PdfSerializer(options ?? new PdfSaveOptions()).SerializeCore(catalog, info, version, output);
    }

    private void SerializeCore(CosDictionary catalog, CosDictionary? info, string version, Stream output)
    {
        var counting = new CountingStream(output);
        var rootNumber = NumberFor(catalog);
        var infoNumber = info != null ? NumberFor(info) : (int?)null;

        if (_options.UseObjectStreams)
        {
            if (CompareVersions(version, "1.5") < 0)
            {
                version = "1.5";
            }

            var bodies = new List<byte[]>();
            for (var i = 0; i < _objects.Count; i++)
            {
                // NumberFor grows _objects as bodies reveal new references; the loop bound is live.
                bodies.Add(SerializeBody(_objects[i]));
            }

            AssembleWithObjectStreams(counting, bodies, rootNumber, infoNumber, version);
            return;
        }

        AssembleClassicStreaming(counting, rootNumber, infoNumber, version);
    }

    private void AssembleClassicStreaming(CountingStream ms, int rootNumber, int? infoNumber, string version)
    {
        WriteHeader(ms, version);

        var offsets = new List<long>();
        for (var i = 0; i < _objects.Count; i++)
        {
            // Objects stream out as they are discovered; NumberFor keeps growing the list.
            offsets.Add(ms.Written);
            Write(ms, $"{i + 1} 0 obj\n");
            var obj = _objects[i];
            if (obj is CosStream stream)
            {
                WriteStreamObject(ms, stream);
            }
            else
            {
                WriteValue(ms, obj);
            }

            Write(ms, "\nendobj\n");
        }

        var xrefPosition = ms.Written;
        var size = offsets.Count + 1;
        Write(ms, $"xref\n0 {size}\n");
        Write(ms, "0000000000 65535 f\r\n");
        foreach (var offset in offsets)
        {
            Write(ms, $"{offset.ToString("D10", CultureInfo.InvariantCulture)} 00000 n\r\n");
        }

        Write(ms, $"trailer\n<< /Size {size} /Root {rootNumber} 0 R");
        if (infoNumber != null)
        {
            Write(ms, $" /Info {infoNumber.Value} 0 R");
        }

        Write(ms, $" >>\nstartxref\n{xrefPosition}\n%%EOF");
    }

    private sealed class CountingStream : Stream
    {
        private readonly Stream _inner;

        public CountingStream(Stream inner)
        {
            _inner = inner;
        }

        public long Written { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Written;
        public override long Position { get => Written; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
            Written += count;
        }

        public override void WriteByte(byte value)
        {
            _inner.WriteByte(value);
            Written += 1;
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private int NumberFor(CosObject obj)
    {
        if (_numbers.TryGetValue(obj, out var number))
        {
            return number;
        }

        number = _objects.Count + 1;
        _numbers[obj] = number;
        _objects.Add(obj);
        return number;
    }

    // ---- Object bodies ----

    private byte[] SerializeBody(CosObject obj)
    {
        using var ms = new MemoryStream();
        if (obj is CosStream stream)
        {
            WriteStreamObject(ms, stream);
        }
        else
        {
            WriteValue(ms, obj);
        }

        return ms.ToArray();
    }

    private void WriteValue(Stream ms, CosObject value)
    {
        switch (value)
        {
            case CosIndirectReference reference:
                var target = reference.Resolve();
                if (target is CosNull)
                {
                    Write(ms, "null");
                }
                else
                {
                    Write(ms, $"{NumberFor(target)} 0 R");
                }

                break;

            case CosStream stream:
                // Streams are only legal as indirect objects; promote nested occurrences.
                Write(ms, $"{NumberFor(stream)} 0 R");
                break;

            case CosDictionary dictionary:
                WriteDictionary(ms, dictionary, lengthOverride: null, addFlateFilter: false);
                break;

            case CosArray array:
                Write(ms, "[");
                foreach (var item in array)
                {
                    Write(ms, " ");
                    WriteValue(ms, item);
                }

                Write(ms, " ]");
                break;

            case CosName name:
                WriteName(ms, name);
                break;

            case CosString text:
                WriteString(ms, text);
                break;

            case CosNumber number:
                Write(ms, FormatNumber(number));
                break;

            case CosBoolean boolean:
                Write(ms, boolean.Value ? "true" : "false");
                break;

            default:
                Write(ms, "null");
                break;
        }
    }

    private void WriteStreamObject(Stream ms, CosStream stream)
    {
        var data = stream.RawData;
        var addFlateFilter = false;
        if (_options.CompressNewStreams
            && data.Length > CompressionThresholdBytes
            && stream.Get(CosNames.Filter) == null)
        {
            var compressed = FlateCodec.Encode(data);
            if (compressed.Length < data.Length)
            {
                data = compressed;
                addFlateFilter = true;
            }
        }

        WriteDictionary(ms, stream, lengthOverride: data.Length, addFlateFilter: addFlateFilter);
        Write(ms, "\nstream\n");
        ms.Write(data, 0, data.Length);
        Write(ms, "\nendstream");
    }

    private void WriteDictionary(Stream ms, CosDictionary dictionary, int? lengthOverride, bool addFlateFilter)
    {
        Write(ms, "<<");
        foreach (var (key, value) in dictionary)
        {
            if (lengthOverride != null && key.Equals(CosNames.Length))
            {
                continue;
            }

            Write(ms, " ");
            WriteName(ms, key);
            Write(ms, " ");
            WriteValue(ms, value);
        }

        if (lengthOverride != null)
        {
            Write(ms, $" /Length {lengthOverride.Value}");
        }

        if (addFlateFilter)
        {
            Write(ms, " /Filter /FlateDecode");
        }

        Write(ms, " >>");
    }

    private static void WriteName(Stream ms, CosName name)
    {
        ms.WriteByte((byte)'/');
        foreach (var b in Encoding.UTF8.GetBytes(name.Value))
        {
            if (b > 0x21 && b < 0x7F && b != (byte)'#' && !PdfLexer.IsDelimiter(b))
            {
                ms.WriteByte(b);
            }
            else
            {
                Write(ms, $"#{b:X2}");
            }
        }
    }

    /// <summary>Formats a string object as a content-stream token ((...) or &lt;...&gt;).</summary>
    internal static string FormatStringToken(CosString text)
    {
        using var ms = new MemoryStream();
        WriteString(ms, text);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static void WriteString(Stream ms, CosString text)
    {
        var bytes = text.RawBytes;
        if (text.IsHex || IsMostlyBinary(bytes))
        {
            ms.WriteByte((byte)'<');
            foreach (var b in bytes)
            {
                Write(ms, b.ToString("X2"));
            }

            ms.WriteByte((byte)'>');
            return;
        }

        ms.WriteByte((byte)'(');
        foreach (var b in bytes)
        {
            switch (b)
            {
                case (byte)'\\':
                case (byte)'(':
                case (byte)')':
                    ms.WriteByte((byte)'\\');
                    ms.WriteByte(b);
                    break;
                case (byte)'\r':
                    Write(ms, "\\r");
                    break;
                case (byte)'\n':
                    Write(ms, "\\n");
                    break;
                case (byte)'\t':
                    Write(ms, "\\t");
                    break;
                default:
                    if (b < 32 || b > 126)
                    {
                        Write(ms, $"\\{Convert.ToString(b, 8).PadLeft(3, '0')}");
                    }
                    else
                    {
                        ms.WriteByte(b);
                    }

                    break;
            }
        }

        ms.WriteByte((byte)')');
    }

    private static bool IsMostlyBinary(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return false;
        }

        var binary = 0;
        foreach (var b in bytes)
        {
            if (b < 32 || b > 126)
            {
                binary++;
            }
        }

        return binary * 3 > bytes.Length;
    }

    private static string FormatNumber(CosNumber number)
    {
        if (number.IsInteger)
        {
            return number.LongValue.ToString(CultureInfo.InvariantCulture);
        }

        var value = number.DoubleValue;
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    // ---- File assembly: object streams + xref stream ----

    private void AssembleWithObjectStreams(CountingStream ms, List<byte[]> bodies, int rootNumber, int? infoNumber, string version)
    {
        WriteHeader(ms, version);

        // records[objectNumber] = (type, field2, field3) exactly as they will land in the xref stream.
        var records = new Dictionary<int, (int Type, long Field2, int Field3)>();

        var memberIndexes = new List<int>();
        for (var i = 0; i < bodies.Count; i++)
        {
            if (_objects[i] is CosStream)
            {
                records[i + 1] = (1, ms.Written, 0);
                Write(ms, $"{i + 1} 0 obj\n");
                ms.Write(bodies[i], 0, bodies[i].Length);
                Write(ms, "\nendobj\n");
            }
            else
            {
                memberIndexes.Add(i);
            }
        }

        var nextNumber = bodies.Count + 1;
        for (var chunkStart = 0; chunkStart < memberIndexes.Count; chunkStart += ObjectsPerObjectStream)
        {
            var chunk = memberIndexes.Skip(chunkStart).Take(ObjectsPerObjectStream).ToList();
            var containerNumber = nextNumber++;

            using var payload = new MemoryStream();
            var headerText = new StringBuilder();
            for (var k = 0; k < chunk.Count; k++)
            {
                var body = bodies[chunk[k]];
                headerText.Append(chunk[k] + 1).Append(' ').Append(payload.Position).Append(k + 1 < chunk.Count ? ' ' : '\n');
                payload.Write(body, 0, body.Length);
                payload.WriteByte((byte)'\n');
                records[chunk[k] + 1] = (2, containerNumber, k);
            }

            var headerBytes = Encoding.ASCII.GetBytes(headerText.ToString());
            var combined = new byte[headerBytes.Length + payload.Length];
            headerBytes.CopyTo(combined, 0);
            payload.ToArray().CopyTo(combined, headerBytes.Length);
            var compressed = FlateCodec.Encode(combined);

            records[containerNumber] = (1, ms.Written, 0);
            Write(ms, $"{containerNumber} 0 obj\n");
            Write(ms, $"<< /Type /ObjStm /N {chunk.Count} /First {headerBytes.Length} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            ms.Write(compressed, 0, compressed.Length);
            Write(ms, "\nendstream\nendobj\n");
        }

        // Xref stream: W [1 4 2], entries for 0..size-1 with no /Index (defaults to the full range).
        var xrefNumber = nextNumber++;
        var size = xrefNumber + 1;
        var xrefPosition = ms.Written;
        records[xrefNumber] = (1, xrefPosition, 0);

        var entryData = new byte[size * 7];
        for (var number = 0; number < size; number++)
        {
            var (type, field2, field3) = number == 0
                ? (0, 0L, 65535)
                : records.TryGetValue(number, out var record) ? record : (0, 0L, 65535);
            var row = number * 7;
            entryData[row] = (byte)type;
            entryData[row + 1] = (byte)(field2 >> 24);
            entryData[row + 2] = (byte)(field2 >> 16);
            entryData[row + 3] = (byte)(field2 >> 8);
            entryData[row + 4] = (byte)field2;
            entryData[row + 5] = (byte)(field3 >> 8);
            entryData[row + 6] = (byte)field3;
        }

        var xrefCompressed = FlateCodec.Encode(entryData);
        Write(ms, $"{xrefNumber} 0 obj\n");
        Write(ms, $"<< /Type /XRef /Size {size} /W [ 1 4 2 ] /Root {rootNumber} 0 R");
        if (infoNumber != null)
        {
            Write(ms, $" /Info {infoNumber.Value} 0 R");
        }

        Write(ms, $" /Filter /FlateDecode /Length {xrefCompressed.Length} >>\nstream\n");
        ms.Write(xrefCompressed, 0, xrefCompressed.Length);
        Write(ms, "\nendstream\nendobj\n");
        Write(ms, $"startxref\n{xrefPosition}\n%%EOF");
    }

    // ---- Helpers ----

    private static void WriteHeader(Stream ms, string version)
    {
        Write(ms, $"%PDF-{(string.IsNullOrEmpty(version) ? "1.7" : version)}\n");
        ms.Write(new byte[] { (byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n' }, 0, 6);
    }

    private static int CompareVersions(string left, string right)
    {
        double.TryParse(left, NumberStyles.Float, CultureInfo.InvariantCulture, out var l);
        double.TryParse(right, NumberStyles.Float, CultureInfo.InvariantCulture, out var r);
        return l.CompareTo(r);
    }

    private static void Write(Stream ms, string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        ms.Write(bytes, 0, bytes.Length);
    }
}
