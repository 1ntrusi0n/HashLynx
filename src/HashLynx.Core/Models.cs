namespace HashLynx.Core;

/// <summary>Stable family identifiers; backend numeric attack IDs belong to the integration layer.</summary>
public static class AttackFamilies
{
    public const string Dictionary = "dictionary";
    public const string Mask = "mask";
    public const string HybridWordlistMask = "hybrid-wordlist-mask";
    public const string HybridMaskWordlist = "hybrid-mask-wordlist";
    public const string Combinator = "combinator";
}

public sealed class AttackConfiguration
{
    public string Kind { get; set; } = AttackFamilies.Dictionary;
    public List<string> Wordlists { get; set; } = [];
    public List<string> RuleFiles { get; set; } = [];
    public string? Mask { get; set; }
    public string? MaskFile { get; set; }
    public Dictionary<int, string> CustomCharsets { get; set; } = [];
    public bool Increment { get; set; }
    public int IncrementMinimum { get; set; } = 1;
    public int IncrementMaximum { get; set; } = 8;
    public string? LeftRule { get; set; }
    public string? RightRule { get; set; }
    public bool Loopback { get; set; }
    public Dictionary<string, string> Parameters { get; set; } = [];
}

public sealed class CommonOptions
{
    public List<int> Devices { get; set; } = [];
    public int WorkloadProfile { get; set; } = 2;
    public bool OptimizedKernel { get; set; }
    public string SessionName { get; set; } = "hashlynx-" + Guid.NewGuid().ToString("N")[..12];
    public string? OutputPath { get; set; }
    public string? PotfilePath { get; set; }
    public bool DisablePotfile { get; set; }
    public string? RestorePath { get; set; }
    public int? TemperatureAbort { get; set; }
    public int StatusIntervalSeconds { get; set; } = 2;
    public List<string> ExtraArguments { get; set; } = [];
}

public sealed class HashcatJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Recovery job";
    public string TargetPath { get; set; } = "";
    public int HashMode { get; set; } = -1;
    public AttackConfiguration Attack { get; set; } = new();
    public CommonOptions Options { get; set; } = new();
}

public enum JobState { Ready, Running, Paused, Cracked, Exhausted, Cancelled, Checkpointed, Failed, Interrupted }

public sealed class JobRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Recovery job";
    public HashcatJob Configuration { get; set; } = new();
    public JobState State { get; set; } = JobState.Ready;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Diagnostic { get; set; }
    public string? RestorePath { get; set; }
    public JobStatusSnapshot? LatestStatus { get; set; }
}

public sealed class AttackProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public HashcatJob Configuration { get; set; } = new();
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record HashMode(int Mode, string Name, string Category = "")
{
    public string DisplayName => $"{Mode} · {Name}";
    public override string ToString() => DisplayName;
}

public sealed class JobStatusSnapshot
{
    public string State { get; set; } = "Unknown";
    public double ProgressPercent { get; set; }
    public double SpeedHashesPerSecond { get; set; }
    public long RecoveredHashes { get; set; }
    public long TotalHashes { get; set; }
    public long RejectedCandidates { get; set; }
    public long RestorePoint { get; set; }
    public long Progress { get; set; }
    public long ProgressTotal { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EstimatedCompletion { get; set; }
    public List<DeviceStatus> Devices { get; set; } = [];
}

public sealed class DeviceStatus
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public double SpeedHashesPerSecond { get; set; }
    public int? Temperature { get; set; }
    public int? Utilization { get; set; }
}

public sealed class BackendDevice
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Backend { get; set; } = "";
    public string Type { get; set; } = "";
    public string Memory { get; set; } = "";
    public string Driver { get; set; } = "";
}

public sealed record RecoveredResult(string Hash, string Plaintext, string HexPlaintext);
public sealed record ValidationMessage(string Field, string Message);
public sealed class ValidationResult
{
    public List<ValidationMessage> Errors { get; } = [];
    public List<ValidationMessage> Warnings { get; } = [];
    public bool IsValid => Errors.Count == 0;
}
