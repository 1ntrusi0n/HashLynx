namespace HashLynx.Persistence;

/// <summary>References to user files; wordlist contents are never copied into the library.</summary>
public sealed class WordlistLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public List<string> Paths { get; set; } = [];

    internal void Validate()
    {
        if (SchemaVersion != 1 || Paths is null || Paths.Any(path => string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)))
            throw new InvalidDataException("The saved wordlist library is invalid or from an unsupported version. It has been preserved.");
        try { Paths = Paths.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new InvalidDataException("The saved wordlist library contains an invalid path. It has been preserved.", ex); }
    }
}
