using HashLynx.Drives;
using System.Buffers.Binary;

namespace HashLynx.Extractors.Tests;

public sealed class DriveReaderTests
{
    [Theory]
    [InlineData(@"\\.\PhysicalDrive0")] [InlineData(@"C:\")] [InlineData(@"\\server\share")]
    [InlineData(@"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\file")]
    [InlineData(@"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}")] [InlineData("")]
    public void BrokerRejectsAnythingExceptCanonicalVolumeIds(string input) => Assert.False(DriveProtocol.IsVolumeId(input));
    [Fact]
    public void OnlyGuidPipeNamesAndVolumeIdsAreAccepted()
    {
        Assert.True(DriveProtocol.IsVolumeId(@"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\"));
        Assert.True(DriveProtocol.IsPipeName("HashLynx.DriveReader." + Guid.NewGuid().ToString("N")));
        Assert.False(DriveProtocol.IsPipeName("HashLynx.DriveReader."));
        Assert.False(DriveProtocol.IsPipeName("HashLynx.DriveReader." + new string('a', 32) + " extra"));
    }
    [Theory]
    [InlineData(512)] [InlineData(4096)]
    public void ReadsStayAlignedAndWithinThePartition(int sector)
    {
        var random = new Random(641);
        const int length = 16 * 1024 * 1024;
        for (var i = 0; i < 1000; i++)
        {
            var position = random.Next(length); var count = random.Next(1, Math.Min(1024 * 1024, length - position) + 1);
            var plan = DriveReadBounds.Plan(position, count, length, sector, 0);
            Assert.Equal(0, plan.Offset % sector); Assert.Equal(0, plan.Length % sector);
            Assert.Equal(position, plan.Offset + plan.Skip); Assert.True(plan.Length >= plan.Skip + count);
            Assert.True(plan.Offset + plan.Length <= length);
        }
    }
    [Fact]
    public void OversizedOutOfRangeAndOverBudgetReadsFail()
    {
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(-1, 1, 4096, 512, 0));
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(4095, 2, 4096, 512, 0));
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(long.MaxValue, 1, 4096, 512, 0));
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(0, int.MaxValue, long.MaxValue - 511, 512, 0));
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(0, 1, 4096, 512, 16 * 1024 * 1024));
        Assert.Throws<InvalidDataException>(() => DriveReadBounds.Plan(0, 1, 4096, 513, 0));
    }
    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(131073)] [InlineData(int.MaxValue)]
    public async Task MalformedProtocolLengthFailsBeforeAllocation(int size)
    {
        var bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, size);
        await Assert.ThrowsAsync<InvalidDataException>(() => DriveProtocol.ReadAsync<DriveReadRequest>(new MemoryStream(bytes), default));
    }
    [Fact]
    public async Task FramedProtocolPreservesRequestAndRejectsTruncation()
    {
        var request = new DriveReadRequest(@"\\?\Volume{aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa}\");
        using var stream = new MemoryStream(); await DriveProtocol.WriteAsync(stream, request, default);
        stream.Position = 0; Assert.Equal(request, await DriveProtocol.ReadAsync<DriveReadRequest>(stream, default));
        await Assert.ThrowsAsync<EndOfStreamException>(() => DriveProtocol.ReadAsync<DriveReadRequest>(new MemoryStream(stream.ToArray()[..^1]), default));
    }
    [Fact]
    public async Task StreamReaderAndImageReaderShareTheBitLockerParser()
    {
        using var stream = new MemoryStream(BitLockerFixture.Image(), writable: false);
        var result = await new BitLockerHashExtractor().ExtractMetadataAsync(stream);
        Assert.True(result.Success); Assert.Equal(BitLockerFixture.Hash, Assert.Single(result.Hashes));
        Assert.False(stream.CanWrite);
    }
    [Fact]
    public async Task ZeroedLegacySectorFieldRequiresAuthoritativeDeviceGeometry()
    {
        var image = BitLockerFixture.Image(); image[11] = image[12] = 0;
        using var stream = new MemoryStream(image, writable: false);
        var extractor = new BitLockerHashExtractor();
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractMetadataAsync(stream));
        Assert.Equal(BitLockerFixture.Hash, Assert.Single((await extractor.ExtractMetadataAsync(stream, deviceSectorSize: 512)).Hashes));
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractMetadataAsync(stream, deviceSectorSize: 513));
        image[12] = 2;
        await Assert.ThrowsAsync<InvalidDataException>(() => extractor.ExtractMetadataAsync(stream, deviceSectorSize: 4096));
    }
}
