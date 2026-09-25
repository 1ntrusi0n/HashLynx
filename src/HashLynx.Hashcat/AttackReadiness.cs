using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>Cheap input checks for the draft. Does not execute a backend or create output files.</summary>
public static class AttackReadiness
{
    public static IReadOnlyList<string> CheckInputs(AttackConfiguration attack, RulePresetCatalog rules, MaskPresetCatalog masks)
    {
        var errors = new List<string>();
        var dictionary = attack.Kind == AttackFamilies.Dictionary;
        var hybrid = attack.Kind is AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist;
        var combinator = attack.Kind == AttackFamilies.Combinator;
        var masked = hybrid || attack.Kind == AttackFamilies.Mask;
        if (!dictionary && !hybrid && !combinator && !masked) errors.Add("Choose a supported attack type.");
        if (dictionary && attack.Wordlists.Count == 0) errors.Add("Choose a wordlist, or use the starter list.");
        if (hybrid && attack.Wordlists.Count != 1) errors.Add("Choose exactly one wordlist for this hybrid attack.");
        if (combinator && (attack.Wordlists.Count != 2 || attack.Wordlists.Any(string.IsNullOrWhiteSpace))) errors.Add("Choose both the left and right wordlists.");
        foreach (var path in attack.Wordlists) CheckFile(path, "A selected wordlist", errors);
        foreach (var path in attack.RuleFiles) CheckFile(path, "A custom rule file", errors);
        if (attack.RulePresetId is { } ruleId)
        {
            try { rules.GetById(ruleId); }
            catch (ArgumentException) { errors.Add("Choose an available rule preset."); }
            if (!dictionary || attack.RuleFiles.Count > 0) errors.Add("Choose one Dictionary rule source.");
        }
        if (attack.RuleFiles.Count > 0 && !dictionary) errors.Add("Rule files require a Dictionary attack.");
        if (attack.Loopback && (!dictionary || (attack.RuleFiles.Count == 0 && attack.RulePresetId is null))) errors.Add("Loopback requires a Dictionary rule preset or file.");
        if (attack.MaskPresetId is { } maskId)
        {
            try { masks.GetById(maskId); }
            catch (ArgumentException) { errors.Add("Choose an available mask preset."); }
            if (attack.Kind != AttackFamilies.Mask || !string.IsNullOrWhiteSpace(attack.Mask) || !string.IsNullOrWhiteSpace(attack.MaskFile) || attack.Increment || attack.CustomCharsets.Count > 0)
                errors.Add("Choose just one mask source.");
        }
        else if (masked)
        {
            if (!string.IsNullOrWhiteSpace(attack.MaskFile))
            {
                CheckFile(attack.MaskFile, "The mask file", errors);
                errors.AddRange(MaskValidator.Analyze("x", attack.CustomCharsets).Errors);
            }
            else
            {
                var analysis = MaskValidator.Analyze(attack.Mask, attack.CustomCharsets);
                errors.AddRange(analysis.Errors);
                if (attack.Increment && attack.IncrementMaximum > analysis.Length) errors.Add("Increment maximum exceeds the mask length.");
            }
            if (attack.Increment && (attack.IncrementMinimum < 1 || attack.IncrementMaximum < attack.IncrementMinimum)) errors.Add("Choose a valid minimum and maximum mask length.");
        }
        return errors.Distinct().ToArray();
    }

    public static bool IsReadableFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); return stream.Length > 0; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return false; }
    }

    private static void CheckFile(string? path, string label, List<string> errors)
    {
        if (!IsReadableFile(path)) errors.Add(label + " is empty, missing or unreadable. Choose another file or reconnect its drive.");
    }
}
