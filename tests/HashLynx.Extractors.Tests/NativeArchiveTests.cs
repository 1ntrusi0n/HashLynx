using System.Buffers.Binary;
using System.Security.Cryptography;
using HashLynx.Extractors;

namespace HashLynx.Extractors.Tests;

public sealed class NativeArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-archives-" + Guid.NewGuid().ToString("N"));
    public NativeArchiveTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Theory]
    [InlineData("rar")] [InlineData("7z")]
    public async Task NativeNeedsNoToolsAndExplicitMissingOverrideIsHonored(string format)
    {
        var native = Extractor(format);
        Assert.True((await native.ValidateAsync()).IsAvailable);
        Assert.Null((await native.GetAvailabilityAsync()).ToolPath);
        var configuration = new ExtractorConfiguration { ToolPath = Path.Combine(_root, "missing.exe") };
        IHashExtractor external = format == "rar" ? new RarHashExtractor(configuration, new RejectRunner()) : new SevenZipHashExtractor(configuration, new RejectRunner());
        Assert.False((await external.GetAvailabilityAsync()).IsAvailable);
    }

    [Theory]
    [InlineData("headers", "$7z$0$")] [InlineData("lzma2", "$7z$2$")]
    [InlineData("solid", "$7z$2$")] [InlineData("plain", "$7z$0$")]
    public async Task RealSyntheticSevenZipFixturesSupportHeadersCompressionAndSolidData(string name, string prefix)
    {
        var bytes = SevenZipFixture(name); var result = await Extract("7z", bytes);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(11600, Assert.Single(result.SuggestedHashcatModes));
        Assert.StartsWith(prefix, Assert.Single(result.Hashes));
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task SignatureRecognitionIgnoresMisleadingExtensionAndSourceIsUnchanged()
    {
        var bytes = NativeArchiveFixtures.Solid; var path = Path.Combine(_root, "input & symbols.rar");
        await File.WriteAllBytesAsync(path, bytes); File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal("7z", Assert.Single(await ExtractorRegistry.CreateDefault().FindCandidatesAsync(path)).Id);
            Assert.True((await new SevenZipHashExtractor().ExtractAsync(path)).Success);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task SolidStreamCombinesMemberCrcsInsteadOfTruncatingVerificationToFirstFile()
    {
        var result = await Extract("7z", NativeArchiveFixtures.Solid);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var allContent = System.Text.Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("HashLynx archive fixture content.\r\n", 3)) +
            string.Concat(Enumerable.Repeat("Second fixture content.\r\n", 2)));
        var fields = Assert.Single(result.Hashes).Split('$');
        Assert.Equal(Crc(allContent).ToString(System.Globalization.CultureInfo.InvariantCulture), fields[8]);
        Assert.Equal("155", fields[12]);
    }

    [Theory]
    [InlineData("start-crc")] [InlineData("next-crc")] [InlineData("offset")]
    [InlineData("huge-header")] [InlineData("truncated")] [InlineData("coder-cycle")]
    [InlineData("unsupported-coder")] [InlineData("lzma-corrupt")] [InlineData("lzma-dictionary")] [InlineData("sfx")]
    public async Task SevenZipRejectsMalformedOrUnsupportedMetadata(string mutation)
    {
        var bytes = mutation.StartsWith("lzma-", StringComparison.Ordinal) ? NativeArchiveFixtures.Solid : NativeArchiveFixtures.Lzma2;
        var offset = checked(32 + (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
        switch (mutation)
        {
            case "start-crc": bytes[8] ^= 1; break;
            case "next-crc": bytes[^1] ^= 1; break;
            case "offset": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), ulong.MaxValue); FixSevenZipCrc(bytes, false); break;
            case "huge-header": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), 16 * 1024 * 1024); FixSevenZipCrc(bytes, false); break;
            case "truncated": bytes = bytes[..^5]; break;
            case "coder-cycle":
                // AES -> LZMA2 has one bind pair (input 1, output 0); make it self-referential.
                var binding = bytes.AsSpan(offset).IndexOf(Convert.FromHexString("2121010001000c"));
                Assert.True(binding >= 0); bytes[offset + binding + 4] = 0; FixSevenZipCrc(bytes, true); break;
            case "unsupported-coder":
                var coder = bytes.AsSpan(offset).IndexOf(Convert.FromHexString("06f10701"));
                Assert.True(coder >= 0); bytes[offset + coder] = 0x7f; FixSevenZipCrc(bytes, true); break;
            case "lzma-corrupt": bytes[112] ^= 0xff; break;
            case "lzma-dictionary":
                var properties = bytes.AsSpan(offset).IndexOf(Convert.FromHexString("5d00100000"));
                Assert.True(properties >= 0); bytes.AsSpan(offset + properties + 1, 4).Fill(255); FixSevenZipCrc(bytes, true); break;
            case "sfx": bytes = [.. "MZ"u8.ToArray(), .. bytes]; break;
        }
        var result = await Extract("7z", bytes);
        Assert.False(result.Success); Assert.Empty(result.Hashes); Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData(false, 23700)] [InlineData(true, 23800)]
    public async Task Rar3StoredAndCompressedMembersProduceInlineHash(bool compressed, int mode)
    {
        var result = await Extract("rar", Rar3(compressed: compressed));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal("$RAR3$*1*0001020304050607*12345678*16*8*1*000102030405060708090a0b0c0d0e0f*" + (compressed ? "33" : "30"), Assert.Single(result.Hashes));
    }

    [Fact]
    public async Task Rar3HeaderEncryptionUsesEndBlockSaltAndCiphertext()
    {
        var result = await Extract("rar", Rar3(headers: true));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(12500, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal("$RAR3$*0*18191a1b1c1d1e1f*202122232425262728292a2b2c2d2e2f", Assert.Single(result.Hashes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Rar5FileAndHeaderVerifiersReturnCorrectFields(bool headers)
    {
        var result = await Extract("rar", Rar5(headers: headers));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(13000, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal("$rar5$16$000102030405060708090a0b0c0d0e0f$15$101112131415161718191a1b1c1d1e1f$8$2021222324252627", Assert.Single(result.Hashes));
    }

    [Theory]
    [InlineData("rar3-crc")] [InlineData("rar3-truncated")] [InlineData("rar3-volume")]
    [InlineData("rar3-solid")] [InlineData("rar3-plain")] [InlineData("rar5-checksum")]
    [InlineData("rar5-no-check")] [InlineData("rar5-cost")] [InlineData("rar5-truncated")]
    [InlineData("rar5-header-crc")] [InlineData("rar5-overflow")]
    public async Task RarRejectsMalformedAndUnsupportedArchives(string mutation)
    {
        byte[] bytes;
        switch (mutation)
        {
            case "rar3-crc": bytes = Rar3(); bytes[7] ^= 1; break;
            case "rar3-truncated": bytes = Rar3()[..^5]; break;
            case "rar3-volume": bytes = Rar3(volume: true); break;
            case "rar3-solid": bytes = Rar3(solid: true); break;
            case "rar3-plain": bytes = Rar3(encrypted: false); break;
            case "rar5-checksum": bytes = Rar5(badCheck: true); break;
            case "rar5-no-check": bytes = Rar5(checkFlag: false); break;
            case "rar5-cost": bytes = Rar5(power: 63); break;
            case "rar5-truncated": bytes = Rar5()[..^1]; break;
            case "rar5-header-crc": bytes = Rar5(); bytes[8] ^= 1; break;
            default: bytes = [.. Convert.FromHexString("526172211a070100"), 0, 0, 0, 0, .. Enumerable.Repeat((byte)255, 10)]; break;
        }
        var result = await Extract("rar", bytes);
        Assert.False(result.Success); Assert.Empty(result.Hashes); Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("rar")] [InlineData("7z")]
    public async Task CancellationPropagatesAndRandomInputDoesNotCrash(string format)
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Extractor(format).ExtractAsync("unused", new CancellationToken(true)));
        var random = new Random(17);
        for (var i = 0; i < 25; i++)
        {
            var bytes = new byte[random.Next(1, 256)]; random.NextBytes(bytes);
            var result = await Extract(format, bytes); Assert.False(result.Success); Assert.Empty(result.Hashes);
        }
    }

    private async Task<ExtractionResult> Extract(string format, byte[] bytes)
    {
        var path = Path.Combine(_root, Guid.NewGuid() + ".archive"); await File.WriteAllBytesAsync(path, bytes);
        return await Extractor(format).ExtractAsync(path);
    }

    [Fact]
    public async Task ChecksummedMutatedSevenZipMetadataProducesAResultWithoutEscapingParserErrors()
    {
        var random = new Random(718);
        for (var index = 0; index < 200; index++)
        {
            var bytes = NativeArchiveFixtures.Lzma2;
            var offset = checked(32 + (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
            bytes[random.Next(offset, bytes.Length)] ^= (byte)random.Next(1, 256);
            FixSevenZipCrc(bytes, true);
            var result = await Extract("7z", bytes);
            Assert.InRange(result.Hashes.Count, 0, 1);
            if (!result.Success) Assert.Empty(result.Hashes);
        }
    }
    private static IHashExtractor Extractor(string format) => format == "rar" ? new RarHashExtractor(runner: new RejectRunner()) : new SevenZipHashExtractor(runner: new RejectRunner());
    private sealed class RejectRunner : IExtractorProcessRunner
    {
        public Task<ExtractorProcessResult> RunAsync(ExtractorProcessRequest request, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Native extraction must not launch a process.");
    }
    private static byte[] SevenZipFixture(string name) => name switch { "headers" => NativeArchiveFixtures.Headers, "solid" => NativeArchiveFixtures.Solid, "plain" => NativeArchiveFixtures.Plain, _ => NativeArchiveFixtures.Lzma2 };
    private static void FixSevenZipCrc(byte[] bytes, bool next)
    {
        if (next)
        {
            var offset = checked(32 + (int)BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(12)));
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(28), Crc(bytes.AsSpan(offset)));
        }
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), Crc(bytes.AsSpan(12, 20)));
    }
    private static uint Crc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) { crc ^= value; for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0 : 0xedb88320); }
        return ~crc;
    }
    private static byte[] Seq(int start, int count) => Enumerable.Range(start, count).Select(i => (byte)i).ToArray();
    private static byte[] Rar3(bool compressed = false, bool headers = false, bool volume = false, bool solid = false, bool encrypted = true)
    {
        byte[] Block(byte type, ushort flags, byte[] body)
        {
            var bytes = new byte[7 + body.Length]; bytes[2] = type;
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(3), flags); BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(5), (ushort)bytes.Length);
            body.CopyTo(bytes, 7); BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)Crc(bytes.AsSpan(2))); return bytes;
        }
        var main = Block(0x73, (ushort)((headers ? 128 : 0) | (volume ? 1 : 0)), new byte[6]);
        if (headers) return [.. Convert.FromHexString("526172211a0700"), .. main, .. Seq(0, 48)];
        var file = new byte[34]; BinaryPrimitives.WriteUInt32LittleEndian(file, 16); BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(4), 8);
        Convert.FromHexString("12345678").CopyTo(file, 9); file[17] = 29; file[18] = compressed ? (byte)0x33 : (byte)0x30;
        file[19] = 1; file[25] = (byte)'a'; Seq(0, 8).CopyTo(file, 26);
        return [.. Convert.FromHexString("526172211a0700"), .. main, .. Block(0x74, (ushort)(0x8400 | (encrypted ? 4 : 0) | (solid ? 16 : 0)), file), .. Seq(0, 16), .. Block(0x7b, 0, [])];
    }
    private static byte[] Rar5(bool headers = false, bool badCheck = false, bool checkFlag = true, byte power = 15)
    {
        byte[] Block(byte[] body)
        {
            Assert.True(body.Length < 128); byte[] bytes = [0, 0, 0, 0, (byte)body.Length, .. body];
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, Crc(bytes.AsSpan(4))); return bytes;
        }
        var check = Seq(32, 8); var checksum = SHA256.HashData(check)[..4]; if (badCheck) checksum[0] ^= 1;
        byte[] crypt = [0, checkFlag ? (byte)1 : (byte)0, power, .. Seq(0, 16), .. (headers ? Array.Empty<byte>() : Seq(16, 16)), .. check, .. checksum];
        var signature = Convert.FromHexString("526172211a070100");
        if (headers) return [.. signature, .. Block([4, 0, .. crypt]), .. Seq(16, 16), .. new byte[16]];
        byte[] extra = [(byte)(crypt.Length + 1), 1, .. crypt];
        return [.. signature, .. Block([1, 0, 0]), .. Block([2, 3, (byte)extra.Length, 16, 0, 8, 0, 0, 0, 1, (byte)'a', .. extra]), .. new byte[16], .. Block([5, 0, 0])];
    }
}
