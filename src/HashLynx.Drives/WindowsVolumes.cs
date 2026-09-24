using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace HashLynx.Drives;

public static class WindowsVolumes
{
    public static IReadOnlyList<DriveCandidate> Discover(CancellationToken ct)
    {
        var result = new List<DriveCandidate>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            ct.ThrowIfCancellationRequested();
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            var id = new StringBuilder(64);
            if (!GetVolumeNameForVolumeMountPointW(drive.Name, id, (uint)id.Capacity) || !DriveProtocol.IsVolumeId(id.ToString())) continue;
            string label = "Locked or unreadable volume"; long size = 0;
            try { if (drive.IsReady) { label = drive.VolumeLabel; size = drive.TotalSize; } } catch (IOException) { } catch (UnauthorizedAccessException) { }
            result.Add(new(id.ToString(), drive.Name, label, size, drive.DriveType.ToString()));
        }
        return result;
    }
    internal sealed record Extent(uint Disk, long Offset, long Length);
    internal static SafeFileHandle OpenVolume(string id)
    {
        if (!DriveProtocol.IsVolumeId(id)) throw new InvalidDataException("Select a valid local volume.");
        return Open(id.TrimEnd('\\'), 0, 0);
    }
    internal static SafeFileHandle Open(string path, uint access, uint flags)
    {
        var handle = CreateFileW(path, access, 3, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error); }
        return handle;
    }
    internal static byte[] Query(SafeFileHandle handle, uint code, int minimum, int capacity)
    {
        var data = new byte[capacity];
        if (!DeviceIoControl(handle, code, IntPtr.Zero, 0, data, data.Length, out var returned, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (returned < minimum || returned > data.Length) throw new InvalidDataException("Windows returned incomplete device information.");
        return data;
    }
    internal static Extent GetExtent(SafeFileHandle volume)
    {
        var bytes = Query(volume, 0x00560000, 32, 4096); // IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS
        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 1) throw new InvalidDataException("The drive reader supports volumes on one contiguous physical disk only.");
        var value = new Extent(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24)));
        if (value.Offset < 0 || value.Length <= 0 || value.Offset > long.MaxValue - value.Length) throw new InvalidDataException("The volume boundaries are invalid.");
        return value;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder name, uint length);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
