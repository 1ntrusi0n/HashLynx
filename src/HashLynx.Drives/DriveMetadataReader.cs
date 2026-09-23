using HashLynx.Extractors;
using System.ComponentModel;

namespace HashLynx.Drives;

public static class DriveMetadataReader
{
    public static async Task<DriveReadResponse> ReadAsync(DriveReadRequest request, CancellationToken ct)
    {
        try
        {
            using var volume = WindowsVolumes.OpenVolume(request.VolumeId);
            var extent = WindowsVolumes.GetExtent(volume);
            using var stream = new ReadOnlyPartitionStream(extent, ct);
            var extractor = new BitLockerHashExtractor();
            var first = await extractor.ExtractMetadataAsync(stream, ct, stream.SectorSize);
            var second = await extractor.ExtractMetadataAsync(stream, ct, stream.SectorSize);
            if (extent != WindowsVolumes.GetExtent(volume) || !first.Hashes.SequenceEqual(second.Hashes) || !first.Diagnostics.SequenceEqual(second.Diagnostics))
                return new(null, "The device metadata changed while it was being read. Wait for encryption or protector changes to finish, then retry.", stream.BytesRead);
            return new(first, null, stream.BytesRead);
        }
        catch (InvalidDataException ex) { return new(null, ex.Message); }
        catch (Win32Exception ex)
        {
            return new(null, ex.NativeErrorCode == 5 ? "Windows denied read access. Approve the administrator prompt and check device access." :
                $"Windows could not read the selected device (error {ex.NativeErrorCode}). Refresh the drive list and check that it is connected.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or OverflowException or NotSupportedException)
        { return new(null, "The selected device is unavailable or has unsupported metadata. Refresh the drive list and retry."); }
    }
}
