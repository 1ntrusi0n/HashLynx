using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using static HashLynx.Extractors.ArchiveData;

namespace HashLynx.Extractors;

/// <summary>Independent MS-CFB / MS-OFFCRYPTO metadata reader. See docs/extractor-expansion.md.</summary>
public sealed class OfficeHashExtractor() : NativeArchiveExtractor(null)
{
    public override string Id => "office";
    public override string DisplayName => "Microsoft Office";
    public override string Description => "Encrypted Word, Excel and PowerPoint OOXML files (Office 2007 Standard and 2010/2013+ Agile defaults).";
    public override IReadOnlyList<string> SupportedExtensions => [".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm", ".dotx", ".xltx", ".potx"];
    protected override async Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken ct)
    {
        var data = await new CompoundDocumentReader(stream, ct).ReadEncryptionInfoAsync();
        var major = U16(data); var minor = U16(data.AsSpan(2)); var flags = U32(data.AsSpan(4));
        if (major == 4 && minor == 4)
        {
            Require(flags == 0x40, "The Office Agile encryption flags are invalid.");
            return Agile(data);
        }
        Require(major is 2 or 3 or 4 && minor == 2 && flags == 0x24,
            "This Office encryption variant is unsupported. Use Standard AES or Agile password encryption.");
        Require(data.Length >= 44); var headerLength = U32(data.AsSpan(8));
        Require(headerLength is >= 32 and <= 65536 && 12UL + headerLength + 72 <= (ulong)data.Length);
        var header = data.AsSpan(12, (int)headerLength); var bits = U32(header[16..]);
        Require(U32(header) == flags && U32(header[4..]) == 0 && U32(header[12..]) == 0x8004
            && (bits == 128 && U32(header[8..]) == 0x660e || bits == 256 && U32(header[8..]) == 0x6610),
            "Only Office Standard AES-128/AES-256 with SHA-1 is supported.");
        var v = data.AsSpan(12 + (int)headerLength);
        Require(U32(v) == 16 && U32(v[36..]) == 20, "The Office verifier lengths are invalid.");
        // Mode 9400 compares the first 20 bytes of the 32-byte encrypted SHA-1 verifier.
        return Success($"$office$*2007*20*{bits}*16*{Hex(v.Slice(4, 16))}*{Hex(v.Slice(20, 16))}*{Hex(v.Slice(40, 20))}",
            9400, "Office Standard password verifier extracted. This recovers the password used to open the document.");
    }

    private ExtractionResult Agile(byte[] data)
    {
        XDocument xml;
        try
        {
            using var input = new MemoryStream(data, 8, data.Length - 8, false);
            using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
            xml = XDocument.Load(reader);
        }
        catch (XmlException) { throw new InvalidDataException("The Office Agile XML metadata is malformed or contains prohibited entities."); }
        XNamespace ns = "http://schemas.microsoft.com/office/2006/encryption";
        XNamespace password = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";
        Require(xml.Root?.Name == ns + "encryption");
        var keys = xml.Root!.Element(ns + "keyEncryptors")?.Elements(ns + "keyEncryptor")
            .Where(e => (string?)e.Attribute("uri") == password.NamespaceName).SelectMany(e => e.Elements(password + "encryptedKey")).ToArray() ?? [];
        Require(keys.Length == 1, "Exactly one Office password key is required; certificate-only and multiple-password documents are unsupported.");
        var key = keys[0];
        string Attribute(string name) => (string?)key.Attribute(name) ?? "";
        int Number(string name) { Require(int.TryParse(Attribute(name), NumberStyles.None, CultureInfo.InvariantCulture, out var n)); return n; }
        byte[] Bytes(string name, int size)
        {
            byte[] value;
            try { value = Convert.FromBase64String(Attribute(name)); }
            catch (FormatException) { throw new InvalidDataException("The Office password metadata contains invalid binary fields."); }
            Require(value.Length == size, "The Office password verifier field has an invalid length."); return value;
        }
        var alg = Attribute("hashAlgorithm"); var bits = Number("keyBits");
        Require(Attribute("cipherAlgorithm") == "AES" && Attribute("cipherChaining") == "ChainingModeCBC"
            && Number("blockSize") == 16 && Number("saltSize") == 16 && Number("spinCount") == 100000
            && (alg == "SHA1" && bits == 128 && Number("hashSize") == 20 || alg == "SHA512" && bits == 256 && Number("hashSize") == 64),
            "Supported Agile profiles are AES-128/SHA-1 or AES-256/SHA-512, CBC, 100,000 rounds and a 16-byte salt. Custom profiles are unsupported by the corresponding Hashcat modes.");
        var salt = Bytes("saltValue", 16); var verifier = Bytes("encryptedVerifierHashInput", 16);
        var digest = Bytes("encryptedVerifierHashValue", alg == "SHA1" ? 32 : 64);
        // The wrapped package key can have a different size from the password-derived key.
        // It is not part of the password verifier; require only a valid AES-aligned field.
        byte[] packageKey;
        try { packageKey = Convert.FromBase64String(Attribute("encryptedKeyValue")); }
        catch (FormatException) { throw new InvalidDataException("The Office wrapped key field is malformed."); }
        Require(packageKey.Length is 16 or 32, "The Office wrapped package key has an unsupported size.");
        var year = alg == "SHA1" ? 2010 : 2013;
        return Success($"$office$*{year}*100000*{bits}*16*{Hex(salt)}*{Hex(verifier)}*{Hex(digest.AsSpan(0, 32))}",
            year == 2010 ? 9500 : 9600, "Office Agile password verifier extracted. This recovers the password used to open the document.");
    }
}
