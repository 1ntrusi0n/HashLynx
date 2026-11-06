using System.Globalization;
using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed class HashcatCommandBuilder
{
    private readonly string _stateDirectory;
    private readonly Dictionary<string, Func<AttackConfiguration, IReadOnlyList<string>>> _attackAdapters;
    public RulePresetCatalog RulePresets { get; }
    public MaskPresetCatalog MaskPresets { get; }
    public HashcatCommandBuilder(string? stateDirectory = null, IEnumerable<AttackArgumentAdapter>? additionalAttacks = null, RulePresetCatalog? rulePresets = null, MaskPresetCatalog? maskPresets = null)
    {
        _stateDirectory = stateDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx", "jobs");
        RulePresets = rulePresets ?? new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_stateDirectory))!, "rule-presets"));
        MaskPresets = maskPresets ?? new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(_stateDirectory))!, "mask-presets"));
        _attackAdapters = new()
        {
            [AttackFamilies.Dictionary] = attack => attack.Wordlists.Select(FullPath).ToArray(),
            [AttackFamilies.Combinator] = attack => attack.Wordlists.Select(FullPath).ToArray(),
            [AttackFamilies.Mask] = attack => [MaskArgument(attack)],
            [AttackFamilies.HybridWordlistMask] = attack => attack.Wordlists.Select(FullPath).Append(MaskArgument(attack)).ToArray(),
            [AttackFamilies.HybridMaskWordlist] = attack => new[] { MaskArgument(attack) }.Concat(attack.Wordlists.Select(FullPath)).ToArray(),
            [AttackFamilies.WordlistPattern] = attack => new[] { attack.Mask ?? "" }.Concat(attack.Wordlists.Select(FullPath)).ToArray()
        };
        if (additionalAttacks is not null)
            foreach (var adapter in additionalAttacks) _attackAdapters[adapter.Kind] = adapter.BuildInputs;
    }

    public string GetRestorePath(HashcatJob job) => FullPath(job.Options.RestorePath ?? Path.Combine(GetJobDirectory(job), job.Options.SessionName + ".restore"));
    public string GetPotfilePath(HashcatJob job) => FullPath(job.Options.PotfilePath ?? Path.Combine(_stateDirectory, "hashlynx.potfile"));
    public string GetOutputPath(HashcatJob job) => FullPath(job.Options.OutputPath ?? Path.Combine(GetJobDirectory(job), "recovered.txt"));
    public string GetJobDirectory(HashcatJob job) => Path.Combine(_stateDirectory, job.Id.ToString("N"));

    public IReadOnlyList<string> ResolveRuleFiles(AttackConfiguration attack, bool expandedRules = false)
    {
        if (attack.RulePresetId is null) return attack.RuleFiles;
        if (!HashcatAttackValidation.SupportsRules(attack.Kind, expandedRules)) throw new ArgumentException("This backend supports built-in rule presets only with dictionary attacks.");
        if (attack.RuleFiles.Count > 0) throw new ArgumentException("Choose a built-in preset or custom rule files. Combining them multiplies rule applications.");
        return [RulePresets.GetRuleFilePath(attack.RulePresetId)];
    }

    public string? ResolveMaskFile(AttackConfiguration attack)
    {
        if (attack.MaskPresetId is null) return attack.MaskFile;
        ValidateMaskPreset(attack);
        return MaskPresets.GetMaskFilePath(attack.MaskPresetId);
    }

    private static void ValidateMaskPreset(AttackConfiguration attack)
    {
        if (attack.Kind != AttackFamilies.Mask) throw new ArgumentException("Built-in mask lists are available for mask attacks only. Hybrid attacks require a custom mask.");
        if (!string.IsNullOrEmpty(attack.Mask) || !string.IsNullOrEmpty(attack.MaskFile))
            throw new ArgumentException("Choose a built-in mask list, a custom mask, or a mask file; do not combine mask sources.");
        if (attack.Increment || attack.CustomCharsets.Count > 0)
            throw new ArgumentException("The built-in mask list already defines its lengths and character sets. Turn off increment and clear custom charsets, or use a custom mask.");
    }

    public HashcatCommand Build(HashcatInstallation installation, HashcatJob job)
    {
        var specialErrors = HashcatAttackValidation.CheckSpecialAttack(job.Attack);
        if (specialErrors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, specialErrors));
        if (job.Attack.MaskPresetId is not null) ValidateMaskPreset(job.Attack);
        if (!installation.Capabilities.AttackModes.TryGetValue(job.Attack.Kind, out var attackId))
            throw new ArgumentException("The installed Hashcat release does not expose this attack family.");
        var args = new List<string>();
        void Option(string key, object value) { args.Add(key); args.Add(Convert.ToString(value, CultureInfo.InvariantCulture)!); }
        Option("--hash-type", job.HashMode);
        Option("--attack-mode", attackId);
        Option("--session", job.Options.SessionName);
        if (!installation.IsVersion8OrLater && installation.Capabilities.WorkloadProfilesEffective) Option("--workload-profile", job.Options.WorkloadProfile);
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
            Option("--opencl-device-types", installation.IsVersion8OrLater ? "1,2" : "1,2,3");
        }
        if (job.Options.OptimizedKernel) args.Add("--optimized-kernel-enable");
        if (job.Options.TemperatureAbort is { } temperature) Option("--hwmon-temp-abort", temperature);
        var ruleFiles = ResolveRuleFiles(job.Attack, installation.Capabilities.ExpandedRules);
        if (ruleFiles.Count > 0 && !HashcatAttackValidation.SupportsRules(job.Attack.Kind, installation.Capabilities.ExpandedRules))
            throw new ArgumentException("The installed backend does not support rule files for this attack.");
        foreach (var rule in ruleFiles) Option("--rules-file", FullPath(rule));
        if (job.Attack.RulesConcatenated && !job.Options.ExtraArguments.Contains("--rules-concat"))
        {
            if (!installation.Capabilities.RulesConcatenation) throw new ArgumentException("This backend does not support joining rule lists. Hashcat 8 is required.");
            args.Add("--rules-concat");
        }
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
        var extraErrors = ExpertArguments.Validate(job.Options.ExtraArguments, installation);
        if (extraErrors.Count > 0) throw new ArgumentException(string.Join(Environment.NewLine, extraErrors));
        args.AddRange(job.Options.ExtraArguments);
        args.Add("--");
        args.Add(FullPath(job.TargetPath));
        if (job.Attack.Kind == AttackFamilies.Pcfg) args.AddRange(PcfgArguments(installation, job.Attack));
        else
        {
            if (!_attackAdapters.TryGetValue(job.Attack.Kind, out var buildInputs)) throw new ArgumentException("No positional-argument adapter is registered for this attack family.");
            args.AddRange(buildInputs(job.Attack));
        }
        return new(installation.ExecutablePath, installation.WorkingDirectory, args.AsReadOnly());
    }

    public HashcatCommand BuildRestore(HashcatInstallation installation, HashcatJob job)
    {
        if (!installation.IsVersion8OrLater) return new(installation.ExecutablePath, installation.WorkingDirectory,
            ["--session", job.Options.SessionName, "--restore-file-path", GetRestorePath(job), "--restore"]);
        if (!installation.Capabilities.RestorePosition) throw new ArgumentException("The installed backend does not expose safe position-based restore.");
        var command = Build(installation, job);
        var arguments = command.Arguments.ToList();
        arguments.Insert(arguments.IndexOf("--"), "--restore-position");
        return command with { Arguments = arguments.AsReadOnly() };
    }

    public static void SynchronizeCompatibilityOptions(HashcatJob job)
    {
        job.Options.ExtraArguments.RemoveAll(value => value == "--rules-concat");
        if (job.Attack.RulesConcatenated) job.Options.ExtraArguments.Add("--rules-concat");
    }

    public string GetPcfgRulesetPath(HashcatInstallation installation, AttackConfiguration attack) =>
        Path.Combine(installation.WorkingDirectory, "pcfg", attack.Pcfg?.UseHints == true ? "hints.tar.xz" : "default-passwords.tar.xz");

    private IReadOnlyList<string> PcfgArguments(HashcatInstallation installation, AttackConfiguration attack)
    {
        var pcfg = attack.Pcfg ?? new();
        var values = new List<string> { GetPcfgRulesetPath(installation, attack) };
        if (pcfg.UseHints) values.Add("hintfile=" + FullPath(pcfg.HintFile!));
        if (pcfg.MinimumLength is { } minimum) values.Add("pwmin=" + minimum.ToString(CultureInfo.InvariantCulture));
        if (pcfg.MaximumLength is { } maximum) values.Add("pwmax=" + maximum.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(pcfg.ShapeMask)) values.Add("mask=" + pcfg.ShapeMask);
        return values;
    }

    public HashcatCommand BuildShow(HashcatInstallation installation, HashcatJob job)
    {
        var arguments = new List<string> { "--quiet", "--show", "--hash-type", job.HashMode.ToString(CultureInfo.InvariantCulture), "--potfile-path", GetPotfilePath(job), "--outfile-format", "1,3" };
        arguments.AddRange(job.Options.ExtraArguments.Where(value => value is "--username" or "--dynamic-x" or "--hex-salt"));
        arguments.Add("--");
        arguments.Add(FullPath(job.TargetPath));
        return new(installation.ExecutablePath, installation.WorkingDirectory, arguments);
    }

    private static string FullPath(string path) => Path.GetFullPath(path);
    private string MaskArgument(AttackConfiguration attack)
    {
        var file = ResolveMaskFile(attack);
        return string.IsNullOrWhiteSpace(file) ? attack.Mask ?? "" : FullPath(file);
    }
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
    public static IReadOnlyList<string> Validate(IReadOnlyList<string> arguments, HashcatInstallation? installation = null)
    {
        var errors = new List<string>();
        for (var index = 0; index < arguments.Count; index++)
        {
            var arg = arguments[index];
            var equal = arg.IndexOf('=');
            var key = equal >= 0 ? arg[..equal] : arg;
            if (key == "--rules-concat" && equal < 0 && installation?.Capabilities.RulesConcatenation == true) continue;
            if (key == "--segment-size" && installation?.IsVersion8OrLater == true)
                errors.Add("Hashcat 8 removed --segment-size. Remove this advanced option; wordlist seeking is managed automatically.");
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
