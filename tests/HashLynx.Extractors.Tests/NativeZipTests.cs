using System.Buffers.Binary;
using System.Text;
using HashLynx.Extractors;

namespace HashLynx.Extractors.Tests;

public sealed class NativeZipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-zip-" + Guid.NewGuid().ToString("N"));
    public NativeZipTests() => Directory.CreateDirectory(_root);
    public void Dispose() => Directory.Delete(_root, true);

    [Fact]
    public async Task NativeIsAvailableWithoutDiscoveringOrLaunchingTools()
    {
        var extractor = new ZipHashExtractor(runner: new RejectRunner());
        Assert.True((await extractor.GetAvailabilityAsync()).IsAvailable);
        Assert.True((await extractor.ValidateAsync()).IsAvailable);
        Assert.Null((await extractor.GetAvailabilityAsync()).ToolPath);
        Assert.True((await Extract(Archive(new Member()))).Success);
    }

    [Theory]
    [InlineData(0, false, false, 17210, "a1b2")]
    [InlineData(8, false, false, 17200, "a1b2")]
    [InlineData(8, true, false, 17200, "3456")]
    [InlineData(8, true, true, 17200, "3456")]
    public async Task ZipCryptoSerializesFullDataAndCorrectChecksum(int method, bool descriptor, bool zip64, int mode, string checksum)
    {
        var result = await Extract(Archive(new Member(Method: (ushort)method, Descriptor: descriptor, Zip64: zip64)));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal($"$pkzip$1*1*2*0*10*4*a1b2c3d4*0*0*{method}*10*{checksum}*000102030405060708090a0b0c0d0e0f*$/pkzip$", Assert.Single(result.Hashes));
    }

    [Theory]
    [InlineData(1, 1)] [InlineData(1, 2)] [InlineData(2, 1)]
    [InlineData(2, 2)] [InlineData(3, 1)] [InlineData(3, 2)]
    public async Task AesExtractsSaltVerifierCiphertextAndAuthentication(int strength, int version)
    {
        var member = new Member(Method: 99, Strength: (byte)strength, AesVersion: (ushort)version);
        var result = await Extract(Archive(member));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var saltLength = 4 + 4 * strength;
        var data = member.Data;
        Assert.Equal($"$zip2$*0*{strength}*0*{Convert.ToHexStringLower(data[..saltLength])}*{Convert.ToHexStringLower(data[saltLength..(saltLength + 2)])}*4*{Convert.ToHexStringLower(data[(saltLength + 2)..^10])}*{Convert.ToHexStringLower(data[^10..])}*$/zip2$", Assert.Single(result.Hashes));
        Assert.Equal(13600, Assert.Single(result.SuggestedHashcatModes));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AesSupportsZip64AndDescriptorsWithoutSignatureScanning(bool signature)
    {
        var result = await Extract(Archive(new Member(Method: 99, Descriptor: true, DescriptorSignature: signature, Zip64: true)));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
    }

    [Fact]
    public async Task SelectsSmallestSupportedMemberAcrossMixedEncryptionAndWarns()
    {
        var result = await Extract(Archive(new Member(Method: 99), new Member(Method: 8, PayloadLength: 100),
            new Member(Method: 8), new Member(Flags: 0x41), new Member(Flags: 0)));
        Assert.True(result.Success);
        Assert.Equal("3", result.Metadata["selectedMember"]);
        Assert.Equal("4", result.Metadata["encryptedMembers"]);
        Assert.Single(result.Hashes);
        Assert.Contains(result.Diagnostics, d => d.Contains("different passwords"));
        Assert.Contains(result.Diagnostics, d => d.Contains("1 encrypted member(s)"));
    }

    [Fact]
    public async Task ReadOnlySourceWithMisleadingExtensionIsUnchanged()
    {
        var data = Archive(new Member());
        var path = Path.Combine(_root, "name & symbols.pdf");
        await File.WriteAllBytesAsync(path, data);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var extractor = new ZipHashExtractor();
            Assert.True(await extractor.CanHandleAsync(path));
            Assert.True((await extractor.ExtractAsync(path)).Success);
            Assert.Equal(data, await File.ReadAllBytesAsync(path));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task NoEncryptionAndEmptyArchivesDoNotProduceHashes()
    {
        foreach (var data in new[] { Archive(), Archive(new Member(Flags: 0)) })
        {
            var result = await Extract(data);
            Assert.False(result.Success);
            Assert.Empty(result.Hashes);
            Assert.Contains(result.Diagnostics, d => d.Contains("no encrypted members"));
        }
    }

    [Theory]
    [InlineData(6, 1, 4)] // Unsupported ZipCrypto compression.
    [InlineData(0, 65, 4)] // PKWARE strong encryption.
    [InlineData(0, 1, 327669)] // Exceeds full-data PKZIP limit including the encryption header.
    [InlineData(99, 1, 8388608)] // Exceeds AES ciphertext limit.
    public async Task UnsupportedMembersReturnActionableExternalToolAdvice(int method, int flags, int payload)
    {
        var result = await Extract(Archive(new Member(Method: (ushort)method, Flags: (ushort)flags, PayloadLength: payload)));
        Assert.False(result.Success);
        Assert.Empty(result.Hashes);
        Assert.Contains(result.Diagnostics, d => d.Contains("external zip2john"));
    }

    [Fact]
    public async Task ArchiveCommentMayContainSignatureBytes()
    {
        var data = Archive(new Member());
        var comment = "PK\u0005\u0006 some comment"u8.ToArray();
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(data.Length - 2), (ushort)comment.Length);
        Assert.True((await Extract([.. data, .. comment])).Success);
    }

    [Theory]
    [InlineData("truncated")] [InlineData("split")] [InlineData("local-method")]
    [InlineData("local-crc")] [InlineData("bad-offset")] [InlineData("bad-extra")]
    [InlineData("descriptor")] [InlineData("zip64-offset")] [InlineData("aes-strength")]
    [InlineData("sfx")] [InlineData("directory-count")]
    public async Task MalformedArchivesNeverProducePartialHashes(string mutation)
    {
        var data = Archive(new Member(Method: mutation == "aes-strength" ? (ushort)99 : (ushort)8,
            Descriptor: mutation == "descriptor", Zip64: mutation == "zip64-offset"));
        var eocd = data.Length - 22;
        switch (mutation)
        {
            case "truncated": data = data[..^10]; break;
            case "split": data[eocd + 4] = 1; break;
            case "local-method": data[8] = 0; break;
            case "local-crc": data[14] ^= 1; break;
            case "bad-offset": BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(eocd + 16), uint.MaxValue - 1); break;
            case "bad-extra": BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), ushort.MaxValue); break;
            case "descriptor": data[30 + 1 + 16 + 4] ^= 1; break;
            case "zip64-offset": data[eocd - 12] = 255; break;
            case "aes-strength": data[30 + 1 + 8] = 0; break;
            case "sfx": data = [.. "MZprefix"u8.ToArray(), .. data]; break;
            case "directory-count": data[eocd + 8] = data[eocd + 10] = 2; break;
        }
        var result = await Extract(data);
        Assert.False(result.Success);
        Assert.Empty(result.Hashes);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var extractor = new ZipHashExtractor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync("unused", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ValidateAsync(cancellation.Token));
    }

    private async Task<ExtractionResult> Extract(byte[] contents)
    {
        var path = Path.Combine(_root, Guid.NewGuid() + ".zip");
        await File.WriteAllBytesAsync(path, contents);
        return await new ZipHashExtractor(runner: new RejectRunner()).ExtractAsync(path);
    }

    // Synthetic structural fixtures use predictable dummy ciphertext; actual cryptographic recovery
    // is tested separately against archives produced independently by installed 7-Zip.
    private sealed record Member(ushort Method = 0, ushort Flags = 1, int PayloadLength = 4,
        byte Strength = 3, ushort AesVersion = 2, bool Descriptor = false, bool DescriptorSignature = true, bool Zip64 = false)
    {
        public byte[] Data => Enumerable.Range(0, PayloadLength + (Method == 99 ? 16 + 4 * Strength : 12)).Select(i => (byte)i).ToArray();
    }

    private static byte[] Archive(params Member[] members)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.UTF8, true);
        var offsets = new List<uint>();
        byte[] ExtraFor(Member m, bool central, uint offset)
        {
            using var extra = new MemoryStream(); using var w = new BinaryWriter(extra);
            if (m.Zip64)
            {
                w.Write((ushort)1); w.Write((ushort)(central ? 24 : 16));
                w.Write((ulong)m.PayloadLength); w.Write((ulong)m.Data.Length);
                if (central) w.Write((ulong)offset);
            }
            if (m.Method == 99)
            {
                w.Write((ushort)0x9901); w.Write((ushort)7); w.Write(m.AesVersion);
                w.Write("AE"u8); w.Write(m.Strength); w.Write((ushort)8);
            }
            return extra.ToArray();
        }
        foreach (var member in members)
        {
            offsets.Add((uint)output.Position);
            var extra = ExtraFor(member, false, 0);
            writer.Write(0x04034b50u); writer.Write((ushort)(member.Zip64 ? 45 : 20));
            writer.Write((ushort)(member.Flags | (member.Descriptor ? 8 : 0))); writer.Write(member.Method);
            writer.Write((ushort)0x3456); writer.Write((ushort)0);
            writer.Write(member.Descriptor ? 0u : 0xa1b2c3d4u);
            writer.Write(member.Zip64 ? uint.MaxValue : member.Descriptor ? 0u : (uint)member.Data.Length);
            writer.Write(member.Zip64 ? uint.MaxValue : member.Descriptor ? 0u : (uint)member.PayloadLength);
            writer.Write((ushort)1); writer.Write((ushort)extra.Length); writer.Write((byte)'a'); writer.Write(extra); writer.Write(member.Data);
            if (member.Descriptor)
            {
                if (member.DescriptorSignature) writer.Write(0x08074b50u);
                writer.Write(0xa1b2c3d4u);
                if (member.Zip64) { writer.Write((ulong)member.Data.Length); writer.Write((ulong)member.PayloadLength); }
                else { writer.Write((uint)member.Data.Length); writer.Write((uint)member.PayloadLength); }
            }
        }
        var directoryOffset = (uint)output.Position;
        for (var i = 0; i < members.Length; i++)
        {
            var m = members[i]; var extra = ExtraFor(m, true, offsets[i]);
            writer.Write(0x02014b50u); writer.Write((ushort)20); writer.Write((ushort)(m.Zip64 ? 45 : 20));
            writer.Write((ushort)(m.Flags | (m.Descriptor ? 8 : 0))); writer.Write(m.Method);
            writer.Write((ushort)0x3456); writer.Write((ushort)0); writer.Write(0xa1b2c3d4u);
            writer.Write(m.Zip64 ? uint.MaxValue : (uint)m.Data.Length); writer.Write(m.Zip64 ? uint.MaxValue : (uint)m.PayloadLength);
            writer.Write((ushort)1); writer.Write((ushort)extra.Length); writer.Write((ushort)0); writer.Write((ushort)0);
            writer.Write((ushort)0); writer.Write(0u); writer.Write(m.Zip64 ? uint.MaxValue : offsets[i]); writer.Write((byte)'a'); writer.Write(extra);
        }
        var directorySize = (uint)output.Position - directoryOffset;
        var zip64 = members.Any(m => m.Zip64);
        if (zip64)
        {
            var end64 = (ulong)output.Position;
            writer.Write(0x06064b50u); writer.Write(44ul); writer.Write((ushort)45); writer.Write((ushort)45);
            writer.Write(0u); writer.Write(0u); writer.Write((ulong)members.Length); writer.Write((ulong)members.Length);
            writer.Write((ulong)directorySize); writer.Write((ulong)directoryOffset);
            writer.Write(0x07064b50u); writer.Write(0u); writer.Write(end64); writer.Write(1u);
        }
        writer.Write(0x06054b50u); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(zip64 ? ushort.MaxValue : (ushort)members.Length); writer.Write(zip64 ? ushort.MaxValue : (ushort)members.Length);
        writer.Write(zip64 ? uint.MaxValue : directorySize); writer.Write(zip64 ? uint.MaxValue : directoryOffset); writer.Write((ushort)0);
        return output.ToArray();
    }

    private sealed class RejectRunner : IExtractorProcessRunner
    {
        public Task<ExtractorProcessResult> RunAsync(ExtractorProcessRequest request, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The native extractor must never launch external tools.");
    }
}
