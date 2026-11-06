namespace HashLynx.Core;

/// <summary>Physical source lines, including blanks; these are not counts of tested password guesses.</summary>
public sealed record JobWordlistInfo(
    string Path,
    long? LineCount,
    long? FileSizeBytes,
    DateTime? LastWriteUtc,
    string? CountStatus = null);
