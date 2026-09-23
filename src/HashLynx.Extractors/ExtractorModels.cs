namespace HashLynx.Extractors;

public sealed record ExtractorConfiguration
{
    public string? ToolPath { get; init; }
    public string? InterpreterPath { get; init; }
}

public sealed record ExtractorAvailability
{
    public bool IsAvailable { get; init; }
    public string Status => IsAvailable ? "Available" : "Missing dependency";
    public string? ToolPath { get; init; }
    public string? InterpreterPath { get; init; }
    public string? Version { get; init; }
    public string Diagnostic { get; init; } = "";
}

public sealed record ExtractionResult
{
    public bool Success { get; init; }
    public IReadOnlyList<string> Hashes { get; init; } = [];
    public string SourceFileType { get; init; } = "";
    public string ExtractorName { get; init; } = "";
    public IReadOnlyList<int> SuggestedHashcatModes { get; init; } = [];
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

/// <summary>Extraction never modifies the source file. Suggestions must still be checked with Hashcat identification.</summary>
public interface IHashExtractor
{
    string Id { get; }
    string DisplayName { get; }
    string Description { get; }
    string ImplementationType { get; }
    bool IsBuiltIn => false;
    IReadOnlyList<string> SupportedExtensions { get; }
    Task<ExtractorAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default);
    Task<ExtractorAvailability> ValidateAsync(CancellationToken cancellationToken = default);
    Task<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default);
    Task<ExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default);
}
