using System.Security.Cryptography;
using System.Text;
using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

/// <summary>Independent bounded KDBX header reader; database entries are never decrypted or loaded.</summary>
public sealed class KeePassHashExtractor() : NativeArchiveExtractor(null)
{
    public override string Id => "keepass";
    public override string DisplayName => "KeePass";
    public override string Description => "Password-only KDBX 3 AES and KDBX 4 AES-KDF/Argon2 databases. Key files and additional key providers are not supported.";
    public override IReadOnlyList<string> SupportedExtensions => [".kdbx"];
    private const string Notice = "Master-password verifier extracted. Use this only for a password-only database: key files, Windows-account keys and hardware/key-provider factors cannot be recovered here and are not reliably detectable from the header.";
    protected override async Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken ct)
    {
        var data = await ReadAsync(stream, 0, (int)Math.Min(stream.Length, 65536), ct);
        Require(data.Length >= 12 && U32(data) == 0x9aa2d903 && U32(data.AsSpan(4)) == 0xb54bfb67,
            "This is not a KeePass KDBX database. KeePass 1 KDB files are not supported.");
        var version = U32(data.AsSpan(8)); var major = version >> 16;
        Require(major is 3 or 4 && (version & 0xffff) <= 1, "This KeePass database version is not supported.");
        var fields = new Dictionary<byte, byte[]>(); var position = 12;
        while (true)
        {
            ct.ThrowIfCancellationRequested(); Require(fields.Count < 32 && position + (major == 3 ? 3 : 5) <= data.Length);
            var id = data[position++]; var length = major == 3 ? U16(data.AsSpan(position)) : U32(data.AsSpan(position));
            position += major == 3 ? 2 : 4;
            Require(length <= 32768 && (ulong)position + length <= (ulong)data.Length, "The KeePass header is truncated or exceeds 64 KiB.");
            Require(fields.TryAdd(id, data.AsSpan(position, (int)length).ToArray()), "The KeePass header contains duplicate fields.");
            position += (int)length;
            if (id == 0) break;
        }
        byte[] Field(byte id, int size)
        { Require(fields.TryGetValue(id, out var bytes) && bytes.Length == size, "A required KeePass header field is missing or malformed."); return bytes!; }
        Require(Field(0, 4).AsSpan().SequenceEqual("\r\n\r\n"u8), "The KeePass end-of-header marker is invalid.");
        var cipher = Hex(Field(2, 16)); var master = Field(4, 32);
        Require(U32(Field(3, 4)) <= 1, "The KeePass compression algorithm is unsupported.");
        Require(!fields.ContainsKey(12), "KeePass public custom/key-provider metadata is unsupported by this extractor.");
        if (major == 3)
        {
            Require(cipher == "31c1f2e6bf714350be5805216afc5aff", "KDBX 3 extraction requires the AES cipher.");
            var transform = Field(5, 32); var rounds = U64(Field(6, 8)); var iv = Field(7, 16); var start = Field(9, 32);
            Require(rounds is > 0 and <= uint.MaxValue, "The KeePass AES round count is outside Hashcat's supported range.");
            Require(position + 32 <= data.Length && stream.Length >= position + 48 && (stream.Length - position) % 16 == 0,
                "The KeePass encrypted payload is truncated or misaligned.");
            return Success($"$keepass$*2*{rounds}*0*{Hex(master)}*{Hex(transform)}*{Hex(iv)}*{Hex(start)}*{Hex(data.AsSpan(position, 32))}", 13400, Notice);
        }
        Require(cipher is "31c1f2e6bf714350be5805216afc5aff" or "d6038a2b8b6f4cb5a524339a31dbb59a", "The KeePass cipher is unsupported.");
        _ = Field(7, cipher.StartsWith("31", StringComparison.Ordinal) ? 16 : 12);
        Require(position + 64 <= data.Length && stream.Length >= position + 100, "The KeePass authenticated header is truncated.");
        Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(data.AsSpan(0, position)), data.AsSpan(position, 32)),
            "The KeePass header checksum does not match; the database may be damaged.");
        Require(fields.TryGetValue(11, out var dictionary), "The KeePass KDF parameters are missing.");
        var values = ReadDictionary(dictionary!);
        byte[] Parameter(string key, byte type, int size)
        { Require(values.TryGetValue(key, out var v) && v.Type == type && v.Data.Length == size, "A KeePass KDF parameter is missing or malformed."); return v.Data; }
        var uuid = Hex(Parameter("$UUID", 0x42, 16)); var seed = Parameter("S", 0x42, 32);
        ulong iterations, memory = 0; uint argonVersion = 0, parallelism = 0; int mode;
        if (uuid == "c9d9f39a628a4460bf740d08c18a4fea")
        {
            Require(values.Count == 3, "Custom KeePass AES-KDF parameters are unsupported.");
            iterations = U64(Parameter("R", 5, 8)); mode = 34301;
            Require(iterations is > 0 and <= uint.MaxValue && position is >= 200 and <= 256,
                "This KDBX 4 AES-KDF header or round count is outside Hashcat's supported range.");
        }
        else
        {
            Require(uuid is "ef636ddf8c29444b91f7a9a403e30a0c" or "9e298b1956db4773b23dfc3ec6f0a1e6", "This KeePass KDF is unsupported.");
            Require(values.Count == 6, "Additional KeePass Argon2 secret or associated-data parameters are unsupported.");
            iterations = U64(Parameter("I", 5, 8)); memory = U64(Parameter("M", 5, 8));
            argonVersion = U32(Parameter("V", 4, 4)); parallelism = U32(Parameter("P", 4, 4)); mode = 34300;
            Require(iterations is > 0 and <= 99999 && parallelism is > 0 and <= 32 && argonVersion is 16 or 19
                && memory >= 8192UL * parallelism && memory <= int.MaxValue && memory % 1024 == 0,
                "The KeePass Argon2 parameters are outside Hashcat's supported range.");
            Require(position == 253, "Hashcat's KDBX 4 Argon2 mode requires a 253-byte header. Custom headers or ChaCha20 layouts are unsupported.");
        }
        return Success($"$keepass$*4*{iterations}*{uuid[..8]}*{memory}*{argonVersion}*{parallelism}*{Hex(master)}*{Hex(seed)}*{Hex(data.AsSpan(0, position))}*{Hex(data.AsSpan(position + 32, 32))}",
            mode, Notice + (mode == 34301 ? " This variant requires a Hashcat build providing mode 34301; older releases may not include it." : ""));
    }

    private static Dictionary<string, (byte Type, byte[] Data)> ReadDictionary(byte[] data)
    {
        Require(data.Length >= 3 && U16(data) == 0x100, "The KeePass variant dictionary version is unsupported.");
        var result = new Dictionary<string, (byte, byte[])>(StringComparer.Ordinal); var pos = 2;
        byte[] ReadBytes()
        {
            Require(pos + 4 <= data.Length); var length = U32(data.AsSpan(pos)); pos += 4;
            Require(length <= 1024 && (ulong)pos + length <= (ulong)data.Length);
            var value = data.AsSpan(pos, (int)length).ToArray(); pos += (int)length; return value;
        }
        while (pos < data.Length && data[pos] != 0)
        {
            Require(result.Count < 16); var type = data[pos++]; var name = ReadBytes();
            Require(name.Length is > 0 and <= 32 && name.All(b => b is >= 0x21 and <= 0x7e));
            var value = ReadBytes(); Require(result.TryAdd(Encoding.ASCII.GetString(name), (type, value)), "Duplicate KeePass KDF parameters were found.");
        }
        Require(pos == data.Length - 1 && data[pos] == 0); return result;
    }
}
