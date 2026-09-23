using System.Security.Cryptography;
using static HashLynx.Extractors.Tests.BitLockerFixture;

namespace HashLynx.Extractors.Tests;

public sealed class NativeBitLockerTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-bitlocker-" + Guid.NewGuid().ToString("N"));
    public NativeBitLockerTests() => Directory.CreateDirectory(root);
    private async Task<ExtractionResult> Extract(byte[] data)
    {
        var path = Path.Combine(root, "partition.raw"); await File.WriteAllBytesAsync(path, data);
        var result = await new BitLockerHashExtractor().ExtractAsync(path);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(path)));
        return result;
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task NativePartitionLayoutsExtractAuthenticatedHash(bool toGo, bool usedSpace)
    {
        var data = Image(toGo, usedSpace);
        Assert.Equal("bitlocker", FileTypeInspector.Identify(data));
        var result = await Extract(data);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(Hash, Assert.Single(result.Hashes)); Assert.Equal(22100, Assert.Single(result.SuggestedHashcatModes));
        Assert.Contains("copy #1", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public async Task RegistryIgnoresLegacyOverridesForAllBuiltInFormats()
    {
        var configurations = new[] { "zip", "rar", "7z", "bitlocker", "pdf" }.ToDictionary(id => id, _ => new ExtractorConfiguration { ToolPath = Path.Combine(root, "missing.exe") });
        var registry = ExtractorRegistry.CreateDefault(root, configurations);
        foreach (var extractor in registry.All)
        { Assert.True(extractor.IsBuiltIn); Assert.True((await extractor.ValidateAsync()).IsAvailable); }
    }

    [Fact]
    public async Task ReversedPropertiesAndDuplicateProtectorsAreHandled()
    {
        var result = await Extract(Image(entries: [.. Protector(reverse: true), .. Protector()]));
        Assert.True(result.Success); Assert.Equal(Hash, Assert.Single(result.Hashes));
    }

    [Fact]
    public async Task MultiplePasswordProtectorsStayDistinctAndNonPasswordKeysAreSkipped()
    {
        var salt = Salt; salt[0] ^= 1;
        var result = await Extract(Image(entries: [.. Protector(), .. Protector(salt: salt), .. Protector(0x800), .. Protector(0x100)]));
        Assert.True(result.Success); Assert.Equal(2, result.Hashes.Count);
        Assert.Contains("skipped: 2", Assert.Single(result.Diagnostics));
    }

    [Theory]
    [InlineData(0)] [InlineData(0x100)] [InlineData(0x200)] [InlineData(0x500)] [InlineData(0x800)]
    public async Task UnsupportedProtectorsNeverProduceHashes(int type)
    {
        var result = await Extract(Image(entries: Protector((ushort)type)));
        Assert.False(result.Success); Assert.Empty(result.Hashes); Assert.Contains(result.Diagnostics, text => text.Contains("user-password"));
    }

    [Fact]
    public async Task CorruptPrimaryUsesBackupWithNotice()
    {
        var data = Image(); data[4096] = 0;
        var result = await Extract(data);
        Assert.True(result.Success); Assert.Equal(Hash, Assert.Single(result.Hashes));
        Assert.Contains("copy #2", Assert.Single(result.Diagnostics)); Assert.Contains("older", Assert.Single(result.Diagnostics));
    }

    [Fact]
    public async Task ValidPrimaryWithoutPasswordDoesNotResurrectBackupProtector()
    {
        var data = Image(); U16(data, 4096 + 112 + 8 + 26, 0x100);
        var result = await Extract(data); Assert.False(result.Success); Assert.Empty(result.Hashes);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public async Task MalformedMetadataIsRejected(int mutation)
    {
        var data = Image();
        for (var offset = 4096; offset <= 12288; offset += 4096)
        {
            switch (mutation)
            {
                case 0: U16(data, offset + 112, 0); break;
                case 1: U16(data, offset + 112, ushort.MaxValue); break;
                case 2: U32(data, offset + 64, uint.MaxValue); break;
                case 3: U32(data, offset + 72, 0); break;
                case 4: U16(data, offset + 10, 99); break;
                case 5: U16(data, offset + 112 + 36, 7); break;
                case 6: U32(data, offset + 76, 0); break;
                case 7: U16(data, offset + 8, 1); break;
            }
        }
        Assert.False((await Extract(data)).Success);
    }

    [Fact]
    public async Task MissingKeysAndUnsupportedCiphertextAreRejected()
    {
        var missing = Protector(); U16(missing, 36 + 4, 2);
        Assert.False((await Extract(Image(entries: missing))).Success);
        Assert.False((await Extract(Image(entries: Protector(encrypted: new byte[80])))).Success);
        var duplicate = Protector(); var body = duplicate[8..];
        Assert.False((await Extract(Image(entries: Entry(2, 8, [.. body, .. Entry(0, 5, Encrypted)])))).Success);
    }

    [Fact]
    public async Task InvalidPartitionAndOutOfRangeOffsetsAreRejected()
    {
        Assert.False((await Extract(new byte[512])).Success);
        Assert.False((await Extract(Image()[..510])).Success);
        var data = Image(); Array.Clear(data, 0xa0, 16); Assert.False((await Extract(data)).Success);
        data = Image(); for (var index = 0; index < 3; index++) U64(data, 0xb0 + index * 8, ulong.MaxValue - 511);
        Assert.False((await Extract(data)).Success);
        Assert.False((await Extract(Image()[..4200])).Success);
    }

    [Fact]
    public async Task CancellationAndMetadataMutationNeverEscapeAsParserCrashes()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new BitLockerHashExtractor().ExtractAsync("unused", cancellation.Token));
        var random = new Random(22100);
        for (var i = 0; i < 100; i++)
        {
            var data = Image(); var position = random.Next(112, 350); var value = (byte)random.Next(256);
            foreach (var offset in new[] { 4096, 8192, 12288 }) data[offset + position] = value;
            await Extract(data);
        }
    }
    public void Dispose() => Directory.Delete(root, true);
}
