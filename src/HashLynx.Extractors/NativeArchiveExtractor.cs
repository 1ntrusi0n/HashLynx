using System.Buffers.Binary;

namespace HashLynx.Extractors;

/// <summary>Shared native archive lifecycle; configured external tools are explicit overrides.</summary>
public abstract class NativeArchiveExtractor : IHashExtractor
{
    private readonly IHashExtractor? _external;
    protected NativeArchiveExtractor(IHashExtractor? external) => _external = external;
    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public abstract IReadOnlyList<string> SupportedExtensions { get; }
    public string ImplementationType => _external?.ImplementationType ?? $"Built-in {DisplayName} extractor";
    public bool IsBuiltIn => _external is null;
    protected virtual string UnsupportedGuidance => "For unsupported variants, use a compatible external extractor separately, then import its Hashcat-compatible output as a hash file.";
    public Task<ExtractorAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _external?.GetAvailabilityAsync(cancellationToken) ?? Task.FromResult(new ExtractorAvailability
        { IsAvailable = true, Version = "Built-in v1", Diagnostic = "Ready without additional tools or configuration." });
    }
    public Task<ExtractorAvailability> ValidateAsync(CancellationToken cancellationToken = default) =>
        _external?.ValidateAsync(cancellationToken) ?? GetAvailabilityAsync(cancellationToken);
    public async Task<bool> CanHandleAsync(string path, CancellationToken cancellationToken = default)
    {
        if (_external is not null) return await _external.CanHandleAsync(path, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        try { return (await FileTypeInspector.InspectAsync(path, cancellationToken).ConfigureAwait(false)).FileType == Id; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    public Task<ExtractionResult> ExtractAsync(string path, CancellationToken cancellationToken = default) =>
        _external?.ExtractAsync(path, cancellationToken) ?? Task.Run(async () =>
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
                return await ExtractNativeAsync(stream, cancellationToken).ConfigureAwait(false);
            }
            catch (InvalidDataException ex) { return Failure(ex.Message); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
            { return Failure("The file could not be read or contains invalid offsets. Check that it is complete and accessible."); }
        }, cancellationToken);
    protected abstract Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken cancellationToken);
    protected ExtractionResult Success(string hash, int mode, string notice) => new()
    {
        Success = true, SourceFileType = Id, ExtractorName = $"Built-in {DisplayName}", Hashes = [hash], SuggestedHashcatModes = [mode],
        Diagnostics = [notice], Metadata = new Dictionary<string, string> { ["implementation"] = $"Built-in {DisplayName} v1" }
    };
    private ExtractionResult Failure(string message) => new()
    {
        SourceFileType = Id, ExtractorName = $"Built-in {DisplayName}",
        Diagnostics = [message, UnsupportedGuidance]
    };
}

internal static class ArchiveData
{
    public static void Require(bool condition, string message = "The archive metadata is malformed or truncated.")
    { if (!condition) throw new InvalidDataException(message); }
    public static ushort U16(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt16LittleEndian(value);
    public static uint U32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32LittleEndian(value);
    public static ulong U64(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64LittleEndian(value);
    public static string Hex(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(value);
    public static async Task<byte[]> ReadAsync(Stream stream, ulong offset, int size, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Require(size is >= 0 and <= 16 * 1024 * 1024 && offset <= (ulong)stream.Length && (ulong)size <= (ulong)stream.Length - offset);
        stream.Position = (long)offset;
        var data = new byte[size];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data;
    }
    private static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(value =>
    {
        var crc = (uint)value;
        for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0);
        return crc;
    }).ToArray();
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 255] ^ (crc >> 8);
        return ~crc;
    }
    // CRC concatenation is linear over GF(2). Precompute the effect of appending
    // 2^n zero bytes, then apply those operators to an already finalized CRC.
    private static readonly uint[][] ZeroByteShifts = BuildZeroByteShifts();
    private static uint Apply(uint[] matrix, uint value)
    {
        uint result = 0;
        for (var bit = 0; value != 0; bit++, value >>= 1) if ((value & 1) != 0) result ^= matrix[bit];
        return result;
    }
    private static uint[][] BuildZeroByteShifts()
    {
        var shifts = new uint[64][];
        shifts[0] = new uint[32];
        for (var bit = 0; bit < 32; bit++)
        {
            var value = 1u << bit;
            for (var step = 0; step < 8; step++) value = (value >> 1) ^ ((value & 1) == 0 ? 0 : 0xedb88320u);
            shifts[0][bit] = value;
        }
        for (var power = 1; power < shifts.Length; power++)
        {
            shifts[power] = new uint[32];
            for (var bit = 0; bit < 32; bit++) shifts[power][bit] = Apply(shifts[power - 1], shifts[power - 1][bit]);
        }
        return shifts;
    }
    public static uint CombineCrc32(uint left, uint right, ulong rightLength)
    {
        if (rightLength == 0) return left;
        for (var power = 0; rightLength != 0; power++, rightLength >>= 1)
            if ((rightLength & 1) != 0) left = Apply(ZeroByteShifts[power], left);
        return left ^ right;
    }
}

internal sealed class ArchiveCursor(byte[] data)
{
    public int Position { get; private set; }
    public int Remaining => data.Length - Position;
    public byte Byte() { ArchiveData.Require(Remaining > 0); return data[Position++]; }
    public byte[] Take(int count)
    {
        ArchiveData.Require(count >= 0 && count <= Remaining);
        var result = data.AsSpan(Position, count).ToArray(); Position += count; return result;
    }
    public void Skip(int count) { ArchiveData.Require(count >= 0 && count <= Remaining); Position += count; }
    public uint UInt32() => ArchiveData.U32(Take(4));
    public ulong RarInteger()
    {
        ulong result = 0;
        for (var index = 0; index < 10; index++)
        {
            var value = Byte();
            ArchiveData.Require(index < 9 || value <= 1, "A RAR variable integer overflows its supported range.");
            result |= (ulong)(value & 127) << (index * 7);
            if ((value & 128) == 0) return result;
        }
        throw new InvalidDataException("The RAR variable integer is too long.");
    }
    public ulong SevenZipInteger()
    {
        var first = Byte(); ulong result = 0; var mask = 0x80;
        for (var index = 0; index < 8; index++, mask >>= 1)
        {
            if ((first & mask) == 0) return result | ((ulong)(first & (mask - 1)) << (index * 8));
            result |= (ulong)Byte() << (index * 8);
        }
        return result;
    }
    public int SevenZipCount(int max = 100_000)
    {
        var value = SevenZipInteger(); ArchiveData.Require(value <= (ulong)max, "The 7-Zip metadata exceeds the built-in limits."); return (int)value;
    }
}
