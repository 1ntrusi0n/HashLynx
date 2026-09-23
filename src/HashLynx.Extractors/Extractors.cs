namespace HashLynx.Extractors;

public sealed class PdfHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "pdf2john.exe", "pdf2john.py", "pdf2john.pl")
{
    public override string Id => "pdf";
    public override string DisplayName => "PDF";
    public override string Description => "Encrypted PDF documents using an externally supplied pdf2john tool.";
    public override IReadOnlyList<string> SupportedExtensions => [".pdf"];
}

internal sealed class ExternalZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "zip2john.exe")
{
    public override string Id => "zip";
    public override string DisplayName => "ZIP";
    public override string Description => "PKZIP and WinZip archives using an externally supplied zip2john executable.";
    public override IReadOnlyList<string> SupportedExtensions => [".zip", ".zipx"];
}

internal sealed class ExternalRarHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "rar2john.exe")
{
    public override string Id => "rar";
    public override string DisplayName => "RAR";
    public override string Description => "RAR3 and RAR5 archives using an externally supplied rar2john executable.";
    public override IReadOnlyList<string> SupportedExtensions => [".rar"];
}

internal sealed class ExternalSevenZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "7z2john.exe", "7z2john.pl", "7z2john.py")
{
    public override string Id => "7z";
    public override string DisplayName => "7-Zip";
    public override string Description => "7-Zip archives using an externally supplied 7z2john tool and its required runtime modules.";
    public override IReadOnlyList<string> SupportedExtensions => [".7z"];
}
