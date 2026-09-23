using System.Buffers.Binary;
using System.Globalization;

namespace HashLynx.Extractors;

/// <summary>Read-only ZIP metadata extraction. An explicit tool path opts into the external adapter.</summary>
public sealed class ZipHashExtractor : IHashExtractor
{
    private readonly ExternalZipHashExtractor? _external;
    public ZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    {
        if (!string.IsNullOrWhiteSpace(configuration?.ToolPath)) _external = new(configuration, runner);
    }

    public string Id => "zip";
    public string DisplayName => "ZIP";
    public string Description => "Built-in ZipCrypto and WinZip AES extraction.";
    public string ImplementationType => _external?.ImplementationType ?? "Built-in ZIP extractor";
    public bool IsBuiltIn => _external is null;
    public IReadOnlyList<string> SupportedExtensions => [".zip", ".zipx"];
    public Task<ExtractorAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _external?.GetAvailabilityAsync(cancellationToken) ?? Task.FromResult(new ExtractorAvailability
        {
            IsAvailable = true, Version = "Built-in v1",
            Diagnostic = "Ready without additional tools. Supports ZipCrypto (stored/deflate), WinZip AES, ZIP64 and data descriptors."
        });
    }
    public Task<ExtractorAvailability> ValidateAsync(CancellationToken cancellationToken = default) =>
        _external?.ValidateAsync(cancellationToken) ?? GetAvailabilityAsync(cancellationToken);
    public async Task<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (_external is not null) return await _external.CanHandleAsync(filePath, cancellationToken).ConfigureAwait(false);
        try { return (await FileTypeInspector.InspectAsync(filePath, cancellationToken).ConfigureAwait(false)).FileType == Id; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }
    public Task<ExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default) =>
        _external?.ExtractAsync(filePath, cancellationToken) ?? Task.Run(() => ExtractNativeAsync(filePath, cancellationToken), cancellationToken);

    private static async Task<ExtractionResult> ExtractNativeAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.RandomAccess);
            var reader = new ZipReader(stream, ct);
            return await reader.ExtractAsync().ConfigureAwait(false);
        }
        catch (InvalidDataException ex) { return Failure(ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or OverflowException)
        {
            return Failure("The ZIP could not be read or contains invalid offsets. Check that it is complete, accessible and not in use by another application.");
        }
    }

    private static ExtractionResult Failure(string message) => new()
    {
        ExtractorName = "Built-in ZIP", SourceFileType = "zip",
        Diagnostics = [message, "For unsupported ZIP variants, use a compatible external zip2john extractor separately, then import its Hashcat-compatible output as a hash file."]
    };

    // ZIP layout: PKWARE APPNOTE; AES layout: WinZip AE-1/AE-2 specification.
    // Hash serialization follows zip2john's documented $pkzip$ / $zip2$ formats.
    // See THIRD_PARTY_NOTICES.md for the permissively licensed format reference.
    private sealed class ZipReader(FileStream stream, CancellationToken ct)
    {
        private const int MaxEntries = 100_000;
        private const int MaxDirectoryBytes = 64 * 1024 * 1024;
        private const int MaxPkzipBytes = 320 * 1024; // Hashcat's full-data PKZIP modules.
        private const int MaxAesCiphertextBytes = 8 * 1024 * 1024 - 1; // Hashcat mode 13600.
        private long _directoryOffset;

        private sealed record Entry(int Index, ushort Version, ushort Flags, ushort Method, ushort Time,
            uint Crc, ulong Compressed, ulong Uncompressed, ulong Offset, byte[] Name, byte[]? Aes);

        public async Task<ExtractionResult> ExtractAsync()
        {
            var signature = await ReadAsync(0, 4).ConfigureAwait(false);
            Require(U32(signature, 0) is 0x04034b50 or 0x06054b50, "Split and self-extracting ZIP archives are not supported by the built-in extractor.");
            var (offset, size, count) = await ReadDirectoryLocationAsync().ConfigureAwait(false);
            _directoryOffset = offset;
            var end = checked(offset + size);
            var position = offset;
            Entry? selected = null;
            var encrypted = 0;
            var unsupported = 0;
            for (var index = 0; index < count; index++)
            {
                ct.ThrowIfCancellationRequested();
                Require(position <= end - 46, "The ZIP central directory is truncated.");
                var header = await ReadAsync(position, 46).ConfigureAwait(false);
                Require(U32(header, 0) == 0x02014b50, "The ZIP central directory is malformed or encrypted.");
                var nameLength = U16(header, 28);
                var extraLength = U16(header, 30);
                var next = checked(position + 46 + nameLength + extraLength + U16(header, 32));
                Require(next <= end, "A ZIP directory entry extends beyond the central directory.");
                var name = await ReadAsync(position + 46, nameLength).ConfigureAwait(false);
                var extra = await ReadAsync(position + 46 + nameLength, extraLength).ConfigureAwait(false);
                ulong compressed = U32(header, 20), uncompressed = U32(header, 24), localOffset = U32(header, 42);
                uint disk = U16(header, 34);
                ReadZip64(extra, ref uncompressed, ref compressed, ref localOffset, ref disk);
                Require(disk == 0, "Split ZIP archives are not supported by the built-in extractor.");
                Require(localOffset < (ulong)offset && compressed <= (ulong)offset - localOffset,
                    "A ZIP member points outside the file data area.");
                var aes = Extra(extra, 0x9901);
                var entry = new Entry(index + 1, U16(header, 6), U16(header, 8), U16(header, 10), U16(header, 12),
                    U32(header, 16), compressed, uncompressed, localOffset, name, aes);
                if ((entry.Flags & 1) != 0)
                {
                    encrypted++;
                    if (Supported(entry))
                    {
                        if (selected is null || entry.Compressed < selected.Compressed) selected = entry;
                    }
                    else unsupported++;
                }
                position = next;
            }
            Require(position == end, "The ZIP directory has unexpected trailing records. Use an external extractor for this variant.");
            Require(encrypted > 0, "This ZIP has no encrypted members. No password recovery is needed.");
            Require(selected is not null, "No supported encrypted member fits the built-in limits: ZipCrypto stored/deflate up to 320 KiB, or WinZip AES ciphertext below 8 MiB. Strong encryption and other ZipCrypto compression methods need an external extractor.");
            var chosen = selected!;
            var data = await ReadMemberAsync(chosen).ConfigureAwait(false);
            var hash = Format(chosen, data);
            var mode = chosen.Method == 99 ? 13600 : chosen.Method == 8 ? 17200 : 17210;
            var diagnostics = new List<string>
            {
                $"Built-in ZIP selected member #{chosen.Index} of {count} ({encrypted} encrypted). Recovery verifies this member; other members can have different passwords."
            };
            if (unsupported > 0) diagnostics.Add($"{unsupported} encrypted member(s) use unsupported variants or exceed the built-in size limits.");
            return new()
            {
                Success = true, SourceFileType = "zip", ExtractorName = "Built-in ZIP", Hashes = [hash], SuggestedHashcatModes = [mode],
                Diagnostics = diagnostics,
                Metadata = new Dictionary<string, string>
                {
                    ["implementation"] = "Built-in ZIP v1", ["selectedMember"] = chosen.Index.ToString(CultureInfo.InvariantCulture),
                    ["encryptedMembers"] = encrypted.ToString(CultureInfo.InvariantCulture), ["selection"] = "Smallest supported encrypted member"
                }
            };
        }

        private async Task<(long Offset, long Size, int Count)> ReadDirectoryLocationAsync()
        {
            Require(stream.Length >= 22, "The ZIP is truncated; its end record is missing.");
            var tailOffset = Math.Max(0, stream.Length - (65535 + 22));
            var tail = await ReadAsync(tailOffset, (int)(stream.Length - tailOffset)).ConfigureAwait(false);
            var eocd = -1;
            for (var i = tail.Length - 22; i >= 0; i--)
                if (U32(tail, i) == 0x06054b50 && i + 22 + U16(tail, i + 20) == tail.Length) { eocd = i; break; }
            Require(eocd >= 0, "The ZIP end record is missing or has trailing data. Check that the archive is complete.");
            Require(U16(tail, eocd + 4) == 0 && U16(tail, eocd + 6) == 0, "Split ZIP archives are not supported by the built-in extractor.");
            ulong count = U16(tail, eocd + 10), size = U32(tail, eocd + 12), offset = U32(tail, eocd + 16);
            Require(U16(tail, eocd + 8) == count, "The ZIP entry counts are inconsistent or describe a split archive.");
            var endOffset = tailOffset + eocd;
            if (count == ushort.MaxValue || size == uint.MaxValue || offset == uint.MaxValue)
            {
                var locator = await ReadAsync(endOffset - 20, 20).ConfigureAwait(false);
                Require(U32(locator, 0) == 0x07064b50 && U32(locator, 4) == 0 && U32(locator, 16) == 1, "The ZIP64 locator is missing or describes a split archive.");
                var zip64Offset = U64(locator, 8);
                Require(zip64Offset <= (ulong)Math.Max(0, endOffset - 76), "The ZIP64 end record offset is invalid.");
                var record = await ReadAsync((long)zip64Offset, 56).ConfigureAwait(false);
                Require(U32(record, 0) == 0x06064b50 && U64(record, 4) >= 44 && U64(record, 4) == (ulong)(endOffset - 20) - zip64Offset - 12,
                    "The ZIP64 end record is malformed.");
                Require(U32(record, 16) == 0 && U32(record, 20) == 0 && U64(record, 24) == U64(record, 32), "Split ZIP64 archives are not supported.");
                count = U64(record, 32); size = U64(record, 40); offset = U64(record, 48);
                endOffset = (long)zip64Offset;
            }
            Require(count <= MaxEntries && size <= MaxDirectoryBytes, "This ZIP exceeds the built-in directory limits (100,000 entries or 64 MiB).");
            Require(offset <= (ulong)endOffset && size == (ulong)endOffset - offset, "The ZIP directory offsets are invalid or this archive contains unsupported extra records.");
            return ((long)offset, (long)size, (int)count);
        }

        private async Task<byte[]> ReadMemberAsync(Entry entry)
        {
            var header = await ReadAsync((long)entry.Offset, 30).ConfigureAwait(false);
            Require(U32(header, 0) == 0x04034b50 && U16(header, 4) == entry.Version && U16(header, 6) == entry.Flags &&
                U16(header, 8) == entry.Method && U16(header, 10) == entry.Time, "The ZIP local header disagrees with its central directory.");
            var nameLength = U16(header, 26);
            var extraLength = U16(header, 28);
            var dataOffset = checked((long)entry.Offset + 30 + nameLength + extraLength);
            Require(dataOffset <= _directoryOffset && entry.Compressed <= (ulong)(_directoryOffset - dataOffset), "The ZIP encrypted data is truncated or overlaps the directory.");
            var name = await ReadAsync((long)entry.Offset + 30, nameLength).ConfigureAwait(false);
            Require(name.AsSpan().SequenceEqual(entry.Name), "The ZIP member names disagree between headers.");
            var extra = await ReadAsync((long)entry.Offset + 30 + nameLength, extraLength).ConfigureAwait(false);
            var aes = Extra(extra, 0x9901);
            Require((aes is null && entry.Aes is null) || (aes is not null && entry.Aes is not null && aes.AsSpan().SequenceEqual(entry.Aes)), "The ZIP AES fields disagree between headers.");
            if ((entry.Flags & 8) == 0)
            {
                ulong compressed = U32(header, 18), uncompressed = U32(header, 22), unusedOffset = 0;
                uint unusedDisk = 0;
                ReadZip64(extra, ref uncompressed, ref compressed, ref unusedOffset, ref unusedDisk);
                Require(compressed == entry.Compressed && uncompressed == entry.Uncompressed && U32(header, 14) == entry.Crc,
                    "The ZIP local sizes or checksum disagree with the central directory.");
            }
            else
            {
                // Read only at the directory-derived boundary; never scan signatures inside ciphertext.
                var descriptorOffset = checked(dataOffset + (long)entry.Compressed);
                var descriptor = await ReadAsync(descriptorOffset, (int)Math.Min(24, _directoryOffset - descriptorOffset)).ConfigureAwait(false);
                Require(DescriptorMatches(descriptor, 0, entry) ||
                    (descriptor.Length >= 4 && U32(descriptor, 0) == 0x08074b50 && DescriptorMatches(descriptor, 4, entry)),
                    "The ZIP data descriptor is missing or disagrees with the central directory.");
            }
            return await ReadAsync(dataOffset, checked((int)entry.Compressed)).ConfigureAwait(false);
        }

        private static bool DescriptorMatches(byte[] data, int start, Entry entry) =>
            data.Length - start >= 12 && U32(data, start) == entry.Crc &&
            ((U32(data, start + 4) == entry.Compressed && U32(data, start + 8) == entry.Uncompressed) ||
             (data.Length - start >= 20 && U64(data, start + 4) == entry.Compressed && U64(data, start + 12) == entry.Uncompressed));

        private static bool Supported(Entry entry)
        {
            if ((entry.Flags & (0x40 | 0x2000)) != 0) return false; // PKWARE strong encryption / masked headers.
            if (entry.Method == 99)
            {
                var aes = entry.Aes;
                if (aes is null || aes.Length < 7 || U16(aes, 0) is not (1 or 2) || aes[2] != 'A' || aes[3] != 'E' || aes[4] is < 1 or > 3) return false;
                var overhead = (ulong)(4 + 4 * aes[4] + 2 + 10);
                return entry.Compressed >= overhead && entry.Compressed - overhead <= MaxAesCiphertextBytes;
            }
            return entry.Aes is null && entry.Method is 0 or 8 && entry.Compressed is >= 13 and <= MaxPkzipBytes &&
                entry.Uncompressed is > 0 and <= uint.MaxValue && (entry.Method != 0 || entry.Compressed == entry.Uncompressed + 12);
        }

        private static string Format(Entry entry, byte[] data)
        {
            if (entry.Method == 99)
            {
                var strength = entry.Aes![4];
                var saltLength = 4 + 4 * strength;
                var cipherLength = data.Length - saltLength - 12;
                return $"$zip2$*0*{strength}*0*{Hex(data.AsSpan(0, saltLength))}*{Hex(data.AsSpan(saltLength, 2))}*{cipherLength:x}*{Hex(data.AsSpan(saltLength + 2, cipherLength))}*{Hex(data.AsSpan(data.Length - 10))}*$/zip2$";
            }
            var checksum = (entry.Flags & 8) != 0 ? entry.Time : entry.Crc >> 16;
            var checksumBytes = (entry.Version & 0xff) < 20 ? 2 : 1;
            return $"$pkzip$1*{checksumBytes}*2*0*{entry.Compressed:x}*{entry.Uncompressed:x}*{entry.Crc:x8}*0*0*{entry.Method}*{data.Length:x}*{checksum:x4}*{Hex(data)}*$/pkzip$";
        }

        private async Task<byte[]> ReadAsync(long offset, int count)
        {
            ct.ThrowIfCancellationRequested();
            Require(offset >= 0 && count >= 0 && offset <= stream.Length && count <= stream.Length - offset, "The ZIP contains truncated data or invalid offsets.");
            stream.Position = offset;
            var bytes = new byte[count];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            return bytes;
        }

        private static void ReadZip64(byte[] extra, ref ulong uncompressed, ref ulong compressed, ref ulong offset, ref uint disk)
        {
            var data = Extra(extra, 1);
            var index = 0;
            ulong Next()
            {
                Require(data is not null && index <= data.Length - 8, "A required ZIP64 size or offset is missing.");
                var value = U64(data!, index); index += 8; return value;
            }
            if (uncompressed == uint.MaxValue) uncompressed = Next();
            if (compressed == uint.MaxValue) compressed = Next();
            if (offset == uint.MaxValue) offset = Next();
            if (disk == ushort.MaxValue)
            {
                Require(data is not null && index <= data.Length - 4, "The ZIP64 disk number is missing.");
                disk = U32(data!, index);
            }
        }

        private static byte[]? Extra(byte[] extra, ushort wanted)
        {
            byte[]? found = null;
            for (var index = 0; index < extra.Length;)
            {
                Require(index <= extra.Length - 4, "A ZIP extra field is truncated.");
                var id = U16(extra, index); var length = U16(extra, index + 2); index += 4;
                Require(length <= extra.Length - index, "A ZIP extra field has an invalid length.");
                if (id == wanted)
                {
                    Require(found is null, "The ZIP contains duplicate structural extra fields.");
                    found = extra.AsSpan(index, length).ToArray();
                }
                index += length;
            }
            return found;
        }

        private static ushort U16(byte[] b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(offset, 2));
        private static uint U32(byte[] b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset, 4));
        private static ulong U64(byte[] b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset, 8));
        private static string Hex(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(bytes);
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    }
}
