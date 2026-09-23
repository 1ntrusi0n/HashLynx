using System.Runtime.CompilerServices;
[assembly: InternalsVisibleTo("HashLynx.Extractors.Tests")]

namespace HashLynx.Drives;

internal static class DriveReadBounds
{
    public static (long Offset, int Skip, int Length) Plan(long position, int count, long length, int sector, long alreadyRead)
    {
        if (sector is not (512 or 1024 or 2048 or 4096) || length <= 0 || length % sector != 0 ||
            position < 0 || position > length || count < 0 || count > length - position || count > 1024 * 1024 || alreadyRead < 0)
            throw new InvalidDataException("The drive metadata read exceeds its bounds.");
        if (count == 0) return (position, 0, 0);
        var aligned = position / sector * sector; var skip = (int)(position - aligned);
        var total = checked((skip + count + sector - 1) / sector * sector);
        if (alreadyRead > 16 * 1024 * 1024 - total || total > length - aligned)
            throw new InvalidDataException("The drive metadata read exceeds its bounds.");
        return (aligned, skip, total);
    }
}
