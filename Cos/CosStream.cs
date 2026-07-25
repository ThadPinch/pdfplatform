using FrontEndSuite.PdfPlatform.IO;

namespace FrontEndSuite.PdfPlatform.Cos;

public sealed class CosStream : CosDictionary
{
    public CosStream()
    {
    }

    public CosStream(CosDictionary dictionary, byte[] rawData)
    {
        foreach (var (key, value) in dictionary)
        {
            Put(key, value);
        }

        RawData = rawData;
    }

    private byte[] _rawData = Array.Empty<byte>();
    private MemoryStream? _appendBuffer;

    /// <summary>The stream bytes exactly as stored in the file (still encoded by any filters).</summary>
    public byte[] RawData
    {
        get
        {
            if (_appendBuffer != null)
            {
                _rawData = _appendBuffer.ToArray();
                _appendBuffer = null;
            }

            return _rawData;
        }
        set
        {
            _rawData = value;
            _appendBuffer = null;
        }
    }

    /// <summary>Replaces the stream data with unencoded bytes, dropping any existing filters.</summary>
    public void SetData(byte[] data)
    {
        RawData = data;
        Remove(CosNames.Filter);
        Remove(CosNames.DecodeParms);
        Remove(CosNames.DP);
        Put(CosNames.Length, new CosNumber(data.Length));
    }

    /// <summary>Appends raw bytes to an unfiltered stream (e.g. a newly created content stream).</summary>
    public void AppendData(ReadOnlySpan<byte> bytes)
    {
        if (Get(CosNames.Filter) != null)
        {
            throw new InvalidOperationException("Cannot append raw bytes to a filtered stream; call SetData first.");
        }

        if (_appendBuffer == null)
        {
            _appendBuffer = new MemoryStream();
            _appendBuffer.Write(_rawData, 0, _rawData.Length);
        }

        _appendBuffer.Write(bytes);
        Put(CosNames.Length, new CosNumber(_appendBuffer.Length));
    }

    /// <summary>Appends operator text to an unfiltered stream, encoded byte-per-char (Latin-1).</summary>
    public void AppendString(string text) => AppendData(System.Text.Encoding.Latin1.GetBytes(text));

    /// <summary>
    /// Runs the filter chain and returns the decoded bytes. Only FlateDecode and ASCIIHexDecode are
    /// decodable; anything else (DCTDecode, JPXDecode, ...) is intentionally passthrough-only.
    /// </summary>
    public byte[] GetDecodedBytes()
    {
        if (!TryGetDecodedBytes(out var bytes))
        {
            throw new NotSupportedException($"Stream has an unsupported filter chain: {DescribeFilters()}.");
        }

        return bytes;
    }

    public bool TryGetDecodedBytes(out byte[] bytes)
    {
        var data = RawData;
        foreach (var (filter, parms) in GetFilterChain())
        {
            if (CosNames.FlateDecode.Equals(filter))
            {
                data = FlateCodec.Decode(data, parms);
            }
            else if (CosNames.LZWDecode.Equals(filter))
            {
                data = LzwCodec.Decode(data, parms);
            }
            else if (CosNames.ASCIIHexDecode.Equals(filter))
            {
                data = DecodeAsciiHex(data);
            }
            else if (CosNames.ASCII85Decode.Equals(filter))
            {
                data = DecodeAscii85(data);
            }
            else if (CosNames.RunLengthDecode.Equals(filter))
            {
                data = DecodeRunLength(data);
            }
            else
            {
                bytes = Array.Empty<byte>();
                return false;
            }
        }

        bytes = data;
        return true;
    }

    /// <summary>True when the filter chain contains a filter this library cannot decode.</summary>
    public bool HasUnsupportedFilter
    {
        get
        {
            foreach (var (filter, _) in GetFilterChain())
            {
                if (!CosNames.FlateDecode.Equals(filter)
                    && !CosNames.LZWDecode.Equals(filter)
                    && !CosNames.ASCIIHexDecode.Equals(filter)
                    && !CosNames.ASCII85Decode.Equals(filter)
                    && !CosNames.RunLengthDecode.Equals(filter))
                {
                    return true;
                }
            }

            return false;
        }
    }

    public string DescribeFilters()
    {
        var names = GetFilterChain().Select(f => f.Filter.Value);
        var joined = string.Join(", ", names);
        return joined.Length == 0 ? "(none)" : joined;
    }

    private List<(CosName Filter, CosDictionary? Parms)> GetFilterChain()
    {
        var chain = new List<(CosName, CosDictionary?)>();
        var filter = Get(CosNames.Filter);
        if (filter == null)
        {
            return chain;
        }

        var parms = Get(CosNames.DecodeParms) ?? Get(CosNames.DP);

        if (filter is CosName single)
        {
            chain.Add((single, parms as CosDictionary));
        }
        else if (filter is CosArray filters)
        {
            for (var i = 0; i < filters.Count; i++)
            {
                var name = filters.GetAsName(i);
                if (name == null)
                {
                    continue;
                }

                var itemParms = parms switch
                {
                    CosArray parmsArray => parmsArray.GetAsDictionary(i),
                    CosDictionary parmsDict when i == 0 => parmsDict,
                    _ => null
                };
                chain.Add((name, itemParms));
            }
        }

        return chain;
    }

    private static byte[] DecodeAscii85(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 4 / 5);
        var group = new int[5];
        var count = 0;

        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            if (b == (byte)'~')
            {
                break; // ~> terminator
            }

            if (b == (byte)'z' && count == 0)
            {
                output.WriteByte(0);
                output.WriteByte(0);
                output.WriteByte(0);
                output.WriteByte(0);
                continue;
            }

            if (b < (byte)'!' || b > (byte)'u')
            {
                continue; // whitespace and junk
            }

            group[count++] = b - '!';
            if (count == 5)
            {
                WriteAscii85Group(output, group, 5);
                count = 0;
            }
        }

        if (count > 1)
        {
            for (var i = count; i < 5; i++)
            {
                group[i] = 84; // pad with 'u'
            }

            WriteAscii85Group(output, group, count);
        }

        return output.ToArray();
    }

    private static void WriteAscii85Group(MemoryStream output, int[] group, int sourceChars)
    {
        var value = 0L;
        foreach (var digit in group)
        {
            value = value * 85 + digit;
        }

        Span<byte> bytes = stackalloc byte[4]
        {
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value
        };
        output.Write(bytes[..(sourceChars - 1)]);
    }

    private static byte[] DecodeRunLength(byte[] data)
    {
        using var output = new MemoryStream(data.Length * 2);
        var i = 0;
        while (i < data.Length)
        {
            var length = data[i++];
            if (length == 128)
            {
                break; // EOD
            }

            if (length < 128)
            {
                var copy = Math.Min(length + 1, data.Length - i);
                output.Write(data, i, copy);
                i += copy;
            }
            else if (i < data.Length)
            {
                var repeated = data[i++];
                for (var r = 0; r < 257 - length; r++)
                {
                    output.WriteByte(repeated);
                }
            }
        }

        return output.ToArray();
    }

    private static byte[] DecodeAsciiHex(byte[] data)
    {
        using var output = new MemoryStream(data.Length / 2);
        var haveNibble = false;
        var nibble = 0;
        foreach (var b in data)
        {
            if (b == (byte)'>')
            {
                break;
            }

            var value = b switch
            {
                >= (byte)'0' and <= (byte)'9' => b - '0',
                >= (byte)'A' and <= (byte)'F' => b - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => b - 'a' + 10,
                _ => -1
            };
            if (value < 0)
            {
                continue;
            }

            if (haveNibble)
            {
                output.WriteByte((byte)(nibble << 4 | value));
                haveNibble = false;
            }
            else
            {
                nibble = value;
                haveNibble = true;
            }
        }

        if (haveNibble)
        {
            output.WriteByte((byte)(nibble << 4));
        }

        return output.ToArray();
    }
}
