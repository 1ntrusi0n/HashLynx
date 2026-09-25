using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed class HashcatFacade
{
    private readonly HashcatRunner _runner = new();
    private readonly string _cacheDirectory;
    private readonly HashcatRuntimeWorkspace _workspace;
    private readonly ConcurrentDictionary<string, IReadOnlyList<BackendDevice>> _hardware = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyList<HashMode>> _modes = new(StringComparer.OrdinalIgnoreCase);
    public HashcatCommandBuilder Commands { get; }
    public RulePresetCatalog RulePresets => Commands.RulePresets;
    public event Action<string>? Diagnostic;

    public HashcatFacade(string? cacheDirectory = null)
    {
        var data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx");
        _cacheDirectory = cacheDirectory ?? Path.Combine(data, "cache");
        _workspace = new(_cacheDirectory);
        Commands = new(Path.Combine(Path.GetDirectoryName(_cacheDirectory)!, "jobs"));
    }

    public string? Discover(string? configuredDirectory = null, string? applicationDirectory = null, string? repositoryRoot = null)
    {
        // A deliberate manual override takes precedence. With no override, prefer the development tree then application-local backend.
        if (!string.IsNullOrWhiteSpace(configuredDirectory)) return ExecutableAt(configuredDirectory);
        if (!string.IsNullOrWhiteSpace(repositoryRoot) && ExecutableAt(Path.Combine(repositoryRoot, "hashcat")) is { } development) return development;
        var current = new DirectoryInfo(applicationDirectory ?? AppContext.BaseDirectory);
        for (var parent = current; parent is not null; parent = parent.Parent)
            if ((Directory.Exists(Path.Combine(parent.FullName, ".git")) || File.Exists(Path.Combine(parent.FullName, "HashLynx.sln"))) && ExecutableAt(Path.Combine(parent.FullName, "hashcat")) is { } path) return path;
        return ExecutableAt(Path.Combine(current.FullName, "hashcat"));
    }

    public async Task<HashcatInstallation> ProbeAsync(string executablePath, CancellationToken cancellationToken = default)
    {
        executablePath = Path.GetFullPath(executablePath);
        if (!File.Exists(executablePath)) throw new HashcatException("Hashcat was not found. Select its installation folder in Settings.");
        var runtime = await _workspace.PrepareAsync(executablePath, cancellationToken).ConfigureAwait(false);
        var version = await CaptureAsync(executablePath, ["--version"], cancellationToken, workingDirectory: runtime).ConfigureAwait(false);
        if (version.ExitCode != 0) throw new HashcatException("Hashcat could not report its version. Check that the complete official release is extracted.");
        var match = Regex.Match(version.StandardOutput, @"v?\d+\.\d+(?:\.\d+)?(?:[-+][\w.-]+)?", RegexOptions.CultureInvariant);
        if (!match.Success) throw new HashcatException("The selected executable did not report a recognizable Hashcat version.");
        var help = await CaptureAsync(executablePath, ["--help"], cancellationToken, workingDirectory: runtime).ConfigureAwait(false);
        if (help.ExitCode != 0) throw new HashcatException("Hashcat's help check failed. Verify that OpenCL, modules and other release folders are present.");
        return new(executablePath, match.Value, HashcatHelpParser.ParseCapabilities(help.StandardOutput)) { RuntimeDirectory = runtime };
    }

    public async Task<IReadOnlyList<HashMode>> IdentifyAsync(HashcatInstallation installation, string targetPath, CancellationToken cancellationToken = default)
    {
        if (!installation.Capabilities.Identify) throw new HashcatException("This Hashcat release does not support identification. Select a hash mode manually.");
        if (!File.Exists(targetPath)) throw new HashcatException("Select or create a readable target file before identification.");
        var result = await CaptureAsync(installation.ExecutablePath, ["--identify", "--", Path.GetFullPath(targetPath)], cancellationToken, 120, installation.WorkingDirectory).ConfigureAwait(false);
        var matches = HashModeParser.ParseIdentification(result.StandardOutput);
        if (result.ExitCode != 0 && matches.Count == 0) throw new HashcatException("Hashcat could not identify this input. Inspect its format and select a supported mode manually.");
        return matches;
    }

    public async Task<IReadOnlyList<HashMode>> GetHashModesAsync(HashcatInstallation installation, CancellationToken cancellationToken = default)
    {
        if (_modes.TryGetValue(installation.Version, out var existing)) return existing;
        var filename = Path.Combine(_cacheDirectory, "hash-modes-" + Regex.Replace(installation.Version, "[^A-Za-z0-9_.-]", "_") + ".json");
        try
        {
            if (File.Exists(filename))
            {
                var cached = JsonSerializer.Deserialize<List<HashMode>>(await File.ReadAllTextAsync(filename, cancellationToken).ConfigureAwait(false));
                if (cached is { Count: > 0 }) { _modes[installation.Version] = cached; return cached; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Diagnostic?.Invoke("The hash-mode cache could not be read; querying the installed backend."); }
        var result = await CaptureAsync(installation.ExecutablePath, ["-hh"], cancellationToken, 120, installation.WorkingDirectory).ConfigureAwait(false);
        var modes = HashModeParser.ParseHelpCatalog(result.StandardOutput);
        if (modes.Count == 0 && installation.Capabilities.HashInfo)
        {
            result = await CaptureAsync(installation.ExecutablePath, ["--hash-info"], cancellationToken, 120, installation.WorkingDirectory).ConfigureAwait(false);
            modes = HashModeParser.ParseHashInfo(result.StandardOutput);
        }
        if (modes.Count == 0) throw new HashcatException("No hash modes could be parsed from this Hashcat release. Its output format may have changed.");
        _modes[installation.Version] = modes;
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            var temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(modes), cancellationToken).ConfigureAwait(false);
            File.Move(temporary, filename, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Diagnostic?.Invoke("The hash-mode catalog is available but its cache could not be saved."); }
        return modes;
    }

    public async Task<IReadOnlyList<BackendDevice>> GetDevicesAsync(HashcatInstallation installation, CancellationToken cancellationToken = default, bool refresh = false)
    {
        if (!refresh && _hardware.TryGetValue(installation.ExecutablePath, out var cached)) return cached;
        if (!installation.Capabilities.BackendInfo) throw new HashcatException("This Hashcat release does not expose backend information.");
        var result = await CaptureAsync(installation.ExecutablePath, ["--backend-info"], cancellationToken, 60, installation.WorkingDirectory).ConfigureAwait(false);
        var devices = BackendInfoParser.Parse(result.StandardOutput);
        if (result.ExitCode != 0 && devices.Count == 0) throw new HashcatException("Hashcat could not initialize its compute backends. Check your device drivers.");
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Diagnostic?.Invoke("Backend discovery returned diagnostics; some devices may be unavailable.");
        _hardware[installation.ExecutablePath] = devices;
        return devices;
    }

    public HashcatCommand BuildCommand(HashcatInstallation installation, HashcatJob job) => Commands.Build(installation, job);
    public ValidationResult ValidateJob(HashcatInstallation installation, HashcatJob job)
    {
        var result = new JobValidator(Commands).Validate(installation, job);
        if (_modes.TryGetValue(installation.Version, out var modes) && !modes.Any(m => m.Mode == job.HashMode)) result.Errors.Add(new("Hash mode", "The selected hash mode is not supported by the installed Hashcat catalog."));
        if (_hardware.TryGetValue(installation.ExecutablePath, out var devices))
            foreach (var id in job.Options.Devices.Where(id => !devices.Any(d => d.Id == id))) result.Errors.Add(new("Devices", $"Backend device {id} was not discovered."));
        return result;
    }

    public HashcatRunningJob StartJob(HashcatInstallation installation, HashcatJob job, IProgress<HashcatEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        var validation = ValidateJob(installation, job);
        if (!validation.IsValid) throw new HashcatException(string.Join(Environment.NewLine, validation.Errors.Select(e => e.Message)));
        Directory.CreateDirectory(Commands.GetJobDirectory(job));
        if (job.Attack.Loopback) Directory.CreateDirectory(Path.Combine(Commands.GetJobDirectory(job), "induction"));
        return _runner.Start(BuildCommand(installation, job), progress, cancellationToken, installation.Capabilities.InteractiveControls);
    }

    public string GetRestorePath(HashcatJob job) => Commands.GetRestorePath(job);
    public bool CanRestore(HashcatJob job) => File.Exists(GetRestorePath(job));
    public HashcatRunningJob RestoreJob(HashcatInstallation installation, HashcatJob job, IProgress<HashcatEvent>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!installation.Capabilities.Restore || !CanRestore(job)) throw new HashcatException("No saved restore file is available for this job.");
        return _runner.Start(Commands.BuildRestore(installation, job), progress, cancellationToken, installation.Capabilities.InteractiveControls);
    }

    /// <summary>Reads only recoveries written by this session. The shared potfile cannot establish session ownership.</summary>
    public async Task<IReadOnlyList<RecoveredResult>> ReadSessionResultsAsync(HashcatJob job, CancellationToken cancellationToken = default)
    {
        var output = Commands.GetOutputPath(job);
        try
        {
            await using var stream = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var contents = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            // Hashcat terminates output records with a newline. Ignore a record that is still being written.
            var end = contents.LastIndexOf('\n');
            return end < 0 ? [] : RecoveredResultParser.Parse(contents[..(end + 1)]).DistinctBy(result => (result.Hash, result.HexPlaintext)).ToArray();
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>Check for a complete session result without retaining targets/passwords or loading the whole outfile.</summary>
    public async Task<bool> HasSessionResultsAsync(HashcatJob job, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = new FileStream(Commands.GetOutputPath(job), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var reader = new StreamReader(stream);
            var buffer = new char[4096];
            long position = 0, separator = -1, hexLength = 0;
            var validHex = true;
            var carriageReturn = false;
            int count;
            while ((count = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                foreach (var character in buffer.AsSpan(0, count))
                {
                    if (character == '\n')
                    {
                        if (separator > 0 && validHex && hexLength % 2 == 0) return true;
                        position = 0; separator = -1; hexLength = 0; validHex = true; carriageReturn = false;
                    }
                    else if (character == ':') { separator = position++; hexLength = 0; validHex = true; carriageReturn = false; }
                    else
                    {
                        position++;
                        if (separator < 0) continue;
                        if (character == '\r') { carriageReturn = true; continue; }
                        validHex &= !carriageReturn && Uri.IsHexDigit(character);
                        hexLength++;
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    /// <summary>Queries known target matches in a potfile; these are not necessarily recoveries made by this session.</summary>
    public async Task<IReadOnlyList<RecoveredResult>> ShowAsync(HashcatInstallation installation, HashcatJob job, CancellationToken cancellationToken = default)
    {
        if (job.Options.DisablePotfile || !File.Exists(Commands.GetPotfilePath(job)))
        {
            var output = Commands.GetOutputPath(job);
            return File.Exists(output) ? RecoveredResultParser.Parse(await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false)) : [];
        }
        var result = await _runner.CaptureAsync(Commands.BuildShow(installation, job), cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new HashcatException("Hashcat could not refresh recovered results. Check the target, mode and potfile settings.");
        return RecoveredResultParser.Parse(result.StandardOutput);
    }

    private async Task<ProcessCapture> CaptureAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken, int timeoutSeconds = 30, string? workingDirectory = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try { return await _runner.CaptureAsync(new(executable, workingDirectory ?? Path.GetDirectoryName(executable)!, arguments), timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new HashcatException("Hashcat did not respond within the allowed time. Check the installation and backend drivers."); }
    }

    private static string? ExecutableAt(string directory)
    {
        try
        {
            var path = directory.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? directory : Path.Combine(directory, "hashcat.exe");
            return File.Exists(path) ? Path.GetFullPath(path) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { return null; }
    }
}
