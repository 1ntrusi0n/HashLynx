using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

public sealed class SevenZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : NativeArchiveExtractor(string.IsNullOrWhiteSpace(configuration?.ToolPath) ? null : new ExternalSevenZipHashExtractor(configuration, runner))
{
    public override string Id => "7z";
    public override string DisplayName => "7-Zip";
    public override string Description => "Built-in 7z AES extraction, including encrypted and LZMA-compressed headers.";
    public override IReadOnlyList<string> SupportedExtensions => [".7z"];
    protected override async Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken cancellationToken)
    {
        var result = await new SevenZipReader(stream, cancellationToken).ExtractAsync().ConfigureAwait(false);
        return Success(result.Hash, 11600, result.Notice);
    }

    private sealed class SevenZipReader(FileStream stream, CancellationToken ct)
    {
        private const int MaxHeader = 8 * 1024 * 1024;
        private const int MaxData = 8 * 1024 * 1024 - 16;
        private ulong _nextHeaderOffset;
        private sealed record Coder(string Method, byte[] Properties);
        private sealed class Folder
        {
            public required Coder[] Coders { get; init; }
            public required int[] Order { get; init; }
            public ulong[] UnpackSizes { get; set; } = [];
            public ulong FinalSize => UnpackSizes[Order[^1]];
            public uint? Crc { get; set; }
            public int SubCount { get; set; } = 1;
            public ulong[] SubSizes { get; set; } = [];
            public uint?[] SubCrcs { get; set; } = [];
        }
        private sealed record Streams(ulong Position, ulong[] Sizes, Folder[] Folders);

        public async Task<(string Hash, string Notice)> ExtractAsync()
        {
            var start = await ReadAsync(stream, 0, 32, ct).ConfigureAwait(false);
            Require(start.AsSpan(0, 6).SequenceEqual(Convert.FromHexString("377ABCAF271C")) && start[6] == 0, "This is not a supported single-volume 7z archive.");
            Require(Crc32(start.AsSpan(12, 20)) == U32(start.AsSpan(8)), "The 7-Zip start header checksum is invalid.");
            var relative = U64(start.AsSpan(12)); var length = U64(start.AsSpan(20));
            Require(relative <= (ulong)stream.Length - 32 && length is > 0 and <= MaxHeader);
            _nextHeaderOffset = 32 + relative;
            Require(length == (ulong)stream.Length - _nextHeaderOffset, "The 7-Zip header is truncated or has unsupported trailing data.");
            var header = await ReadAsync(stream, _nextHeaderOffset, (int)length, ct).ConfigureAwait(false);
            Require(Crc32(header) == U32(start.AsSpan(28)), "The 7-Zip next header checksum is invalid.");
            for (var depth = 0; depth < 3; depth++)
            {
                ct.ThrowIfCancellationRequested();
                var reader = new ArchiveCursor(header); var kind = reader.Byte();
                if (kind == 0x17)
                {
                    var encoded = ReadStreams(reader); Require(reader.Remaining == 0);
                    if (encoded.Folders.Any(folder => folder.Coders.Any(coder => coder.Method == "06f10701")))
                        return await SelectHashAsync(encoded, true).ConfigureAwait(false);
                    Require(encoded.Folders.Length == 1 && encoded.Sizes.Length == 1, "Complex compressed 7-Zip headers need an external extractor.");
                    var folder = encoded.Folders[0];
                    Require(folder.Coders.Length == 1 && folder.FinalSize is > 0 and <= MaxHeader && folder.Crc is not null,
                        "The compressed 7-Zip header lacks a bounded output size or checksum.");
                    var packed = await ReadPackAsync(encoded.Position, encoded.Sizes[0], MaxHeader).ConfigureAwait(false);
                    var coder = folder.Coders[0];
                    header = coder.Method switch
                    {
                        "00" when (ulong)packed.Length == folder.FinalSize => packed,
                        "030101" => LzmaHeaderDecoder.Decode(packed, coder.Properties, (int)folder.FinalSize, ct),
                        _ => throw new InvalidDataException("This 7-Zip header compression method needs an external extractor.")
                    };
                    Require(Crc32(header) == folder.Crc, "The decoded 7-Zip metadata checksum is invalid.");
                    continue;
                }
                Require(kind == 1, "The 7-Zip main header is missing.");
                Streams? main = null;
                var sawFiles = false;
                while ((kind = reader.Byte()) != 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (kind == 2)
                    {
                        while (reader.Byte() != 0) reader.Skip(reader.SevenZipCount(MaxHeader));
                    }
                    else if (kind == 4)
                    {
                        Require(main is null && !sawFiles, "The 7-Zip stream information is duplicated or misplaced.");
                        main = ReadStreams(reader);
                    }
                    else if (kind == 5)
                    {
                        Require(!sawFiles); sawFiles = true;
                        reader.SevenZipCount();
                        while (reader.Byte() != 0) reader.Skip(reader.SevenZipCount(MaxHeader));
                    }
                    else throw new InvalidDataException("Additional or external 7-Zip metadata streams need an external extractor.");
                }
                Require(reader.Remaining == 0 && main is not null, "The 7-Zip archive has no supported encrypted data stream.");
                return await SelectHashAsync(main!, false).ConfigureAwait(false);
            }
            throw new InvalidDataException("The 7-Zip archive has too many nested encoded headers.");
        }

        private Streams ReadStreams(ArchiveCursor reader)
        {
            ulong position = 0; ulong[] sizes = []; Folder[] folders = []; var kind = reader.Byte();
            if (kind == 6)
            {
                position = reader.SevenZipInteger(); var count = reader.SevenZipCount();
                Require(reader.Byte() == 9); sizes = new ulong[count];
                for (var i = 0; i < count; i++) sizes[i] = reader.SevenZipInteger();
                kind = reader.Byte(); if (kind == 10) { ReadDigests(reader, count); kind = reader.Byte(); }
                Require(kind == 0); kind = reader.Byte();
            }
            if (kind == 7)
            {
                Require(reader.Byte() == 11); var count = reader.SevenZipCount();
                Require(reader.Byte() == 0, "External 7-Zip folder records need an external extractor.");
                folders = new Folder[count];
                for (var i = 0; i < count; i++) { ct.ThrowIfCancellationRequested(); folders[i] = ReadFolder(reader); }
                Require(reader.Byte() == 12);
                foreach (var folder in folders)
                {
                    folder.UnpackSizes = new ulong[folder.Coders.Length];
                    for (var i = 0; i < folder.Coders.Length; i++) folder.UnpackSizes[i] = reader.SevenZipInteger();
                }
                kind = reader.Byte();
                if (kind == 10)
                {
                    var crcs = ReadDigests(reader, count);
                    for (var i = 0; i < count; i++) folders[i].Crc = crcs[i];
                    kind = reader.Byte();
                }
                Require(kind == 0); kind = reader.Byte();
            }
            Require(folders.Length > 0 && folders.Length == sizes.Length, "Only one packed input per 7-Zip folder is supported.");
            if (kind == 8)
            {
                kind = reader.Byte();
                if (kind == 13)
                {
                    var total = 0;
                    foreach (var folder in folders) { folder.SubCount = reader.SevenZipCount(); total = checked(total + folder.SubCount); Require(total <= 100_000); }
                    kind = reader.Byte();
                }
                foreach (var folder in folders)
                {
                    folder.SubSizes = new ulong[folder.SubCount]; ulong sum = 0;
                    if (folder.SubCount == 0) continue;
                    Require(kind == 9 || folder.SubCount == 1, "7-Zip substream sizes are missing.");
                    for (var i = 0; i < folder.SubCount - 1; i++)
                    { var size = reader.SevenZipInteger(); Require(size <= folder.FinalSize - sum); folder.SubSizes[i] = size; sum += size; }
                    folder.SubSizes[^1] = folder.FinalSize - sum;
                }
                if (kind == 9) kind = reader.Byte();
                var digestCount = folders.Sum(folder => folder.SubCount == 1 && folder.Crc.HasValue ? 0 : folder.SubCount);
                var digests = kind == 10 ? ReadDigests(reader, digestCount) : new uint?[digestCount];
                if (kind == 10) kind = reader.Byte();
                var digestIndex = 0;
                foreach (var folder in folders)
                {
                    folder.SubCrcs = new uint?[folder.SubCount];
                    for (var i = 0; i < folder.SubCount; i++)
                        folder.SubCrcs[i] = folder.SubCount == 1 && folder.Crc.HasValue ? folder.Crc : digests[digestIndex++];
                }
                Require(kind == 0); kind = reader.Byte();
            }
            else foreach (var folder in folders) { folder.SubSizes = [folder.FinalSize]; folder.SubCrcs = [folder.Crc]; }
            Require(kind == 0);
            ulong packEnd = position;
            foreach (var size in sizes) { Require(packEnd <= _nextHeaderOffset - 32 && size <= _nextHeaderOffset - 32 - packEnd); packEnd += size; }
            return new(position, sizes, folders);
        }

        private static Folder ReadFolder(ArchiveCursor reader)
        {
            var count = reader.SevenZipCount(4); Require(count > 0);
            var coders = new Coder[count];
            for (var i = 0; i < count; i++)
            {
                var flags = reader.Byte(); var idLength = flags & 15;
                Require(idLength is > 0 and <= 8 && (flags & 0xc0) == 0, "Unsupported 7-Zip coder description.");
                var method = Hex(reader.Take(idLength));
                if ((flags & 0x10) != 0) Require(reader.SevenZipInteger() == 1 && reader.SevenZipInteger() == 1,
                    "Multi-input 7-Zip filters need an external extractor.");
                var properties = (flags & 0x20) != 0 ? reader.Take(reader.SevenZipCount(64)) : [];
                coders[i] = new(method, properties);
            }
            var edges = new Dictionary<int, int>(); var inputs = new HashSet<int>();
            for (var i = 0; i < count - 1; i++)
            {
                var input = reader.SevenZipCount(count - 1); var output = reader.SevenZipCount(count - 1);
                Require(input != output && inputs.Add(input) && edges.TryAdd(output, input), "The 7-Zip coder graph is invalid.");
            }
            var first = Enumerable.Range(0, count).Single(index => !inputs.Contains(index));
            var order = new List<int>(); var current = first;
            while (true)
            {
                Require(!order.Contains(current), "The 7-Zip coder graph contains a cycle."); order.Add(current);
                if (!edges.TryGetValue(current, out current)) break;
            }
            Require(order.Count == count, "The 7-Zip coder graph is disconnected.");
            return new() { Coders = coders, Order = order.ToArray() };
        }

        private static uint?[] ReadDigests(ArchiveCursor reader, int count)
        {
            var all = reader.Byte(); Require(all is 0 or 1);
            var defined = all == 0 ? reader.Take((count + 7) / 8) : [];
            var result = new uint?[count];
            for (var i = 0; i < count; i++)
                if (all == 1 || (defined[i / 8] & (0x80 >> (i % 8))) != 0) result[i] = reader.UInt32();
            return result;
        }

        private async Task<(string Hash, string Notice)> SelectHashAsync(Streams streams, bool headers)
        {
            var position = streams.Position;
            (Folder Folder, ulong Position, ulong Size, int Index, uint Crc, ulong CrcSize, int Type, byte Power, byte[] Iv, byte[] Props)? best = null;
            for (var i = 0; i < streams.Folders.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                var folder = streams.Folders[i]; var size = streams.Sizes[i];
                var start = position; position += size;
                var ordered = folder.Order.Select(index => folder.Coders[index]).ToArray();
                if (ordered[0].Method != "06f10701" || ordered.Length > 2 || size is < 16 or > MaxData || size % 16 != 0) continue;
                var aesSize = folder.UnpackSizes[folder.Order[0]];
                if (aesSize == 0 || aesSize > size || size - aesSize >= 16) continue;
                var props = ordered[0].Properties;
                if (props.Length == 0) continue;
                var power = (byte)(props[0] & 63); var saltSize = 0; var ivSize = 0; var propertyOffset = 1;
                if ((props[0] & 0xc0) != 0)
                {
                    Require(props.Length >= 2); propertyOffset = 2;
                    saltSize = (props[0] >> 7) + (props[1] >> 4); ivSize = ((props[0] >> 6) & 1) + (props[1] & 15);
                }
                Require(propertyOffset + saltSize + ivSize == props.Length && ivSize <= 16, "The 7-Zip AES properties are malformed.");
                if (saltSize != 0 || power > 24) continue; // Hashcat 11600 supports unsalted AES; bound work factors.
                var iv = new byte[16]; props.AsSpan(propertyOffset, ivSize).CopyTo(iv);
                var type = ordered.Length == 1 ? 0 : ordered[1].Method switch { "00" => 0, "030101" => 1, "21" => 2, "040108" => 7, _ => -1 };
                var attributes = ordered.Length == 1 ? [] : ordered[1].Properties;
                if (type < 0 || (type == 1 && attributes.Length != 5) || (type == 2 && attributes.Length != 1) || (type == 7 && attributes.Length != 0)) continue;
                if ((type == 1 && (attributes[0] >= 225 || U32(attributes.AsSpan(1)) > 64 * 1024 * 1024)) ||
                    (type == 2 && attributes[0] > 30)) continue;
                uint? crc = folder.Crc; var crcSize = folder.FinalSize;
                if (crc is null && crcSize <= 9_999_999 && folder.SubCrcs.Length > 0 && folder.SubCrcs.All(value => value.HasValue))
                {
                    uint combined = 0;
                    for (var sub = 0; sub < folder.SubCrcs.Length; sub++)
                    {
                        ct.ThrowIfCancellationRequested();
                        combined = CombineCrc32(combined, folder.SubCrcs[sub]!.Value, folder.SubSizes[sub]);
                    }
                    crc = combined;
                }
                if (crc is null || crcSize == 0 || crcSize > 9_999_999 || (type == 0 && crcSize > aesSize)) continue;
                if (best is null || size < best.Value.Size) best = (folder, start, size, i + 1, crc.Value, crcSize, type, power, iv, attributes);
            }
            Require(best is not null, "No compatible encrypted 7-Zip stream was found. Built-in recovery requires unsalted AES with Copy, LZMA, LZMA2 or Deflate, a CRC, ciphertext below 8 MiB and verification output below 10 MB. Filters, other codecs or encryption settings need an external workflow.");
            var selected = best!.Value;
            var data = await ReadPackAsync(selected.Position, selected.Size, MaxData).ConfigureAwait(false);
            var unpack = selected.Type == 0 ? selected.CrcSize : selected.Folder.UnpackSizes[selected.Folder.Order[0]];
            var hash = FormattableString.Invariant($"$7z${selected.Type}${selected.Power}$0$$16${Hex(selected.Iv)}${selected.Crc}${selected.Size}${unpack}${Hex(data)}");
            if (selected.Type != 0) hash += FormattableString.Invariant($"${selected.CrcSize}${Hex(selected.Props)}");
            return (hash, headers ? "Built-in 7-Zip selected the encrypted archive headers for password recovery." :
                $"Built-in 7-Zip selected encrypted stream #{selected.Index}. Recovery verifies this stream; other streams can have different passwords.");
        }

        private Task<byte[]> ReadPackAsync(ulong position, ulong size, int limit)
        {
            Require(size <= (ulong)limit && position <= _nextHeaderOffset - 32 && size <= _nextHeaderOffset - 32 - position, "The 7-Zip packed data is truncated or exceeds the built-in limit.");
            return ReadAsync(stream, 32 + position, (int)size, ct);
        }
    }
}
