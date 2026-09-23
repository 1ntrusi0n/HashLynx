using System.Globalization;
using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed class HashcatCommandBuilder
{
    private readonly string _stateDirectory;
    private readonly Dictionary<string, Func<AttackConfiguration, IReadOnlyList<string>>> _attackAdapters;
    public RulePresetCatalog RulePresets { get; }
    public HashcatCommandBuilder(string? stateDirectory = null, IEnumerable<AttackArgumentAdapter>? additionalAttacks = null, RulePresetCatalog? rulePresets = null)
    {
        _stateDirectory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx", "jobs");
        RulePresets = rulePresets ?? new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_stateDirectory))!, "rule-presets"));
        _attackAdapters = new()
        {
            [AttackFamilies.Dictionary] = attack => attack.Wordlists.Select(FullPath).ToArray(),
            [AttackFamilies.Combinator] = attack => attack.Wordlists.Select(FullPath).ToArray(),
            [AttackFamilies.Mask] = attack => [MaskArgument(attack)],
            [AttackFamilies.HybridWordlistMask] = attack => attack.Wordlists.Select(FullPath).Append(MaskArgument(attack)).ToArray(),
            [AttackFamilies.HybridMaskWordlist] = attack => new[] { MaskArgument(attack) }.Concat(attack.Wordlists.Select(FullPath)).ToArray()
        };
        if (additionalAttacks is not null)
            foreach (var adapter in additionalAttacks) _attackAdapters[adapter.Kind] = adapter.BuildInputs;
    }

    public string GetRestorePath(HashcatJob job) => FullPath(job.Options.RestorePath ?? Path.Combine(GetJobDirectory(job), job.Options.SessionName + ".restore"));
    public string GetPotfilePath(HashcatJob job) => FullPath(job.Options.PotfilePath ?? Path.Combine(_stateDirectory, "hashlynx.potfile"));
    public string GetOutputPath(HashcatJob job) => FullPath(job.Options.OutputPath ?? Path.Combine(GetJobDirectory(job), "recovered.txt"));
    public string GetJobDirectory(HashcatJob job) => Path.Combine(_stateDirectory, job.Id.ToString("N"));

    public IReadOnlyList<string> ResolveRuleFiles(AttackConfiguration attack)
    {
        if (attack.RulePresetId is null) return attack.RuleFiles;
        if (attack.Kind != AttackFamilies.Dictionary) throw new ArgumentException("Built-in rule presets are available for dictionary attacks.");
        if (attack.RuleFiles.Count > 0) throw new ArgumentException("Choose a built-in preset or custom rule files. Combining them multiplies rule applications.");
        return [RulePresets.GetRuleFilePath(attack.RulePresetId)];
    }

    public HashcatCommand Build(HashcatInstallation installation, HashcatJob job)
    {
        if (!installation.Capabilities.AttackModes.TryGetValue(job.Attack.Kind, out var attackId))
            throw new ArgumentException("The installed Hashcat release does not expose this attack family.");
        var args = new List<string>();
        void Option(string key, object value) { args.Add(key); args.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        Option("--hash-type", job.HashMode);
        Option("--attack-mode", attackId);
        Option("--session", job.Options.SessionName);
        Option("--workload-profile", job.Options.WorkloadProfile);
        Option("--outfile", GetOutputPath(job));
        Option("--outfile-format", "1,3");
        args.Add("--quiet");
        args.Add("--logfile-disable");
        if (installation.Capabilities.Restore) Option("--restore-file-path", GetRestorePath(job));
        if (job.Options.DisablePotfile) args.Add("--potfile-disable");
        else Option("--potfile-path", GetPotfilePath(job));
        if (installation.Capabilities.Status) args.Add("--status");
        if (installation.Capabilities.StatusJson) args.Add("--status-json");
        if (installation.Capabilities.Status) Option("--status-timer", job.Options.StatusIntervalSeconds);
        if (job.Options.Devices.Count > 0)
        {
            Option("--backend-devices", string.Join(',', job.Options.Devices));
            // Hashcat independently filters IDs and OpenCL device types. Explicit IDs should remain eligible even when they select a CPU.
            Option("--opencl-device-types", "1,2,3");
        }
        if (job.Options.OptimizedKernel) args.Add("--optimized-kernel-enable");
        if (job.Options.TemperatureAbort is { } temperature) Option("--hwmon-temp-abort", temperature);
        foreach (var rule in ResolveRuleFiles(job.Attack)) Option("--rules-file", FullPath(rule));
        if (!string.IsNullOrEmpty(job.Attack.LeftRule)) Option("--rule-left", job.Attack.LeftRule);
        if (!string.IsNullOrEmpty(job.Attack.RightRule)) Option("--rule-right", job.Attack.RightRule);
        foreach (var charset in job.Attack.CustomCharsets.OrderBy(c => c.Key)) Option($"--custom-charset{charset.Key}", charset.Value);
        if (job.Attack.Increment)
        {
            args.Add("--increment");
            Option("--increment-min", job.Attack.IncrementMinimum);
            Option("--increment-max", job.Attack.IncrementMaximum);
        }
        if (job.Attack.Loopback)
        {
            args.Add("--loopback");
            Option("--induction-dir", Path.Combine(GetJobDirectory(job), "induction"));
        }
        var extraErrors = ExpertArguments.Validate(job.Options.ExtraArguments);
        if (extraErrors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, extraErrors));
        args.AddRange(job.Options.ExtraArguments);
        args.Add("--");
        args.Add(FullPath(job.TargetPath));
        if (!_attackAdapters.TryGetValue(job.Attack.Kind, out var buildInputs)) throw new ArgumentException("No positional-argument adapter is registered for this attack family.");
        args.AddRange(buildInputs(job.Attack));
        return new(installation.ExecutablePath, installation.WorkingDirectory, args.AsReadOnly());
    }

    public HashcatCommand BuildRestore(HashcatInstallation installation, HashcatJob job) => new(installation.ExecutablePath, installation.WorkingDirectory,
        ["--session", job.Options.SessionName, "--restore-file-path", GetRestorePath(job), "--restore"]);

    public HashcatCommand BuildShow(HashcatInstallation installation, HashcatJob job)
    {
        var arguments = new List<string> { "--quiet", "--show", "--hash-type", job.HashMode.ToString(CultureInfo.InvariantCulture), "--potfile-path", GetPotfilePath(job), "--outfile-format", "1,3" };
        arguments.AddRange(job.Options.ExtraArguments.Where(value => value is "--username" or "--dynamic-x" or "--hex-salt"));
        arguments.Add("--");
        arguments.Add(FullPath(job.TargetPath));
        return new(installation.ExecutablePath, installation.WorkingDirectory, arguments);
    }

    private static string FullPath(string path) => Path.GetFullPath(path);
    private static string MaskArgument(AttackConfiguration attack) => string.IsNullOrWhiteSpace(attack.MaskFile) ? attack.Mask ?? "" : FullPath(attack.MaskFile);
}

public sealed record AttackArgumentAdapter(string Kind, Func<AttackConfiguration, IReadOnlyList<string>> BuildInputs);

/// <summary>The preview is informational Windows argument quoting, never a shell execution mechanism.</summary>
public static class CommandPreview
{
    public static string Format(string executable, IEnumerable<string> arguments) => string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));
    public static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c is '"' or '&' or '|' or '<' or '>' or '^')) return argument;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"') result.Append('\\', slashes * 2 + 1).Append('"');
            else result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

public static class ExpertArguments
{
    // An explicit extension surface prevents aliases and attached short-option values from overriding managed settings.
    private static readonly HashSet<string> Flags = ["--hex-charset", "--hex-salt", "--hex-wordlist", "--username", "--dynamic-x", "--markov-disable", "--markov-classic", "--markov-inverse", "--slow-candidates", "--keep-guessing", "--wordlist-autohex-disable"];
    private static readonly HashSet<string> Values = ["--runtime", "--skip", "--limit", "--encoding-from", "--encoding-to", "--markov-threshold", "--segment-size", "--scrypt-tmto", "--nonce-error-corrections"];
    public static IReadOnlyList<string> Validate(IReadOnlyList<string> arguments)
    {
        var errors = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var arg = arguments[index];
            var equal = arg.IndexOf('=');
            var key = equal >= 0 ? arg[..equal] : arg;
            if (Flags.Contains(key) && equal < 0) continue;
            if (Values.Contains(key))
            {
                if (equal >= 0 && equal < arg.Length - 1) continue;
                if (equal < 0 && index + 1 < arguments.Count && !arguments[index + 1].StartsWith('-')) { index++; continue; }
                errors.Add($"Advanced option {key} requires a value."); continue;
            }
            errors.Add($"Advanced argument '{key}' is managed by HashLynx or unsupported. Use its dedicated control; permitted extensions are: {string.Join(", ", Flags.Concat(Values).Order())}.");
        }
        return errors;
    }

    public static IReadOnlyList<string> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var args = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';
        var started = false;
        foreach (var character in text)
        {
            if (quote != '\0') { if (character == quote) quote = '\0'; else current.Append(character); started = true; }
            else if (character is '\'' or '"') { quote = character; started = true; }
            else if (char.IsWhiteSpace(character)) { if (started) { args.Add(current.ToString()); current.Clear(); started = false; } }
            else { current.Append(character); started = true; }
        }
        if (quote != '\0') throw new ArgumentException("An advanced argument has an unmatched quote.");
        if (started) args.Add(current.ToString());
        return args;
    }
}
