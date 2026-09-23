using static HashLynx.Extractors.PdfSyntax;
using static HashLynx.Extractors.PdfDocumentMetadata;

namespace HashLynx.Extractors;

/// <summary>Native extraction of Standard Security Handler password verification fields.</summary>
public sealed class PdfHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : NativeArchiveExtractor(string.IsNullOrWhiteSpace(configuration?.ToolPath) ? null : new ExternalPdfHashExtractor(configuration, runner))
{
    public override string Id => "pdf";
    public override string DisplayName => "PDF";
    public override string Description => "Built-in PDF Standard Security Handler extraction (RC4 and AES, revisions 2 through 6).";
    public override IReadOnlyList<string> SupportedExtensions => [".pdf"];
    protected override async Task<ExtractionResult> ExtractNativeAsync(FileStream stream, CancellationToken ct)
    {
        Need(stream.Length is > 0 and <= 64 * 1024 * 1024, "Built-in PDF extraction supports documents up to 64 MiB.");
        var data = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        var document = new PdfDocumentMetadata(data, ct); document.Read();
        var value = document.Resolve(document.Trailer.GetValueOrDefault("Encrypt"));
        Need(value is not null, "This PDF is not encrypted with a password.");
        var encryption = Dictionary(value);
        object? Get(string key) => document.Resolve(encryption.GetValueOrDefault(key));
        Need(Name(Get("Filter")) == "Standard" && Get("SubFilter") is null,
            "This PDF uses certificate-based or a custom security handler; only Standard password encryption is supported.");
        var revision = Number(Get("R")); var version = Number(Get("V"));
        var bits = Get("Length") is { } length ? Number(length) : revision >= 5 ? 256 : 40;
        var mode = (version, revision, bits) switch
        {
            (1 or 2, 2, 40) => 10400, (2, 3, 128) or (4, 4, 128) => 10500,
            (5, 5, 256) => 10600, (5, 6, 256) => 10700, _ => 0
        };
        Need(mode != 0, "The PDF encryption revision or key length is unsupported. Supported: R2/40-bit, R3-R4/128-bit and R5-R6/256-bit.");
        var permissions = Number(Get("P")); Need(permissions is >= int.MinValue and <= int.MaxValue);
        var encryptMetadata = Get("EncryptMetadata") ?? true; Need(encryptMetadata is bool);
        var ids = Array(document.Resolve(document.Trailer.GetValueOrDefault("ID"))); Need(ids.Count == 2);
        var id = Bytes(document.Resolve(ids[0]));
        Need(mode == 10400 ? id.Length == 16 : mode == 10500 ? id.Length is 0 or 16 or 32 : id.Length <= 512,
            "The PDF document identifier length is unsupported by the selected Hashcat mode.");
        var u = Bytes(Get("U")); var o = Bytes(Get("O")); var keySize = revision >= 5 ? 48 : 32;
        Need(u.Length == keySize && o.Length == keySize, "PDF password verification fields have invalid lengths.");
        var hash = FormattableString.Invariant($"$pdf${version}*{revision}*{bits}*{permissions}*{((bool)encryptMetadata ? 1 : 0)}*{id.Length}*{ArchiveData.Hex(id)}*{u.Length}*{ArchiveData.Hex(u)}*{o.Length}*{ArchiveData.Hex(o)}");
        if (revision >= 5)
        {
            var oe = Bytes(Get("OE")); var ue = Bytes(Get("UE"));
            Need(oe.Length == 32 && ue.Length == 32 && Bytes(Get("Perms")).Length == 16, "PDF AES-256 key fields have invalid lengths.");
            hash += $"*32*{ArchiveData.Hex(oe)}*32*{ArchiveData.Hex(ue)}";
        }
        return Success(hash, mode, $"Built-in PDF read Standard Security Handler revision {revision}. Recovery targets the document's user/open password; it does not remove permissions or rewrite the PDF.");
    }
    private static byte[] Bytes(object? value) { Need(value is PdfBytes); return ((PdfBytes)value!).Value; }
}
