using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Extractors.Tests;

public sealed class NativeDocumentTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "HashLynx-documents-" + Guid.NewGuid().ToString("N"));
    public NativeDocumentTests() => Directory.CreateDirectory(root);
    private async Task<ExtractionResult> Extract(byte[] data, bool keepass = false)
    {
        var path = Path.Combine(root, keepass ? "target.kdbx" : "target.docx"); await File.WriteAllBytesAsync(path, data);
        IHashExtractor extractor = keepass ? new KeePassHashExtractor() : new OfficeHashExtractor();
        var result = await extractor.ExtractAsync(path);
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(path)));
        return result;
    }
    [Theory]
    [InlineData("ecma376standard_password.docx", 9400)]
    [InlineData("example_password.docx", 9600)]
    [InlineData("example_password.xlsx", 9600)]
    public async Task IndependentOfficeFilesMatchReferenceOutput(string name, int mode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Office", name);
        var result = await Extract(await File.ReadAllBytesAsync(path));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal((await File.ReadAllTextAsync(path + ".expected")).Trim(), Assert.Single(result.Hashes));
    }
    [Theory]
    [InlineData("keepass4_keepass.info_2.59_argon2d_defaultsettings", 34300)]
    [InlineData("keepass4_keepass.info_2.59_argon2id", 34300)]
    [InlineData("keepass4_keepassxc_2.7.9_aeskdf_defaultsettings", 34301)]
    public async Task IndependentKeePassFilesMatchPublishedHashcatVectors(string name, int mode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "KeePass", name);
        var result = await Extract(await File.ReadAllBytesAsync(path + ".kdbx"), true);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
        Assert.Equal((await File.ReadAllTextAsync(path + ".hash")).Trim(), Assert.Single(result.Hashes));
    }
    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(false, true)] [InlineData(true, true)]
    public async Task CompoundMiniAndRegularStreamsWithBothSectorSizes(bool large, bool version4)
    {
        var result = await Extract(Compound(Agile(), large, version4));
        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.StartsWith("$office$*2010*100000*128*16*", Assert.Single(result.Hashes));
        Assert.Equal(9500, Assert.Single(result.SuggestedHashcatModes));
    }
    [Theory]
    [InlineData("spinCount=\"100000\"", "spinCount=\"123456\"")]
    [InlineData("hashAlgorithm=\"SHA1\"", "hashAlgorithm=\"SHA256\"")]
    [InlineData("ChainingModeCBC", "ChainingModeCFB")]
    [InlineData("keyBits=\"128\"", "keyBits=\"192\"")]
    [InlineData("saltValue=\"", "saltValue=\"!")]
    [InlineData("<encryption ", "<!DOCTYPE encryption [<!ENTITY x SYSTEM 'file:///not-read'>]><encryption ")]
    public async Task UnsupportedOrHostileOfficeMetadataFailsWithoutTokens(string old, string replacement)
    {
        var data = Agile(); var xml = Encoding.UTF8.GetString(data, 8, data.Length - 8).Replace(old, replacement);
        var result = await Extract(Compound([.. data[..8], .. Encoding.UTF8.GetBytes(xml)]));
        Assert.False(result.Success); Assert.Empty(result.Hashes);
    }
    [Theory]
    [InlineData(48, 0)] // directory points to FAT
    [InlineData(48, 0xfffffffe)] // missing directory
    [InlineData(44, 0xffffffff)] // excessive FAT
    [InlineData(68, 0)] // inconsistent DIFAT
    [InlineData(1024 + 76, 0)] // directory points to itself
    [InlineData(1536, 0)] // cyclic mini-FAT
    [InlineData(1024 + 128 + 116, 5000)] // invalid mini-sector
    public async Task InvalidCompoundChainsFailPromptly(int offset, uint value)
    {
        var data = Compound(Agile()); Put32(data, offset, value);
        var result = await Extract(data); Assert.False(result.Success); Assert.Empty(result.Hashes);
    }
    [Fact]
    public async Task DuplicateOfficeDirectoryNamesAndMissingPackageFail()
    {
        var data = Compound(Agile()); Encoding.Unicode.GetBytes("EncryptionInfo\0").CopyTo(data, 1024 + 256); Put16(data, 1024 + 256 + 64, 30);
        Assert.False((await Extract(data)).Success);
        data = Compound(Agile()); Put32(data, 1024 + 128 + 72, uint.MaxValue);
        Assert.False((await Extract(data)).Success);
    }
    [Theory]
    [InlineData(128)] [InlineData(256)]
    public async Task StandardAesProfilesKeepCorrectVerifierLength(int bits)
    {
        var data = new byte[12 + 32 + 72]; Put16(data, 0, 4); Put16(data, 2, 2); Put32(data, 4, 0x24); Put32(data, 8, 32);
        Put32(data, 12, 0x24); Put32(data, 20, bits == 128 ? 0x660eu : 0x6610u); Put32(data, 24, 0x8004); Put32(data, 28, (uint)bits);
        Put32(data, 44, 16); Put32(data, 80, 20); Seq(32, 32).CopyTo(data, 84);
        var result = await Extract(Compound(data)); Assert.True(result.Success);
        var hash = Assert.Single(result.Hashes); Assert.EndsWith(Convert.ToHexStringLower(Seq(32, 20)), hash);
        Put32(data, 28, 192); Assert.False((await Extract(Compound(data))).Success);
    }
    [Fact]
    public async Task KeePass3HeaderProducesPasswordVerifier()
    {
        var result = await Extract(Kdbx3(), true); Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        Assert.Equal(13400, Assert.Single(result.SuggestedHashcatModes));
        var parts = Assert.Single(result.Hashes).Split('*'); Assert.Equal("2", parts[1]); Assert.Equal("10", parts[2]);
        Assert.Equal(Convert.ToHexStringLower(Seq(1, 32)), parts[4]); Assert.Equal(Convert.ToHexStringLower(Seq(2, 32)), parts[5]);
        Assert.Contains(result.Diagnostics, d => d.Contains("key files"));
    }
    [Theory]
    [InlineData(false, false, 34301)] [InlineData(true, false, 34300)] [InlineData(true, true, 34300)]
    public async Task KeePass4ParametersAndAuthenticatedHeaderArePreserved(bool argon, bool id, int mode)
    {
        var data = Kdbx4(argon, id); var result = await Extract(data, true);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics)); Assert.Equal(mode, Assert.Single(result.SuggestedHashcatModes));
        var parts = Assert.Single(result.Hashes).Split('*');
        Assert.Equal(argon ? id ? "9e298b19" : "ef636ddf" : "c9d9f39a", parts[3]);
        Assert.Equal(Convert.ToHexStringLower(data.AsSpan(0, data.Length - 100)), parts[9]);
        data[20] ^= 1; Assert.False((await Extract(data, true)).Success);
    }
    [Fact]
    public async Task KeePassRejectsUnsupportedCiphersFactorsAndDuplicateFields()
    {
        var v3 = Kdbx3(); v3[15] ^= 1; Assert.False((await Extract(v3, true)).Success);
        var v4 = Kdbx4(); v4[12] = 12; Assert.False((await Extract(v4, true)).Success);
        v3 = Kdbx3(); v3[31] = 2; Assert.False((await Extract(v3, true)).Success);
        v4 = Kdbx4(true); Put32(v4, 13, uint.MaxValue); Assert.False((await Extract(v4, true)).Success);
    }
    [Fact]
    public async Task TruncationsNeverReturnTokensOrThrow()
    {
        foreach (var keepass in new[] { false, true })
        {
            var data = keepass ? Kdbx4() : Compound(Agile());
            for (var n = 0; n < data.Length; n += keepass ? 17 : 107)
            { var result = await Extract(data[..n], keepass); Assert.False(result.Success); Assert.Empty(result.Hashes); }
        }
    }
    [Fact]
    public async Task SignaturesOverrideMisleadingExtensionsAndCancellationPropagates()
    {
        foreach (var keepass in new[] { false, true })
        {
            var path = Path.Combine(root, "misleading.zip"); await File.WriteAllBytesAsync(path, keepass ? Kdbx3() : Compound(Agile()));
            Assert.Equal(keepass ? "keepass" : "office", Assert.Single(await ExtractorRegistry.CreateDefault().FindCandidatesAsync(path)).Id);
            IHashExtractor extractor = keepass ? new KeePassHashExtractor() : new OfficeHashExtractor();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.ExtractAsync(path, new CancellationToken(true)));
        }
    }

    internal static byte[] Seq(int start, int count) => Enumerable.Range(start, count).Select(n => (byte)n).ToArray();
    private static void Put16(byte[] data, int pos, ushort value) => BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(pos), value);
    private static void Put32(byte[] data, int pos, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(pos), value);
    internal static byte[] Agile()
    {
        string B(int n) => Convert.ToBase64String(Seq(1, n));
        var xml = $"<encryption xmlns=\"http://schemas.microsoft.com/office/2006/encryption\"><keyEncryptors><keyEncryptor uri=\"http://schemas.microsoft.com/office/2006/keyEncryptor/password\"><encryptedKey xmlns=\"http://schemas.microsoft.com/office/2006/keyEncryptor/password\" spinCount=\"100000\" saltSize=\"16\" blockSize=\"16\" keyBits=\"128\" hashSize=\"20\" cipherAlgorithm=\"AES\" cipherChaining=\"ChainingModeCBC\" hashAlgorithm=\"SHA1\" saltValue=\"{B(16)}\" encryptedVerifierHashInput=\"{B(16)}\" encryptedVerifierHashValue=\"{B(32)}\" encryptedKeyValue=\"{B(16)}\" /></keyEncryptor></keyEncryptors></encryption>";
        return [4, 0, 4, 0, 0x40, 0, 0, 0, .. Encoding.UTF8.GetBytes(xml)];
    }
    internal static byte[] Compound(byte[] metadata, bool large = false, bool version4 = false)
    {
        var sector = version4 ? 4096 : 512;
        if (large) metadata = [.. metadata, .. Enumerable.Repeat((byte)' ', Math.Max(0, 4096 - metadata.Length))];
        var dataSectors = (metadata.Length + sector - 1) / sector; var data = new byte[(4 + dataSectors) * sector];
        Convert.FromHexString("d0cf11e0a1b11ae1").CopyTo(data, 0); Put16(data, 24, 0x3e); Put16(data, 26, version4 ? (ushort)4 : (ushort)3);
        Put16(data, 28, 0xfffe); Put16(data, 30, version4 ? (ushort)12 : (ushort)9); Put16(data, 32, 6);
        Put32(data, 40, version4 ? 1u : 0u); Put32(data, 44, 1); Put32(data, 48, 1); Put32(data, 56, 4096);
        Put32(data, 60, 2); Put32(data, 64, 1); Put32(data, 68, 0xfffffffe);
        for (var i = 0; i < 109; i++) Put32(data, 76 + i * 4, i == 0 ? 0u : uint.MaxValue);
        Array.Fill(data, (byte)255, sector, sector); Put32(data, sector, 0xfffffffd); Put32(data, sector + 4, 0xfffffffe); Put32(data, sector + 8, 0xfffffffe);
        for (var i = 0; i < dataSectors; i++) Put32(data, sector + (3 + i) * 4, i + 1 == dataSectors ? 0xfffffffe : (uint)(4 + i));
        void Entry(int id, string name, byte type, uint start, uint size, uint sibling = uint.MaxValue, uint child = uint.MaxValue)
        {
            var p = 2 * sector + id * 128; Encoding.Unicode.GetBytes(name + '\0').CopyTo(data, p); Put16(data, p + 64, (ushort)((name.Length + 1) * 2));
            data[p + 66] = type; Put32(data, p + 68, uint.MaxValue); Put32(data, p + 72, sibling); Put32(data, p + 76, child); Put32(data, p + 116, start); Put32(data, p + 120, size);
        }
        Entry(0, "Root Entry", 5, 3, large ? 0 : (uint)((metadata.Length + 63) / 64 * 64), child: 1);
        Entry(1, "EncryptionInfo", 2, large ? 3u : 0u, (uint)metadata.Length, sibling: 2);
        Entry(2, "EncryptedPackage", 2, 0xfffffffe, 0);
        Array.Fill(data, (byte)255, 3 * sector, sector);
        for (var i = 0; i < (metadata.Length + 63) / 64; i++) Put32(data, 3 * sector + i * 4, i + 1 == (metadata.Length + 63) / 64 ? 0xfffffffe : (uint)(i + 1));
        metadata.CopyTo(data, 4 * sector); return data;
    }
    internal static byte[] Kdbx3()
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(0x9aa2d903u); w.Write(0xb54bfb67u); w.Write(0x00030001u);
        void Field(byte id, byte[] bytes) { w.Write(id); w.Write((ushort)bytes.Length); w.Write(bytes); }
        Field(2, Convert.FromHexString("31c1f2e6bf714350be5805216afc5aff")); Field(3, BitConverter.GetBytes(0)); Field(4, Seq(1, 32));
        Field(5, Seq(2, 32)); Field(6, BitConverter.GetBytes(10UL)); Field(7, Seq(3, 16)); Field(8, Seq(4, 32)); Field(9, Seq(5, 32)); Field(10, BitConverter.GetBytes(2)); Field(0, "\r\n\r\n"u8.ToArray());
        using var aes = Aes.Create(); aes.Key = Seq(2, 32);
        var key = SHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes("HashLynxFixture!")));
        for (var i = 0; i < 10; i++) key = aes.EncryptEcb(key, PaddingMode.None);
        aes.Key = SHA256.HashData([.. Seq(1, 32), .. SHA256.HashData(key)]);
        w.Write(aes.EncryptCbc(Seq(5, 32), Seq(3, 16), PaddingMode.PKCS7)); return output.ToArray();
    }
    internal static byte[] Kdbx4(bool argon = false, bool id = false)
    {
        using var output = new MemoryStream(); using var w = new BinaryWriter(output); w.Write(0x9aa2d903u); w.Write(0xb54bfb67u); w.Write(0x00040000u);
        void Field(byte field, byte[] bytes) { w.Write(field); w.Write(bytes.Length); w.Write(bytes); }
        Field(2, Convert.FromHexString("31c1f2e6bf714350be5805216afc5aff")); Field(3, BitConverter.GetBytes(0)); Field(4, Seq(1, 32));
        using var dict = new MemoryStream(); using var d = new BinaryWriter(dict); d.Write((ushort)0x100);
        void Parameter(byte type, string name, byte[] bytes) { d.Write(type); d.Write(name.Length); d.Write(Encoding.ASCII.GetBytes(name)); d.Write(bytes.Length); d.Write(bytes); }
        Parameter(0x42, "$UUID", Convert.FromHexString(argon ? id ? "9e298b1956db4773b23dfc3ec6f0a1e6" : "ef636ddf8c29444b91f7a9a403e30a0c" : "c9d9f39a628a4460bf740d08c18a4fea"));
        if (argon) { Parameter(4, "V", BitConverter.GetBytes(19)); Parameter(5, "I", BitConverter.GetBytes(2UL)); Parameter(5, "M", BitConverter.GetBytes(64UL * 1024 * 1024)); Parameter(4, "P", BitConverter.GetBytes(2)); }
        else Parameter(5, "R", BitConverter.GetBytes(10UL));
        Parameter(0x42, "S", Seq(2, 32)); d.Write((byte)0); Field(11, dict.ToArray()); Field(7, Seq(3, 16)); Field(0, "\r\n\r\n"u8.ToArray());
        var header = output.ToArray(); w.Write(SHA256.HashData(header));
        using var aes = Aes.Create(); aes.Key = Seq(2, 32); var key = SHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes("HashLynxFixture!")));
        for (var i = 0; i < 10; i++) key = aes.EncryptEcb(key, PaddingMode.None);
        var macKey = SHA512.HashData([.. BitConverter.GetBytes(ulong.MaxValue), .. SHA512.HashData([.. Seq(1, 32), .. SHA256.HashData(key), 1])]);
        w.Write(HMACSHA256.HashData(macKey, header)); w.Write(new byte[36]); return output.ToArray();
    }
    public void Dispose() => Directory.Delete(root, true);
}
