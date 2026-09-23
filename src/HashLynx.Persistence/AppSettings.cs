namespace HashLynx.Persistence;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string? HashcatDirectory { get; set; }
    public string DefaultOutputDirectory { get; set; } = string.Empty;
    public string Theme { get; set; } = "Dark";
    public int DefaultWorkloadProfile { get; set; } = 2;
    public bool ExpertMode { get; set; }
    public int StatusIntervalSeconds { get; set; } = 2;
    public Dictionary<string, ExtractorToolSettings> ExtractorTools { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ExtractorToolSettings
{
    public string? ToolPath { get; set; }
    public string? InterpreterPath { get; set; }
}
