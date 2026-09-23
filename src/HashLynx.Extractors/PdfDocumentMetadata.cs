using System.IO.Compression;
using static HashLynx.Extractors.PdfSyntax;

namespace HashLynx.Extractors;

/// <summary>Follows the active cross-reference chain instead of searching arbitrary document bytes for keys.</summary>
internal sealed class PdfDocumentMetadata(byte[] data, CancellationToken ct)
{
    private const int MaxEntries = 250_000, MaxStream = 8 * 1024 * 1024;
    private sealed record Location(int Type, long Offset, int Generation);
    private readonly Dictionary<int, Location> _objects = [];
    private readonly HashSet<int> _visited = [];
    private int _entryCount;
    public Dictionary<string, object?> Trailer { get; } = [];

    public void Read()
    {
        Need(data.AsSpan(0, Math.Min(1024, data.Length)).IndexOf("%PDF-"u8) >= 0, "This is not a PDF document.");
        var tailStart = Math.Max(0, data.Length - 65536);
        var tail = data.AsSpan(tailStart); var eof = tail.LastIndexOf("%%EOF"u8);
        Need(eof >= 0 && tail[(eof + 5)..].ToArray().All(White), "The PDF end marker is missing or has unsupported trailing data.");
        var start = tail[..eof].LastIndexOf("startxref"u8); Need(start >= 0, "The PDF cross-reference pointer is missing.");
        var reader = new PdfSyntax(data, tailStart + start, ct); reader.Expect("startxref"); var offset = reader.Integer();
        Need(reader.Position <= tailStart + eof && data.AsSpan(reader.Position, tailStart + eof - reader.Position).ToArray().All(White));
        while (true)
        {
            var section = ReadSection(offset, false);
            foreach (var pair in section.Trailer) Trailer.TryAdd(pair.Key, pair.Value);
            if (section.Trailer.TryGetValue("XRefStm", out var hybrid) && hybrid is not null)
            {
                var supplement = ReadSection(Number(hybrid), true);
                foreach (var pair in supplement.Objects) section.Objects[pair.Key] = pair.Value;
                foreach (var pair in supplement.Trailer) Trailer.TryAdd(pair.Key, pair.Value);
            }
            foreach (var pair in section.Objects) _objects.TryAdd(pair.Key, pair.Value);
            if (!section.Trailer.TryGetValue("Prev", out var previous) || previous is null) break;
            offset = Number(previous);
        }
    }

    private (Dictionary<string, object?> Trailer, Dictionary<int, Location> Objects) ReadSection(long offset, bool requireStream)
    {
        Need(offset >= 0 && offset < data.Length && _visited.Count < 64 && _visited.Add((int)offset), "The PDF cross-reference chain is cyclic, invalid or too long.");
        Need(data[(int)offset] == 'x' || data[(int)offset] is >= (byte)'0' and <= (byte)'9');
        var reader = new PdfSyntax(data, (int)offset, ct); var entries = new Dictionary<int, Location>();
        Dictionary<string, object?> dictionary;
        if (!requireStream && reader.Eat("xref"))
        {
            while (!reader.Eat("trailer"))
            {
                var first = reader.Integer(); var count = reader.Integer(); CheckRange(first, count);
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested(); var location = reader.Integer(); var generation = reader.Integer(); var status = reader.Token();
                    Need(location >= 0 && generation is >= 0 and <= 65535 && status is "n" or "f");
                    Need(entries.TryAdd((int)first + i, new(status == "n" ? 1 : 0, location, (int)generation)), "The PDF cross-reference section contains duplicate entries.");
                }
            }
            dictionary = Dictionary(reader.Value());
        }
        else
        {
            var objectNumber = reader.Integer(); var generation = reader.Integer(); reader.Expect("obj");
            Need(objectNumber > 0 && objectNumber <= int.MaxValue && generation is >= 0 and <= 65535);
            dictionary = Dictionary(reader.Value()); Need(Name(dictionary.GetValueOrDefault("Type")) == "XRef");
            var size = Number(dictionary.GetValueOrDefault("Size"));
            var widths = Array(dictionary.GetValueOrDefault("W")).Select(Number).ToArray();
            Need(widths.Length == 3 && widths.All(width => width is >= 0 and <= 8) && widths.Sum() is > 0 and <= 24);
            var indices = dictionary.TryGetValue("Index", out var index) ? Array(index).Select(Number).ToArray() : [0, size];
            Need(indices.Length > 0 && indices.Length % 2 == 0);
            long total = 0;
            for (var i = 0; i < indices.Length; i += 2) { CheckRange(indices[i], indices[i + 1]); total += indices[i + 1]; }
            var expected = checked((int)(total * widths.Sum())); Need(expected <= MaxStream);
            reader.Expect("stream");
            // PDF requires an EOL after the stream keyword, not arbitrary whitespace skipping.
            Need(reader.Position < data.Length);
            if (data[reader.Position] == 13) reader.Position++;
            else Need(data[reader.Position] == 10);
            if (reader.Position < data.Length && data[reader.Position] == 10) reader.Position++;
            var length = Number(dictionary.GetValueOrDefault("Length"));
            Need(length >= 0 && length <= MaxStream && length <= data.Length - reader.Position);
            var packed = data.AsSpan(reader.Position, (int)length).ToArray(); reader.Position += (int)length;
            reader.Expect("endstream"); reader.Expect("endobj");
            var decoded = Decode(packed, dictionary, expected); var position = 0;
            for (var range = 0; range < indices.Length; range += 2)
            for (var i = 0; i < indices[range + 1]; i++)
            {
                ct.ThrowIfCancellationRequested();
                var type = widths[0] == 0 ? 1 : ReadBig(decoded, ref position, (int)widths[0]);
                var location = ReadBig(decoded, ref position, (int)widths[1]); var gen = ReadBig(decoded, ref position, (int)widths[2]);
                Need(type is >= 0 and <= 2 && gen <= int.MaxValue);
                Need(entries.TryAdd((int)indices[range] + i, new((int)type, location, (int)gen)), "The PDF cross-reference stream contains overlapping ranges.");
            }
        }
        var declaredSize = Number(dictionary.GetValueOrDefault("Size"));
        Need(declaredSize is > 0 and <= int.MaxValue && entries.Keys.All(number => number < declaredSize));
        return (dictionary, entries);
    }

    private void CheckRange(long first, long count)
    {
        Need(first is >= 0 and <= int.MaxValue && count >= 0 && count <= int.MaxValue - first && count <= MaxEntries && _entryCount + count <= MaxEntries,
            "The PDF cross-reference table exceeds the built-in limits.");
        _entryCount += (int)count;
    }
    private static long ReadBig(byte[] bytes, ref int offset, int width)
    {
        ulong value = 0;
        for (var i = 0; i < width; i++) value = (value << 8) | bytes[offset++];
        Need(value <= long.MaxValue); return (long)value;
    }

    private byte[] Decode(byte[] packed, Dictionary<string, object?> dictionary, int expected)
    {
        var filter = dictionary.GetValueOrDefault("Filter"); var parameters = dictionary.GetValueOrDefault("DecodeParms");
        if (filter is List<object?> filters) { Need(filters.Count == 1); filter = filters[0]; }
        if (parameters is List<object?> parms) { Need(parms.Count == 1); parameters = parms[0]; }
        var settings = parameters is null ? new Dictionary<string, object?>() : Dictionary(parameters);
        var predictor = settings.TryGetValue("Predictor", out var p) ? Number(p) : 1;
        Need(predictor == 1 || predictor is >= 10 and <= 15, "This PDF cross-reference predictor is unsupported.");
        var columns = settings.TryGetValue("Columns", out var c) ? Number(c) : 1;
        Need(columns is > 0 and <= 1024 && (!settings.TryGetValue("Colors", out var colors) || Number(colors) == 1) &&
            (!settings.TryGetValue("BitsPerComponent", out var bits) || Number(bits) == 8));
        Need(predictor == 1 || expected % columns == 0);
        var expanded = predictor == 1 ? expected : checked(expected + expected / (int)columns);
        Need(expanded <= MaxStream);
        byte[] raw;
        if (filter is null) { Need(predictor == 1); raw = packed; }
        else
        {
            Need(Name(filter) is "FlateDecode" or "Fl", "The PDF cross-reference stream uses an unsupported compression filter.");
            using var input = new MemoryStream(packed, false); using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            raw = new byte[expanded]; var written = 0;
            while (written < raw.Length)
            {
                ct.ThrowIfCancellationRequested(); var count = zlib.Read(raw, written, Math.Min(65536, raw.Length - written));
                Need(count > 0, "The PDF cross-reference stream ended early."); written += count;
            }
            Need(zlib.ReadByte() == -1, "The PDF cross-reference stream expands beyond its declared entry count.");
        }
        Need(raw.Length == expanded);
        if (predictor == 1) return raw;
        var result = new byte[expected]; var source = 0;
        for (var row = 0; row < expected; row += (int)columns)
        {
            ct.ThrowIfCancellationRequested(); var kind = raw[source++]; Need(kind <= 4);
            for (var col = 0; col < columns; col++)
            {
                var left = col == 0 ? 0 : result[row + col - 1]; var up = row == 0 ? 0 : result[row + col - (int)columns];
                var corner = row == 0 || col == 0 ? 0 : result[row + col - (int)columns - 1];
                var add = kind switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, corner) };
                result[row + col] = unchecked((byte)(raw[source++] + add));
            }
        }
        return result;
    }
    private static int Paeth(int left, int up, int corner)
    {
        var estimate = left + up - corner; var a = Math.Abs(estimate - left); var b = Math.Abs(estimate - up); var c = Math.Abs(estimate - corner);
        return a <= b && a <= c ? left : b <= c ? up : corner;
    }

    public object? Resolve(object? value)
    {
        var visited = new HashSet<PdfReference>();
        while (value is PdfReference reference)
        {
            ct.ThrowIfCancellationRequested(); Need(visited.Count < 16 && visited.Add(reference), "The PDF metadata has a cyclic or excessive reference chain.");
            Need(_objects.TryGetValue(reference.Number, out var location) && location.Type == 1 && location.Generation == reference.Generation,
                "The PDF encryption metadata refers to a missing, freed or compressed object.");
            Need(location!.Offset >= 0 && location.Offset < data.Length);
            Need(data[(int)location.Offset] is >= (byte)'0' and <= (byte)'9', "The PDF object offset is invalid.");
            var parser = new PdfSyntax(data, (int)location.Offset, ct);
            Need(parser.Integer() == reference.Number && parser.Integer() == reference.Generation, "The PDF object does not match its cross-reference entry.");
            parser.Expect("obj"); value = parser.Value(); parser.Expect("endobj");
        }
        return value;
    }
    public static Dictionary<string, object?> Dictionary(object? value) { Need(value is Dictionary<string, object?>); return (Dictionary<string, object?>)value!; }
    public static List<object?> Array(object? value) { Need(value is List<object?>); return (List<object?>)value!; }
    public static long Number(object? value) { Need(value is long); return (long)value!; }
    public static string Name(object? value) { Need(value is PdfName); return ((PdfName)value!).Value; }
}
