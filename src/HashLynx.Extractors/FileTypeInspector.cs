namespace HashLynx.Extractors;

public sealed record FileTypeInspection(string FileType, string Extension, bool SignatureMatched, string Diagnostic);

/// <summary>Reads at most 4 KiB. Extensions are hints; conflicting or missing signatures never prove support.</summary>
public static class FileTypeInspector
{
    public static async Task<FileTypeInspection> InspectAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var header = new byte[4096];
        var count = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken);
        var type = Identify(header.AsSpan(0, count));
        return new(type, extension, type != "unknown", type == "unknown"
            ? "No registered file signature was found. Disk images must start at the BitLocker partition."
            : $"{type} signature detected; encryption is checked by the extractor.");
    }

    public static string Identify(ReadOnlySpan<byte> header)
    {
        if (header.StartsWith((ReadOnlySpan<byte>)[0xd0, 0xcf, 0x11, 0xe0, 0xa1, 0xb1, 0x1a, 0xe1])) return "office";
        if (header.StartsWith((ReadOnlySpan<byte>)[0x03, 0xd9, 0xa2, 0x9a, 0x67, 0xfb, 0x4b, 0xb5])) return "keepass";
        if (header.StartsWith("PK\u0003\u0004"u8) || header.StartsWith("PK\u0005\u0006"u8) || header.StartsWith("PK\u0007\u0008"u8)) return "zip";
        if (header.StartsWith((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x00]) || header.StartsWith((ReadOnlySpan<byte>)[0x52, 0x61, 0x72, 0x21, 0x1a, 0x07, 0x01, 0x00])) return "rar";
        if (header.StartsWith((ReadOnlySpan<byte>)[0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c])) return "7z";
        if (header.Length >= 11 && header.Slice(3, 8).SequenceEqual("-FVE-FS-"u8)) return "bitlocker";
        // MSWIN4.1 alone is an ordinary FAT OEM marker. Require BitLocker To Go's volume GUID too.
        ReadOnlySpan<byte> bitLockerGuid = [0x3b, 0xd6, 0x67, 0x49, 0x29, 0x2e, 0xd8, 0x4a, 0x83, 0x99, 0xf6, 0xa3, 0x39, 0xe3, 0xd0, 0x01];
        ReadOnlySpan<byte> usedSpaceGuid = [0x3b, 0x4d, 0xa8, 0x92, 0x80, 0xdd, 0x0e, 0x4d, 0x9e, 0x4e, 0xb1, 0xe3, 0x28, 0x4e, 0xae, 0xd8];
        if (header.Length >= 0x1b8 && header.Slice(3, 8).SequenceEqual("MSWIN4.1"u8)
            && (header.Slice(0x1a8, 16).SequenceEqual(bitLockerGuid) || header.Slice(0x1a8, 16).SequenceEqual(usedSpaceGuid))) return "bitlocker";
        // PDF allows its marker within 1024 bytes. Prefer exact archive headers over an embedded PDF member.
        if (header[..Math.Min(1024, header.Length)].IndexOf("%PDF-"u8) >= 0) return "pdf";
        return "unknown";
    }
}
