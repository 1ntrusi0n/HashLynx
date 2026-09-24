using Microsoft.Win32.SafeHandles;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace HashLynx.Drives;

/// <summary>Sector-aligned reads confined to one selected partition. No device writes or control mutations.</summary>
internal sealed class ReadOnlyPartitionStream : Stream
{
    private readonly SafeFileHandle _disk;
    private readonly WindowsVolumes.Extent _extent;
    private readonly int _sector;
    private readonly CancellationToken _ct;
    private long _position;
    public long BytesRead { get; private set; }
    public int SectorSize => _sector;
    public ReadOnlyPartitionStream(WindowsVolumes.Extent extent, CancellationToken ct)
    {
        _extent = extent; _ct = ct;
        // Physical access preserves encrypted on-disk metadata even if Windows has unlocked the volume.
        _disk = WindowsVolumes.Open(@"\\.\PhysicalDrive" + extent.Disk, 0x80000000, 0x20000000); // GENERIC_READ, NO_BUFFERING
        try
        {
            var geometry = WindowsVolumes.Query(_disk, 0x70000, 24, 32);
            _sector = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(geometry.AsSpan(20)));
            var length = BinaryPrimitives.ReadInt64LittleEndian(WindowsVolumes.Query(_disk, 0x7405c, 8, 8));
            if (_sector is not (512 or 1024 or 2048 or 4096) || extent.Offset % _sector != 0 || extent.Length % _sector != 0 || extent.Offset > length || extent.Length > length - extent.Offset)
                throw new InvalidDataException("The disk sector size or partition boundaries are unsupported.");
        }
        catch { _disk.Dispose(); throw; }
    }
    public override int Read(byte[] buffer, int offset, int count)
    {
        _ct.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegative(offset); ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (offset > buffer.Length - count) throw new ArgumentException("Invalid read buffer.");
        count = (int)Math.Min(count, Length - Position); if (count == 0) return 0;
        var (aligned, skip, total) = DriveReadBounds.Plan(Position, count, Length, _sector, BytesRead);
        // Align to 64 KiB, covering the supported Windows logical/physical sector sizes.
        var allocation = Marshal.AllocHGlobal(total + 65535);
        try
        {
            var alignedBuffer = new IntPtr((allocation.ToInt64() + 65535) & ~65535L);
            if (!SetFilePointerEx(_disk, checked(_extent.Offset + aligned), out var actual, 0) || actual != _extent.Offset + aligned)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!ReadFile(_disk, alignedBuffer, total, out var read, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (read != total) throw new EndOfStreamException("The device was removed or returned an incomplete metadata read.");
            BytesRead += read; _ct.ThrowIfCancellationRequested();
            Marshal.Copy(alignedBuffer + skip, buffer, offset, count); _position += count; return count;
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var data = new byte[buffer.Length]; var count = Read(data, 0, data.Length); data.AsMemory(0, count).CopyTo(buffer);
        return ValueTask.FromResult(count);
    }
    public override long Length => _extent.Length;
    public override long Position { get => _position; set { if (value < 0 || value > Length) throw new IOException("Read outside the selected partition."); _position = value; } }
    public override long Seek(long offset, SeekOrigin origin) { Position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => Position, SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset); return Position; }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { if (disposing) _disk.Dispose(); base.Dispose(disposing); }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFilePointerEx(SafeFileHandle handle, long distance, out long position, uint method);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle handle, IntPtr buffer, int length, out int read, IntPtr overlapped);
}
