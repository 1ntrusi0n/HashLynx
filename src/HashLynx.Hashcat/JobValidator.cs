using System.Text.RegularExpressions;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed class JobValidator(HashcatCommandBuilder builder)
{
    public ValidationResult Validate(HashcatInstallation installation, HashcatJob job)
    {
        var result = new ValidationResult();
        void Error(string field, string message) => result.Errors.Add(new(field, message));
        void RequireFile(string field, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) Error(field, $"{field} does not exist or cannot be read.");
            else
            {
                try { using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Error(field, $"{field} is not readable."); }
            }
        }
        RequireFile("Hashcat executable", installation.ExecutablePath);
        RequireFile("Target", job.TargetPath);
        if (job.HashMode < 0) Error("Hash mode", "Select a hash mode. Ambiguous identification requires an explicit choice.");
        if (!installation.Capabilities.AttackModes.ContainsKey(job.Attack.Kind)) Error("Attack", "This attack family is not supported by the detected Hashcat release.");
        var count = job.Attack.Wordlists.Count;
        if (job.Attack.Kind == AttackFamilies.Dictionary && count == 0) Error("Wordlists", "Select at least one wordlist.");
        if (job.Attack.Kind == AttackFamilies.Combinator && count != 2) Error("Wordlists", "Combinator requires exactly two wordlists, left then right.");
        if (job.Attack.Kind is AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist && count != 1) Error("Wordlists", "Hybrid requires one wordlist and one mask.");
        foreach (var path in job.Attack.Wordlists) RequireFile("Wordlist", path);
        IReadOnlyList<string> ruleFiles = job.Attack.RuleFiles;
        try { ruleFiles = builder.ResolveRuleFiles(job.Attack); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Error("Rules", ex is ArgumentException ? ex.Message : "The built-in rule preset could not be prepared. Check access to the application data directory.");
        }
        foreach (var path in ruleFiles) RequireFile("Rule file", path);
        string? maskFile = job.Attack.MaskFile;
        var maskPresetInvalid = false;
        try { maskFile = builder.ResolveMaskFile(job.Attack); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            maskPresetInvalid = true;
            Error("Mask", ex is ArgumentException ? ex.Message : "The built-in mask list could not be prepared. Check access to the application data directory.");
        }
        var hasMask = job.Attack.Kind is AttackFamilies.Mask or AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist;
        if (hasMask && !maskPresetInvalid)
        {
            if (!string.IsNullOrWhiteSpace(maskFile))
            {
                RequireFile("Mask file", maskFile);
                foreach (var error in MaskValidator.Analyze("x", job.Attack.CustomCharsets).Errors) Error("Charset", error);
            }
            else
            {
                var mask = MaskValidator.Analyze(job.Attack.Mask, job.Attack.CustomCharsets);
                foreach (var error in mask.Errors) Error("Mask", error);
                if (job.Attack.Increment && job.Attack.IncrementMaximum > mask.Length) Error("Increment", "Increment maximum exceeds the mask length.");
            }
        }
        else if (!hasMask && (job.Attack.Increment || job.Attack.CustomCharsets.Count > 0)) Error("Mask", "Increment and custom charsets require a mask attack.");
        if (job.Attack.MaskPresetId is not null && !maskPresetInvalid)
            result.Warnings.Add(new("Mask list", "The built-in 1,000-pattern list contains nearly one trillion candidates. It can take a very long time, especially for encrypted documents or drives. Remembered hints can greatly reduce the search."));
        if (ruleFiles.Count > 0 && job.Attack.Kind is not (AttackFamilies.Dictionary or "hashcat:9")) Error("Rules", "This Hashcat release accepts rule files with dictionary or association attacks. Use inline left/right rules for hybrid or combinator.");
        if (job.Attack.Loopback && job.Attack.Kind != AttackFamilies.Dictionary) Error("Loopback", "Loopback is available with dictionary attacks.");
        if (job.Attack.Loopback && ruleFiles.Count == 0) Error("Loopback", "Loopback requires at least one dictionary rule file.");
        if (job.Attack.Loopback && job.Options.ExtraArguments.Any(value => value == "--limit" || value.StartsWith("--limit=", StringComparison.Ordinal))) Error("Loopback", "Loopback cannot be combined with an advanced candidate limit.");
        if (job.Attack.Increment && (job.Attack.IncrementMinimum < 1 || job.Attack.IncrementMaximum < job.Attack.IncrementMinimum)) Error("Increment", "Increment must have a positive minimum no greater than its maximum.");
        foreach (var pair in job.Attack.CustomCharsets)
            if (pair.Key < 1 || pair.Key > installation.Capabilities.CustomCharsetCount || string.IsNullOrEmpty(pair.Value)) Error("Charset", "Custom charset is empty or unsupported by the installed release.");
        if (!Regex.IsMatch(job.Options.SessionName, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$", RegexOptions.CultureInvariant)) Error("Session", "Use 1–80 letters, numbers, dots, underscores or hyphens, starting with a letter or number.");
        if (job.Options.WorkloadProfile is < 1 or > 4) Error("Workload", "Workload profile must be between 1 and 4.");
        if (job.Options.StatusIntervalSeconds is < 1 or > 3600) Error("Status interval", "Use a status interval between 1 and 3600 seconds.");
        if (job.Options.TemperatureAbort is < 40 or > 120) Error("Temperature", "Temperature abort must be between 40 and 120 °C.");
        if (job.Options.Devices.Any(id => id < 1)) Error("Devices", "Device IDs must be positive backend device numbers.");
        foreach (var error in ExpertArguments.Validate(job.Options.ExtraArguments)) Error("Advanced arguments", error);
        if (job.Options.OptimizedKernel) result.Warnings.Add(new("Optimized kernel", "Optimized kernels may limit supported password lengths."));
        if (!installation.Capabilities.StatusJson) result.Warnings.Add(new("Status", "This Hashcat release lacks JSON status; live metrics will be unavailable."));
        try
        {
            var outputs = new[] { builder.GetOutputPath(job), builder.GetRestorePath(job), builder.GetPotfilePath(job) };
            if (File.Exists(outputs[0])) Error("Output", "Choose a new output file for each session. Reusing an existing file would mix recovered results. Use Restore to resume an existing session.");
            var inputs = new[] { job.TargetPath, installation.ExecutablePath }.Concat(job.Attack.Wordlists).Concat(ruleFiles).Append(maskFile ?? "").Where(p => !string.IsNullOrWhiteSpace(p)).Select(Path.GetFullPath).ToList();
            if (outputs.Distinct(StringComparer.OrdinalIgnoreCase).Count() != outputs.Length) Error("Output", "Output, restore and potfile must use different paths.");
            foreach (var path in outputs)
            {
                if (inputs.Contains(path, StringComparer.OrdinalIgnoreCase)) { Error("Output", "Output, restore and potfile paths must not overwrite an input file."); continue; }
                if (IsWithin(path, installation.DirectoryPath)) { Error("Output", "Choose a writable data location outside the Hashcat installation."); continue; }
                CheckWritableParent(path, result);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException) { Error("Paths", "An output or session path is invalid or inaccessible."); }
        return result;
    }

    internal static bool IsWithin(string path, string directory) => Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void CheckWritableParent(string path, ValidationResult result)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var probe = Path.Combine(directory, ".hashlynx-write-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0) result.Errors.Add(new("Output", "An output or session file is read-only."));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Errors.Add(new("Output", "An output or session directory is not writable.")); }
    }
}
