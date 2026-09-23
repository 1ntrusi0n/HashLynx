using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed class HashcatCapabilities
{
    public bool Identify { get; set; }
    public bool StatusJson { get; set; }
    public bool Status { get; set; }
    public bool BackendInfo { get; set; }
    public bool HashInfo { get; set; }
    public bool Restore { get; set; }
    public bool InteractiveControls { get; set; }
    public int CustomCharsetCount { get; set; } = 4;
    public Dictionary<string, int> AttackModes { get; set; } = [];
}

public sealed record HashcatInstallation(string ExecutablePath, string Version, HashcatCapabilities Capabilities)
{
    public string DirectoryPath => Path.GetDirectoryName(ExecutablePath)!;
    public string? RuntimeDirectory { get; init; }
    public string WorkingDirectory => RuntimeDirectory ?? DirectoryPath;
}

public sealed record HashcatCommand(string ExecutablePath, string WorkingDirectory, IReadOnlyList<string> Arguments)
{
    public string Preview => CommandPreview.Format(ExecutablePath, Arguments);
}

public sealed record HashcatEvent(string Kind, string? Message = null, JobStatusSnapshot? Status = null);
public sealed record HashcatRunResult(int ExitCode, JobState State, string Diagnostic, bool Cancelled = false);
public sealed record ProcessCapture(int ExitCode, string StandardOutput, string StandardError);
public enum HashcatControl { Pause, Resume, Checkpoint, Status, Quit }

public sealed class HashcatException(string message) : Exception(message);
