namespace HashLynx.Extractors;

public sealed class PdfHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "pdf2john.exe", "pdf2john.py", "pdf2john.pl")
{
    public override string Id => "pdf";
    public override string DisplayName => "PDF";
    public override string Description => "Encrypted PDF documents using an externally supplied pdf2john tool.";
    public override IReadOnlyList<string> SupportedExtensions => [".pdf"];
}

public sealed class ZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "zip2john.exe")
{
    public override string Id => "zip";
    public override string DisplayName => "ZIP";
    public override string Description => "PKZIP and WinZip archives using an externally supplied zip2john executable.";
    public override IReadOnlyList<string> SupportedExtensions => [".zip", ".zipx"];
}

public sealed class RarHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "rar2john.exe")
{
    public override string Id => "rar";
    public override string DisplayName => "RAR";
    public override string Description => "RAR3 and RAR5 archives using an externally supplied rar2john executable.";
    public override IReadOnlyList<string> SupportedExtensions => [".rar"];
}

public sealed class SevenZipHashExtractor(ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
    : ExternalHashExtractor(configuration, runner, "7z2john.exe", "7z2john.pl", "7z2john.py")
{
    public override string Id => "7z";
    public override string DisplayName => "7-Zip";
    public override string Description => "7-Zip archives using an externally supplied 7z2john tool and its required runtime modules.";
    public override IReadOnlyList<string> SupportedExtensions => [".7z"];
}

public sealed class BitLockerHashExtractor : ExternalHashExtractor
{
    public BitLockerHashExtractor(string? hashcatDirectory = null, ExtractorConfiguration? configuration = null, IExtractorProcessRunner? runner = null)
        : base(ResolveConfiguration(hashcatDirectory, configuration), runner) { }
    public override string Id => "bitlocker";
    public override string DisplayName => "BitLocker";
    public override string Description => "Hashcat's bitlocker2hashcat.py and Python 3; supports user-password protectors on partition images.";
    public override string ImplementationType => "Hashcat-supplied Python adapter";
    public override IReadOnlyList<string> SupportedExtensions => [".img", ".dd", ".bin", ".raw", ".vhd"];

    private static ExtractorConfiguration ResolveConfiguration(string? directory, ExtractorConfiguration? configuration)
    {
        configuration ??= new();
        if (!string.IsNullOrWhiteSpace(configuration.ToolPath) || string.IsNullOrWhiteSpace(directory)) return configuration;
        return configuration with { ToolPath = Path.Combine(directory, "tools", "bitlocker2hashcat.py") };
    }
}
