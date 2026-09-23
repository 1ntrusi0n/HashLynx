using HashLynx.Extractors;
using System.Buffers.Binary;
using System.Text.Json;

namespace HashLynx.Drives;

public sealed record DriveCandidate(string VolumeId, string MountPoint, string Label, long Size, string Kind)
{
    public string DisplayName => $"{MountPoint}  {Label}  ({(Size > 0 ? $"{Size / 1_000_000_000d:0.#} GB" : "size unavailable")}, {Kind})";
    public override string ToString() => DisplayName;
}
public sealed record DriveReadRequest(string VolumeId);
public sealed record DriveReadResponse(ExtractionResult? Extraction, string? Error, long BytesRead = 0);
public interface IBitLockerDriveService
{
    Task<IReadOnlyList<DriveCandidate>> DiscoverAsync(CancellationToken ct = default);
    Task<DriveReadResponse> ExtractAsync(DriveCandidate candidate, CancellationToken ct = default);
}

/// <summary>Only canonical local volume IDs and bounded framed JSON cross the elevation boundary.</summary>
public static class DriveProtocol
{
    public const int MaxMessage = 128 * 1024;
    public static bool IsVolumeId(string? value) => value is { Length: 49 } &&
        value.StartsWith(@"\\?\Volume{", StringComparison.Ordinal) && value.EndsWith(@"}\", StringComparison.Ordinal) &&
        Guid.TryParseExact(value.Substring(11, 36), "D", out _);
    public static bool IsPipeName(string? name) => name is not null && name.StartsWith("HashLynx.DriveReader.", StringComparison.Ordinal) &&
        Guid.TryParseExact(name["HashLynx.DriveReader.".Length..], "N", out _);
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaxMessage) throw new InvalidDataException("The drive reader response exceeds its limit.");
        var prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, bytes.Length);
        await stream.WriteAsync(prefix, ct); await stream.WriteAsync(bytes, ct); await stream.FlushAsync(ct);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken ct)
    {
        var prefix = new byte[4]; await stream.ReadExactlyAsync(prefix, ct);
        var size = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (size is <= 0 or > MaxMessage) throw new InvalidDataException("The drive reader message is invalid.");
        var bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, ct);
        return JsonSerializer.Deserialize<T>(bytes) ?? throw new InvalidDataException("The drive reader message is empty.");
    }
}
