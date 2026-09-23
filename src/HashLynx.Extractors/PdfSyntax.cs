using System.Globalization;
using System.Text;

namespace HashLynx.Extractors;

internal sealed record PdfName(string Value);
internal sealed record PdfBytes(byte[] Value);
internal sealed record PdfReference(int Number, int Generation);

/// <summary>Bounded PDF object syntax. Never interprets page content, actions or scripts.</summary>
internal sealed class PdfSyntax(byte[] data, int position, CancellationToken ct)
{
    public int Position { get; set; } = position;
    private int _values;
    public static bool White(byte c) => c is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Delimiter(byte c) => White(c) || c is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    public static void Need(bool condition, string message = "The PDF metadata is malformed, truncated or exceeds the built-in limits.")
    { if (!condition) throw new InvalidDataException(message); }
    public void Skip()
    {
        ct.ThrowIfCancellationRequested();
        while (Position < data.Length)
        {
            if ((Position & 65535) == 0) ct.ThrowIfCancellationRequested();
            if (White(data[Position])) { Position++; continue; }
            if (data[Position] != '%') break;
            while (Position < data.Length && data[Position] is not 10 and not 13)
            { Position++; if ((Position & 65535) == 0) ct.ThrowIfCancellationRequested(); }
        }
    }
    public bool Eat(string word)
    {
        Skip();
        if (Position + word.Length > data.Length) return false;
        for (var i = 0; i < word.Length; i++) if (data[Position + i] != word[i]) return false;
        if (Position + word.Length < data.Length && !Delimiter(data[Position + word.Length])) return false;
        Position += word.Length; return true;
    }
    public void Expect(string word) => Need(Eat(word));
    public string Token()
    {
        Skip(); var start = Position;
        while (Position < data.Length && !Delimiter(data[Position])) { Position++; Need(Position - start <= 128); }
        Need(Position > start); return Encoding.ASCII.GetString(data, start, Position - start);
    }
    public long Integer()
    {
        Need(long.TryParse(Token(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)); return value;
    }
    public object? Value(int depth = 0)
    {
        Skip(); Need(depth <= 32 && ++_values <= 100_000 && Position < data.Length);
        var first = data[Position++];
        if (first == '/')
        {
            var bytes = new List<byte>();
            while (Position < data.Length && !Delimiter(data[Position]))
            {
                var c = data[Position++];
                if (c == '#') { Need(Position + 2 <= data.Length); c = (byte)((Hex(data[Position++]) << 4) | Hex(data[Position++])); }
                Need(c != 0 && bytes.Count < 256); bytes.Add(c);
            }
            return new PdfName(Encoding.Latin1.GetString(bytes.ToArray()));
        }
        if (first == '(')
        {
            var bytes = new List<byte>(); var nesting = 1;
            while (nesting > 0)
            {
                ct.ThrowIfCancellationRequested(); Need(Position < data.Length && bytes.Count <= 65536);
                var c = data[Position++];
                if (c == '\\')
                {
                    Need(Position < data.Length); c = data[Position++];
                    if (c is 10 or 13) { if (c == 13 && Position < data.Length && data[Position] == 10) Position++; continue; }
                    if (c is >= (byte)'0' and <= (byte)'7')
                    {
                        var octal = c - '0';
                        for (var i = 1; i < 3 && Position < data.Length && data[Position] is >= (byte)'0' and <= (byte)'7'; i++) octal = octal * 8 + data[Position++] - '0';
                        bytes.Add((byte)octal); continue;
                    }
                    c = c switch { (byte)'n' => 10, (byte)'r' => 13, (byte)'t' => 9, (byte)'b' => 8, (byte)'f' => 12, _ => c };
                }
                else if (c == '(') { Need(++nesting <= 32); }
                else if (c == ')') { if (--nesting == 0) break; }
                else if (c == 13) { if (Position < data.Length && data[Position] == 10) Position++; c = 10; }
                bytes.Add(c);
            }
            return new PdfBytes(bytes.ToArray());
        }
        if (first == '<')
        {
            if (Position < data.Length && data[Position] == '<')
            {
                Position++; var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
                while (true)
                {
                    Skip(); Need(Position < data.Length);
                    if (data[Position] == '>') { Position++; Need(Position < data.Length && data[Position++] == '>'); return dictionary; }
                    Need(dictionary.Count < 4096);
                    var name = Value(depth + 1) as PdfName; Need(name is not null);
                    Need(dictionary.TryAdd(name!.Value, Value(depth + 1)), "The PDF metadata contains a duplicate dictionary key.");
                }
            }
            var bytes = new List<byte>(); var high = -1;
            while (true)
            {
                if ((Position & 65535) == 0) ct.ThrowIfCancellationRequested();
                Need(Position < data.Length && bytes.Count <= 65536); var c = data[Position++];
                if (c == '>') break;
                if (White(c)) continue;
                var nibble = Hex(c); if (high < 0) high = nibble; else { bytes.Add((byte)(high * 16 + nibble)); high = -1; }
            }
            if (high >= 0) bytes.Add((byte)(high * 16)); return new PdfBytes(bytes.ToArray());
        }
        if (first == '[')
        {
            var array = new List<object?>();
            while (true)
            {
                Skip(); Need(Position < data.Length && array.Count <= 16384);
                if (data[Position] == ']') { Position++; return array; }
                array.Add(Value(depth + 1));
            }
        }
        Position--; var token = Token();
        if (token == "null") return null;
        if (token is "true" or "false") return token == "true";
        if (long.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number))
        {
            var saved = Position; Skip();
            if (number is >= 0 and <= int.MaxValue && Position < data.Length && data[Position] is >= (byte)'0' and <= (byte)'9')
            {
                var isInteger = long.TryParse(Token(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var generation);
                if (isInteger && Eat("R")) { Need(generation is >= 0 and <= 65535); return new PdfReference((int)number, (int)generation); }
            }
            Position = saved; return number;
        }
        Need(double.TryParse(token, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var real) && double.IsFinite(real));
        return real;
    }
    private static int Hex(byte value) => value switch
    {
        >= (byte)'0' and <= (byte)'9' => value - '0', >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
        >= (byte)'A' and <= (byte)'F' => value - 'A' + 10, _ => throw new InvalidDataException("The PDF metadata contains invalid hexadecimal data.")
    };
}
