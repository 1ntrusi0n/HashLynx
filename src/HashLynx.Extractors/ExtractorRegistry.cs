namespace HashLynx.Extractors;

public sealed class ExtractorRegistry
{
    public IReadOnlyList<IHashExtractor> All { get; }

    public ExtractorRegistry(IEnumerable<IHashExtractor> extractors)
    {
        var registrations = extractors.ToArray();
        if (registrations.Select(x => x.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != registrations.Length)
            throw new ArgumentException("Extractor IDs must be unique.", nameof(extractors));
        All = Array.AsReadOnly(registrations);
    }

    public IHashExtractor? GetById(string id) => All.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public async Task<IReadOnlyList<IHashExtractor>> FindCandidatesAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var matches = new List<IHashExtractor>();
        foreach (var extractor in All)
            if (await extractor.CanHandleAsync(filePath, cancellationToken)) matches.Add(extractor);
        return matches.OrderByDescending(x => x.SupportedExtensions.Contains(Path.GetExtension(filePath), StringComparer.OrdinalIgnoreCase)).ToArray();
    }

    public static ExtractorRegistry CreateDefault(string? hashcatDirectory = null,
        IReadOnlyDictionary<string, ExtractorConfiguration>? configurations = null, IExtractorProcessRunner? runner = null)
    {
        ExtractorConfiguration? Config(string id) => configurations?.TryGetValue(id, out var configuration) == true ? configuration : null;
        return new([
            // Built-in formats always use native readers. Preserve old configuration in storage,
            // but do not let an invisible legacy tool path override them.
            new PdfHashExtractor(Config("pdf"), runner), new ZipHashExtractor(),
            new RarHashExtractor(), new SevenZipHashExtractor(), new BitLockerHashExtractor()
        ]);
    }
}
