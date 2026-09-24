namespace HashLynx.Persistence;

/// <summary>References to user files; wordlist contents are never copied into the library.</summary>
public sealed class WordlistLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Paths { get; set; } = [];
    // Optional metadata keeps existing version-one path libraries readable without rewriting them on load.
    public List<WordlistEntry> Entries { get; set; } = [];

    internal void Validate()
    {
        if (SchemaVersion != 1 || Paths is null || Entries is null || Paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("The saved wordlist library is invalid or from an unsupported version. It has been preserved.");
        try
        {
            Paths = Paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (Entries.Any(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path) || !Path.IsPathFullyQualified(entry.Path)
                || entry.Name is null || entry.Name.Length > 200 || entry.Name.Any(char.IsControl)
                || entry.FileSizeBytes < 0 || entry.LineCount < 0))
                throw new InvalidDataException("The saved wordlist metadata is invalid. It has been preserved.");
            var metadata = Entries.GroupBy(entry => Path.GetFullPath(entry.Path), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            Entries = Paths.Select(path => metadata.TryGetValue(path, out var entry) ? entry with { Path = path } : new WordlistEntry { Path = path }).ToList();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The saved wordlist library contains an invalid path. It has been preserved.", ex); }
    }
}

public sealed record WordlistEntry
{
    public string Path { get; init; } = "";
    public string Name { get; init; } = "";
    public long? FileSizeBytes { get; init; }
    public long? LineCount { get; init; }
    public DateTime? LastWriteUtc { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? System.IO.Path.GetFileName(Path) : Name;
    public override string ToString() => DisplayName;
}
