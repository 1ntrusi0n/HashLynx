using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

/// <summary>Reads password protectors from raw Windows 7+ BitLocker partition metadata.</summary>
public sealed class BitLockerHashExtractor() : NativeArchiveExtractor(null)
{
    public override string Id => "bitlocker";
    public override string DisplayName => "BitLocker";
    public override string Description => "Built-in password-protector extraction from raw BitLocker partition images.";
    public override IReadOnlyList<string> SupportedExtensions => [".img", ".dd", ".bin", ".raw"];
    protected override string UnsupportedGuidance => "Use a raw partition image starting at its boot sector. TPM, PIN, startup-key and recovery-password protectors are not supported.";

    // Field layout: Hashcat bitlocker2hashcat.py and the libbde format specification.
    // Serialization: Hashcat mode 22100, type 1 (full AES-CCM authentication).
    // See THIRD_PARTY_NOTICES.md. No disk mounting or decryption is performed here.
    protected override Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken ct) => ExtractMetadataAsync(stream, ct);

    /// <summary>Reads a seekable, partition-relative source supplied by the read-only drive broker.</summary>
    public async Task<ExtractionResult> ExtractMetadataAsync(Stream stream, CancellationToken ct = default, int? deviceSectorSize = null)
    {
        var boot = await ReadAsync(stream, 0, 512, ct).ConfigureAwait(false);
        Require(FileTypeInspector.Identify(boot) == Id && U16(boot.AsSpan(510)) == 0xaa55,
            "The file is not a supported raw BitLocker partition image. Whole disks and virtual-disk containers must first be exported as a raw partition.");
        var sector = U16(boot.AsSpan(11));
        // Some Windows-created removable volumes zero the legacy BPB field. For live devices,
        // use the logical sector size obtained from Windows, never an inferred image-file default.
        if (deviceSectorSize is not null)
        {
            Require(deviceSectorSize is 512 or 1024 or 2048 or 4096, "Windows reported an unsupported device sector size.");
            Require(sector == 0 || sector == deviceSectorSize, "The BitLocker header and device sector sizes disagree.");
            if (sector == 0) sector = (ushort)deviceSectorSize.Value;
        }
        Require(sector is 512 or 1024 or 2048 or 4096, "The BitLocker sector size is unsupported.");
        var guidOffset = boot.AsSpan(3, 8).SequenceEqual("MSWIN4.1"u8) ? 0x1a8 : 0xa0;
        var guid = new Guid(boot.AsSpan(guidOffset, 16));
        Require(guid == new Guid("4967d63b-2e29-4ad8-8399-f6a339e3d001") || guid == new Guid("92a84d3b-dd80-4d0e-9e4e-b1e3284eaed8"),
            "This BitLocker volume layout is unsupported. Windows Vista metadata is not supported.");
        var tried = new HashSet<ulong>();
        for (var copy = 0; copy < 3; copy++)
        {
            ct.ThrowIfCancellationRequested();
            var offset = U64(boot.AsSpan(guidOffset + 16 + copy * 8));
            if (!tried.Add(offset) || offset < (ulong)sector || offset % (ulong)sector != 0) continue;
            List<string> hashes;
            int skipped;
            try
            {
                var header = await ReadAsync(stream, offset, 112, ct).ConfigureAwait(false);
                Require(header.AsSpan(0, 8).SequenceEqual("-FVE-FS-"u8) && U16(header.AsSpan(10)) == 2);
                var size = U32(header.AsSpan(64));
                Require(size is >= 48 and <= 1024 * 1024 && U32(header.AsSpan(68)) == 1 && U32(header.AsSpan(72)) == 48);
                Require(U32(header.AsSpan(76)) == size, "BitLocker metadata size fields disagree.");
                var blockSize = U16(header.AsSpan(8)) * 16;
                Require((ulong)blockSize >= size + 64ul, "BitLocker metadata extends past its block boundary.");
                var data = await ReadAsync(stream, offset + 112, (int)size - 48, ct).ConfigureAwait(false);
                (hashes, skipped) = ReadProtectors(data, ct);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException) { continue; }
            // Use one coherent metadata snapshot. Do not mix older backup protectors into it.
            Require(hashes.Count > 0, "No supported user-password protector was found in the selected BitLocker metadata. TPM/PIN, startup-key and recovery-password protectors cannot be used with this extractor.");
            return new ExtractionResult
            {
                Success = true, SourceFileType = Id, ExtractorName = "Built-in BitLocker", Hashes = hashes,
                SuggestedHashcatModes = [22100], Metadata = new Dictionary<string, string> { ["implementation"] = "Built-in BitLocker v1" },
                Diagnostics = [$"Built-in BitLocker read {hashes.Count} distinct user-password protector(s) from metadata copy #{copy + 1}. Other protector types or unsupported key formats skipped: {skipped}. Recovery checks full key authentication; it does not decrypt the partition." +
                    (copy > 0 ? " Earlier metadata copies were unavailable or invalid; this backup may describe an older protector configuration." : "")]
            };
        }
        throw new InvalidDataException("No complete, supported BitLocker metadata copy could be read. The image may be truncated, damaged or use an unsupported metadata version.");
    }

    private static (List<string> Hashes, int Skipped) ReadProtectors(byte[] data, CancellationToken ct)
    {
        var hashes = new HashSet<string>(StringComparer.Ordinal); var skipped = 0;
        foreach (var entry in Entries(data, true, ct))
        {
            if (entry.Kind != 2 || entry.Value != 8) continue;
            Require(entry.Data.Length >= 28, "A BitLocker VMK record is truncated.");
            if (entry.Version != 1 || U16(entry.Data.AsSpan(26)) != 0x2000) { skipped++; continue; }
            byte[]? salt = null, encrypted = null; var supported = true;
            foreach (var property in Entries(entry.Data[28..], false, ct))
            {
                if (property.Kind != 0) continue;
                if (property.Value == 3)
                {
                    Require(salt is null && property.Data.Length >= 20, "A BitLocker stretch-key record is duplicated or truncated.");
                    salt = property.Data[4..20];
                    supported &= property.Version == 1 && U32(property.Data) is 0x1000 or 0x1001;
                    // A nested stretch-key value is separate from the outer encrypted VMK.
                    foreach (var _ in Entries(property.Data[20..], false, ct)) { }
                }
                else if (property.Value == 5)
                {
                    Require(encrypted is null && property.Data.Length >= 28, "A BitLocker encrypted-key record is duplicated or truncated.");
                    encrypted = property.Data;
                    supported &= property.Version == 1 && encrypted.Length == 72;
                }
            }
            Require(salt is not null && encrypted is not null, "A BitLocker password protector is missing its stretch key or encrypted VMK.");
            if (!supported) { skipped++; continue; }
            hashes.Add($"$bitlocker$1$16${Hex(salt!)}$1048576$12${Hex(encrypted!.AsSpan(0, 12))}$60${Hex(encrypted.AsSpan(12))}");
            Require(hashes.Count <= 128, "BitLocker metadata contains too many password protectors.");
        }
        return (hashes.ToList(), skipped);
    }

    private sealed record Entry(ushort Kind, ushort Value, ushort Version, byte[] Data);
    private static IEnumerable<Entry> Entries(byte[] data, bool padding, CancellationToken ct)
    {
        var position = 0; var count = 0;
        while (position < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            if (padding && (data.Length - position < 8 || U64(data.AsSpan(position)) == 0))
            {
                Require(data.AsSpan(position).IndexOfAnyExcept((byte)0) < 0, "BitLocker metadata padding contains an incomplete entry.");
                yield break;
            }
            Require(++count <= 8192 && data.Length - position >= 8, "The BitLocker entry table is truncated or exceeds the built-in limit.");
            var size = U16(data.AsSpan(position));
            Require(size >= 8 && size <= data.Length - position, "A BitLocker metadata entry has an invalid size.");
            yield return new(U16(data.AsSpan(position + 2)), U16(data.AsSpan(position + 4)), U16(data.AsSpan(position + 6)), data.AsSpan(position + 8, size - 8).ToArray());
            position += size;
        }
    }
}
