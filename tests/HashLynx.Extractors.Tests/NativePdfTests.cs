using System.Security.Cryptography;
using System.Text;
using System.IO.Compression;
using System.Buffers.Binary;

namespace HashLynx.Extractors.Tests;

public sealed class NativePdfTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-pdf-" + Guid.NewGuid().ToString("N"));
    public NativePdfTests() => Directory.CreateDirectory(root);
    private async Task<ExtractionResult> Extract(byte[] data)
    {
        var path = Path.Combine(root, "document.pdf"); await File.WriteAllBytesAsync(path, data);
        var result = await new PdfHashExtractor().ExtractAsync(path);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(path)));
        return result;
    }
    [Theory]
    [InlineData("r2", 10400)] [InlineData("r3", 10500)] [InlineData("r4-rc4", 10500)]
    [InlineData("r4-aes", 10500)] [InlineData("r5", 10600)] [InlineData("r6", 10700)] [InlineData("r6-linearized", 10700)]
    public async Task IndependentEncryptedPdfsMatchPdf2John(string name, int mode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Pdf", name);
        var result = await Extract(await File.ReadAllBytesAsync(path + ".pdf"));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal((await File.ReadAllTextAsync(path + ".expected")).Trim(), Assert.Single(result.Hashes));
        Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
    }
    private const string Id = "000102030405060708090a0b0c0d0e0f";
    private static string Encryption => $"<< /Filter /Standard /V 1 /R 2 /P -4 /O <{new string('1', 64)}> /U <{new string('2', 64)}> >>";
    private static byte[] Document(string? encryption = null, string? extra = null)
    {
        var text = "%PDF-1.4\n"; var offset = text.Length;
        text += "1 0 obj\n" + (encryption ?? Encryption) + "\nendobj\n";
        var xref = text.Length;
        text += $"xref\n0 2\n0000000000 65535 f\n{offset:0000000000} 00000 n\ntrailer\n<< /Size 2 /Encrypt 1 0 R /ID [<{Id}><{Id}>] {extra} >>\nstartxref\n{xref}\n%%EOF\n";
        return Encoding.ASCII.GetBytes(text);
    }
    [Fact]
    public async Task EscapedNamesAndLiteralBytesAreDecoded()
    {
        var literal = "(" + string.Concat(Enumerable.Repeat("\\042", 32)) + ")";
        var result = await Extract(Document(Encryption.Replace("/Filter /Standard", "/Fil#74er /St#61ndard")
            .Replace($"<{new string('2', 64)}>", literal)));
        Assert.True(result.Success); Assert.Contains("*32*" + new string('2', 64), Assert.Single(result.Hashes));
    }
    [Theory]
    [InlineData("/R 2", "/R 8")] [InlineData("/P -4", "/P 2147483648")]
    [InlineData("/Standard", "/Adobe.PubSec")] [InlineData("/R 2", "/R 2 /R 2")]
    [InlineData("/V 1", "/V 1 /Length 128")]
    [InlineData("/R 2", "/R 2 /EncryptMetadata 1")]
    [InlineData("/R 2", "/R 2 /SubFilter /adbe.pkcs7.s5")]
    public async Task InvalidOrUnsupportedEncryptionFailsWithoutHashes(string old, string replacement)
    {
        var result = await Extract(Document(Encryption.Replace(old, replacement)));
        Assert.False(result.Success); Assert.Empty(result.Hashes); Assert.NotEmpty(result.Diagnostics);
    }
    [Fact]
    public async Task LatestIncrementalObjectOverridesStaleEncryption()
    {
        var original = Encoding.ASCII.GetString(Document()); var previous = OriginalXref(original);
        var offset = original.Length; var text = original + "1 0 obj\n" + Encryption.Replace("/P -4", "/P -8") + "\nendobj\n";
        var xref = text.Length;
        text += $"xref\n1 1\n{offset:0000000000} 00000 n\ntrailer\n<< /Size 2 /Prev {previous} >>\nstartxref\n{xref}\n%%EOF\n";
        var result = await Extract(Encoding.ASCII.GetBytes(text));
        Assert.True(result.Success); Assert.Contains("*40*-8*", Assert.Single(result.Hashes));
    }
    [Theory]
    [InlineData("1 1\n0000000000 00001 f", "")]
    [InlineData("0 1\n0000000000 65535 f", "/Encrypt null")]
    [InlineData("1 1\n0000000000 00000 n", "")]
    public async Task LatestFreeNullOrInvalidEntriesNeverFallBackToStaleMetadata(string entries, string extra)
    {
        var original = Encoding.ASCII.GetString(Document()); var previous = OriginalXref(original);
        var text = original + $"xref\n{entries}\ntrailer\n<< /Size 2 /Prev {previous} {extra} >>\nstartxref\n{original.Length}\n%%EOF\n";
        var result = await Extract(Encoding.ASCII.GetBytes(text));
        Assert.False(result.Success); Assert.Empty(result.Hashes);
    }
    [Fact]
    public async Task CyclicCrossReferenceAndMetadataReferencesFail()
    {
        var normal = Encoding.ASCII.GetString(Document()); var xref = OriginalXref(normal);
        Assert.False((await Extract(Document(extra: $"/Prev {xref}"))).Success);
        Assert.False((await Extract(Document("1 0 R"))).Success);
        Assert.False((await Extract(Document(extra: "/XRefStm 9"))).Success);
    }
    [Fact]
    public async Task UnencryptedMalformedAndOversizedDocumentsAreReported()
    {
        var normal = Encoding.ASCII.GetString(Document());
        Assert.False((await Extract(Encoding.ASCII.GetBytes(normal.Replace("/Encrypt 1 0 R", "")))).Success);
        Assert.False((await Extract(Document("<< /Filter /Standard /O (unterminated >>"))).Success);
        Assert.False((await Extract(Document(extra: "/Foo " + new string('[', 40)))).Success);
        Assert.False((await Extract(Encoding.ASCII.GetBytes(normal.Replace("0 2\n", "9223372036854775807 2\n")))).Success);
        var path = Path.Combine(root, "oversized.pdf");
        using (var file = File.Create(path)) file.SetLength(64 * 1024 * 1024 + 1);
        Assert.Contains((await new PdfHashExtractor().ExtractAsync(path)).Diagnostics, message => message.Contains("64 MiB"));
    }
    [Fact]
    public async Task TruncationsAndRandomMutationsStayBounded()
    {
        var data = Document();
        for (var length = 0; length < data.Length - 6; length += 13)
            Assert.False((await Extract(data[..length])).Success);
        var random = new Random(4096);
        for (var i = 0; i < 80; i++)
        {
            var mutated = (byte[])data.Clone(); mutated[random.Next(mutated.Length)] = (byte)random.Next(256);
            var result = await Extract(mutated);
            if (!result.Success) Assert.Empty(result.Hashes);
        }
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PdfHashExtractor().ExtractAsync(Path.Combine(root, "document.pdf"), cancellation.Token));
    }
    private static int OriginalXref(string text) => int.Parse(text[(text.LastIndexOf("startxref\n", StringComparison.Ordinal) + 10)..].Split('\n')[0]);

    private static byte[] StreamDocument(int predictor = -1, bool hybrid = false, bool compressedReference = false, bool expandBeyondCount = false)
    {
        var prefix = "%PDF-1.5\n1 0 obj\n" + Encryption + "\nendobj\n";
        var records = new byte[21]; records[5] = 255; records[6] = 255;
        records[7] = (byte)(compressedReference ? 2 : 1); BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(8), 9);
        records[14] = 1; BinaryPrimitives.WriteUInt32BigEndian(records.AsSpan(15), (uint)prefix.Length);
        byte[] packed = records;
        if (predictor >= 0)
        {
            using var rows = new MemoryStream();
            for (var row = 0; row < 3; row++)
            {
                rows.WriteByte((byte)predictor);
                for (var column = 0; column < 7; column++)
                {
                    var a = column == 0 ? 0 : records[row * 7 + column - 1];
                    var b = row == 0 ? 0 : records[(row - 1) * 7 + column];
                    var c = row == 0 || column == 0 ? 0 : records[(row - 1) * 7 + column - 1];
                    var distances = new[] { Math.Abs(b - c), Math.Abs(a - c), Math.Abs(a + b - 2 * c) };
                    var nearest = System.Array.IndexOf(distances, distances.Min());
                    var prediction = predictor switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => new[] { a, b, c }[nearest] };
                    rows.WriteByte(unchecked((byte)(records[row * 7 + column] - prediction)));
                }
            }
            if (expandBeyondCount) rows.Write(new byte[65536]);
            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true)) zlib.Write(rows.ToArray());
            packed = compressed.ToArray();
        }
        using var file = new MemoryStream();
        void Write(string value) => file.Write(Encoding.ASCII.GetBytes(value));
        Write(prefix);
        Write($"2 0 obj\n<< /Type /XRef /Size 3 /W [1 4 2] /Length {packed.Length} /Encrypt 1 0 R /ID [<{Id}><{Id}>] ");
        if (predictor >= 0) Write("/Filter [/FlateDecode] /DecodeParms [<< /Predictor 15 /Columns 7 >>] ");
        Write(">>\nstream\n"); file.Write(packed); Write("\nendstream\nendobj\n");
        long finalOffset = prefix.Length;
        if (hybrid)
        {
            finalOffset = file.Length;
            Write($"xref\n1 1\n0000000000 00000 n\ntrailer\n<< /Size 3 /XRefStm {prefix.Length} /Encrypt 1 0 R /ID [<{Id}><{Id}>] >>\n");
        }
        Write($"startxref\n{finalOffset}\n%%EOF\n"); return file.ToArray();
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task CrossReferenceStreamsSupportUncompressedAndPngFilters(int predictor)
    {
        var expected = Assert.Single((await Extract(Document())).Hashes);
        var result = await Extract(StreamDocument(predictor));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics)); Assert.Equal(expected, Assert.Single(result.Hashes));
    }
    [Fact]
    public async Task HybridStreamOverridesTableAndInvalidStreamsFail()
    {
        Assert.True((await Extract(StreamDocument(2, hybrid: true))).Success);
        Assert.False((await Extract(StreamDocument(2, compressedReference: true))).Success);
        Assert.False((await Extract(StreamDocument(2, expandBeyondCount: true))).Success);
        var data = StreamDocument();
        var text = Encoding.Latin1.GetString(data);
        Assert.False((await Extract(Encoding.Latin1.GetBytes(text.Replace("/W [1 4 2]", "/W [1 8 2]")))).Success);
        Assert.False((await Extract(Encoding.Latin1.GetBytes(text.Replace("/Length 21", "/Length 20")))).Success);
    }
    public void Dispose() => Directory.Delete(root, true);
}
