using System.Security.Cryptography;
using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

public sealed class RarHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : NativeArchiveExtractor(string.IsNullOrWhiteSpace(configuration?.ToolPath) ? null : new ExternalRarHashExtractor(configuration, runner))
{
    public override string Id => "rar";
    public override string DisplayName => "RAR";
    public override string Description => "Built-in RAR3 and RAR5 extraction, including encrypted headers. Leave the tool path blank, or configure rar2john for other variants.";
    public override IReadOnlyList<string> SupportedExtensions => [".rar"];

    protected override async Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken ct)
    {
        var signature = await ReadAsync(stream, 0, 8, ct).ConfigureAwait(false);
        if (signature.AsSpan().StartsWith("Rar!\x1a\x07\x00"u8)) return await ReadRar3Async(stream, ct).ConfigureAwait(false);
        Require(signature.AsSpan().SequenceEqual("Rar!\x1a\x07\x01\x00"u8), "This is not a supported RAR3/RAR5 archive. Self-extracting containers are not supported.");
        return await ReadRar5Async(stream, ct).ConfigureAwait(false);
    }

    private async Task<ExtractionResult> ReadRar3Async(FileStream stream, CancellationToken ct)
    {
        ulong offset = 7, scanned = 0;
        string? selectedHash = null; var selectedMode = 0; ulong selectedSize = ulong.MaxValue;
        var selectedIndex = 0; var fileCount = 0; var encryptedCount = 0; var ended = false; var main = false;
        for (var blocks = 0; offset < (ulong)stream.Length; blocks++)
        {
            Require(blocks < 100_000 && scanned < 64 * 1024 * 1024, "RAR metadata exceeds the built-in scan limits.");
            var basic = await ReadAsync(stream, offset, 7, ct).ConfigureAwait(false);
            var type = basic[2]; var flags = U16(basic.AsSpan(3)); var headerSize = U16(basic.AsSpan(5));
            Require(headerSize >= 7);
            var header = await ReadAsync(stream, offset, headerSize, ct).ConfigureAwait(false);
            scanned += headerSize;
            Require((ushort)Crc32(header.AsSpan(2)) == U16(header), "The RAR3 header checksum is invalid; the archive may be damaged.");
            ulong packed = 0;
            if ((flags & 0x8000) != 0) { Require(headerSize >= 11); packed = U32(header.AsSpan(7)); }
            if (!main)
            {
                Require(type == 0x73 && headerSize >= 13, "The RAR3 main header is missing.");
                main = true;
                Require((flags & 1) == 0, "Split RAR3 volumes are not supported by the built-in extractor.");
                if ((flags & 0x80) != 0)
                {
                    Require((ulong)stream.Length >= offset + headerSize + 48, "The encrypted RAR3 headers are truncated.");
                    Require((flags & 0x40) == 0, "RAR3 encrypted headers with recovery records need an external extractor.");
                    var end = await ReadAsync(stream, (ulong)stream.Length - 24, 24, ct).ConfigureAwait(false);
                    return Success($"$RAR3$*0*{Hex(end.AsSpan(0, 8))}*{Hex(end.AsSpan(8))}", 12500,
                        "Built-in RAR selected the encrypted archive headers for password recovery.");
                }
            }
            else if (type == 0x74)
            {
                fileCount++;
                Require(headerSize >= 32 && (flags & 0x8000) != 0);
                ulong unpacked = U32(header.AsSpan(11)); var nameSize = U16(header.AsSpan(26)); var extra = 32;
                if ((flags & 0x100) != 0)
                {
                    Require(headerSize >= 40);
                    packed |= (ulong)U32(header.AsSpan(32)) << 32; unpacked |= (ulong)U32(header.AsSpan(36)) << 32; extra = 40;
                }
                Require(nameSize <= headerSize - extra); extra += nameSize;
                byte[]? salt = null;
                if ((flags & 0x400) != 0) { Require(extra + 8 <= headerSize); salt = header.AsSpan(extra, 8).ToArray(); }
                Require((flags & 3) == 0, "Split RAR3 members need an external extractor.");
                if ((flags & 4) != 0)
                {
                    encryptedCount++;
                    var method = header[25];
                    // Stay within both the binary size and token length limits of modes 23700/23800.
                    var supported = header[24] is >= 29 and <= 36 && salt is not null && (flags & 0x10) == 0 && (flags & 0xe0) != 0xe0 &&
                        method is >= 0x30 and <= 0x35 && packed is >= 16 and <= 327520 && packed % 16 == 0 && unpacked is > 0 and <= 655360 &&
                        (method != 0x30 || (unpacked <= packed && packed - unpacked < 16));
                    if (supported && (selectedHash is null || (method == 0x30 && selectedMode != 23700) ||
                        ((method == 0x30) == (selectedMode == 23700) && packed < selectedSize)))
                    {
                        var data = await ReadAsync(stream, offset + headerSize, (int)packed, ct).ConfigureAwait(false);
                        selectedHash = FormattableString.Invariant($"$RAR3$*1*{Hex(salt!)}*{Hex(header.AsSpan(16, 4))}*{packed}*{unpacked}*1*{Hex(data)}*{method:x2}");
                        selectedMode = method == 0x30 ? 23700 : 23800; selectedSize = packed; selectedIndex = fileCount;
                    }
                }
            }
            else if (type == 0x7b)
            {
                Require((flags & 1) == 0, "This RAR3 archive continues in another volume."); ended = true;
            }
            Require(offset + headerSize <= (ulong)stream.Length && packed <= (ulong)stream.Length - offset - headerSize);
            offset += headerSize + packed;
            if (ended) break;
        }
        Require(ended && offset == (ulong)stream.Length, "The RAR3 end record is missing or has unsupported trailing data.");
        Require(selectedHash is not null, encryptedCount == 0 ? "This RAR3 archive has no encrypted files." :
            "No supported RAR3 member was found. Solid continuations, old encryption, oversized or empty members need another workflow.");
        return Success(selectedHash!, selectedMode, $"Built-in RAR selected file #{selectedIndex} ({encryptedCount} encrypted). Recovery verifies this member; other members can have different passwords.");
    }

    private async Task<ExtractionResult> ReadRar5Async(FileStream stream, CancellationToken ct)
    {
        ulong offset = 8, scanned = 0; var main = false; var ended = false; var fileCount = 0; var selectedIndex = 0;
        string? selectedHash = null;
        for (var blocks = 0; offset < (ulong)stream.Length; blocks++)
        {
            Require(blocks < 100_000 && scanned < 64 * 1024 * 1024, "RAR metadata exceeds the built-in scan limits.");
            var prefix = await ReadAsync(stream, offset, (int)Math.Min(14, (ulong)stream.Length - offset), ct).ConfigureAwait(false);
            var sizeReader = new ArchiveCursor(prefix); var crc = sizeReader.UInt32(); var bodySize = sizeReader.RarInteger();
            Require(bodySize <= 2 * 1024 * 1024 && sizeReader.Position <= 7, "The RAR5 header exceeds the supported size.");
            var headerSize = checked(sizeReader.Position + (int)bodySize);
            var header = await ReadAsync(stream, offset, headerSize, ct).ConfigureAwait(false);
            scanned += (uint)headerSize;
            Require(Crc32(header.AsSpan(4)) == crc, "The RAR5 header checksum is invalid; the archive may be damaged.");
            var reader = new ArchiveCursor(header); reader.Skip(sizeReader.Position);
            var type = reader.RarInteger(); var flags = reader.RarInteger();
            var extraSize = (flags & 1) != 0 ? reader.RarInteger() : 0;
            var dataSize = (flags & 2) != 0 ? reader.RarInteger() : 0;
            Require(extraSize <= (ulong)reader.Remaining && dataSize <= (ulong)stream.Length - offset - (uint)headerSize);
            Require((flags & (8 | 16)) == 0, "Split RAR5 members need an external extractor.");
            var extraStart = headerSize - (int)extraSize;
            if (type == 4)
            {
                Require(!main && blocks == 0 && extraSize == 0 && dataSize == 0, "The RAR5 encryption header is misplaced or unsupported.");
                var crypt = ReadCrypt(reader, false);
                Require(crypt is not null, "This RAR5 header has unsupported encryption or no usable password check value.");
                Require(reader.Remaining == 0);
                var following = await ReadAsync(stream, offset + (uint)headerSize, 32, ct).ConfigureAwait(false);
                return Success(FormatRar5(crypt!, following.AsSpan(0, 16)), 13000,
                    "Built-in RAR selected the archive header password verifier. Encrypted file contents remain unchanged.");
            }
            if (type == 1)
            {
                Require(!main, "The RAR5 main header is repeated."); main = true;
                var archiveFlags = reader.RarInteger();
                Require((archiveFlags & 3) == 0, "Split RAR5 volumes need an external extractor.");
            }
            else if (type is 2 or 3)
            {
                Require(main, "The RAR5 main header is missing.");
                if (type == 2) fileCount++;
                var fileFlags = reader.RarInteger(); reader.RarInteger(); reader.RarInteger();
                if ((fileFlags & 2) != 0) reader.Skip(4);
                if ((fileFlags & 4) != 0) reader.Skip(4);
                reader.RarInteger(); reader.RarInteger(); var nameSize = reader.RarInteger();
                Require(nameSize <= (ulong)Math.Max(0, extraStart - reader.Position)); reader.Skip((int)nameSize);
                Require(reader.Position == extraStart);
                var extra = new ArchiveCursor(reader.Take((int)extraSize)); var cryptSeen = false;
                while (extra.Remaining > 0)
                {
                    var recordSize = extra.RarInteger(); Require(recordSize > 0 && recordSize <= (ulong)extra.Remaining);
                    var record = new ArchiveCursor(extra.Take((int)recordSize));
                    if (record.RarInteger() != 1) continue;
                    Require(!cryptSeen, "The RAR5 encryption extra field is duplicated."); cryptSeen = true;
                    var crypt = ReadCrypt(record, true);
                    if (crypt is not null && type == 2 && (fileFlags & 1) == 0 && selectedHash is null)
                    { selectedHash = FormatRar5(crypt, crypt.Iv); selectedIndex = fileCount; }
                }
            }
            else if (type == 5)
            {
                Require(main && (reader.RarInteger() & 1) == 0, "This RAR5 archive is incomplete or continues in another volume."); ended = true;
            }
            else Require((flags & 4) != 0, "An unsupported mandatory RAR5 block was found.");
            Require(reader.Position <= extraStart || reader.Position == headerSize);
            offset += (uint)headerSize + dataSize;
            if (ended) break;
        }
        Require(ended && offset == (ulong)stream.Length, "The RAR5 end record is missing or has unsupported trailing data.");
        Require(selectedHash is not null, "No usable RAR5 password verifier was found. The archive may be unencrypted or use unsupported encryption settings.");
        return Success(selectedHash!, 13000, $"Built-in RAR selected file #{selectedIndex}'s password verifier. Other members can have different passwords.");
    }

    private sealed record Rar5Crypt(byte Power, byte[] Salt, byte[] Iv, byte[] Check);
    private static Rar5Crypt? ReadCrypt(ArchiveCursor reader, bool file)
    {
        var version = reader.RarInteger(); var flags = reader.RarInteger();
        if (version != 0 || (flags & 1) == 0 || (flags & ~(file ? 3ul : 1ul)) != 0) return null;
        var power = reader.Byte(); var salt = reader.Take(16); var iv = file ? reader.Take(16) : [];
        var check = reader.Take(8); var checksum = reader.Take(4);
        Require(SHA256.HashData(check).AsSpan(0, 4).SequenceEqual(checksum), "The RAR5 password verifier checksum is invalid.");
        if (power is < 1 or > 24) return null;
        return new(power, salt, iv, check);
    }
    private static string FormatRar5(Rar5Crypt crypt, ReadOnlySpan<byte> iv) =>
        FormattableString.Invariant($"$rar5$16${Hex(crypt.Salt)}${crypt.Power:D2}${Hex(iv)}$8${Hex(crypt.Check)}");
}
