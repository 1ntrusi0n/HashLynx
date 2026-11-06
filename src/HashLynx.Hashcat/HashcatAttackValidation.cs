using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>Backend-specific syntax remains outside the view models and generic mask parser.</summary>
public static class HashcatAttackValidation
{
    public static bool SupportsRules(string family, bool expanded) => family is AttackFamilies.Dictionary or "hashcat:9"
        || expanded && family is AttackFamilies.Mask or AttackFamilies.HybridWordlistMask or AttackFamilies.HybridMaskWordlist
            or AttackFamilies.Combinator or AttackFamilies.WordlistPattern or AttackFamilies.Pcfg;

    public static IReadOnlyList<string> CheckSpecialAttack(AttackConfiguration attack)
    {
        var errors = new List<string>();
        if (attack.Kind == AttackFamilies.WordlistPattern)
        {
            if (attack.Increment) errors.Add("Words inside a pattern cannot use increment; the word position must remain in the pattern.");
            if (attack.MaskFile is not null || attack.MaskPresetId is not null) errors.Add("Words inside a pattern currently uses a typed pattern, not a mask file or built-in mask list.");
            var mask = attack.Mask ?? "";
            var ordinary = new StringBuilder();
            int words = 0, secondWords = 0;
            for (var i = 0; i < mask.Length; i++)
            {
                if (mask[i] != '?' || i + 1 == mask.Length) { ordinary.Append(mask[i]); continue; }
                var token = mask[++i];
                if (token == 'w') { words++; continue; }
                if (token == 'q')
                {
                    secondWords++;
                    if (words == 0) errors.Add("The second word marker ?q must come after the first word marker ?w.");
                    continue;
                }
                ordinary.Append('?').Append(token);
            }
            if (words != 1) errors.Add("Include exactly one ?w to mark the first word's position.");
            if (secondWords > 1) errors.Add("Use ?q at most once for a word from the second wordlist.");
            if (attack.Wordlists.Count != (secondWords > 0 ? 2 : 1)) errors.Add("Choose one wordlist for ?w and a second wordlist only when the pattern contains ?q.");
            errors.AddRange(MaskValidator.Analyze(ordinary.Length == 0 ? "x" : ordinary.ToString(), attack.CustomCharsets).Errors);
        }
        if (attack.Kind == AttackFamilies.Pcfg)
        {
            var pcfg = attack.Pcfg ?? new();
            if (attack.Wordlists.Count > 0) errors.Add("Smart password patterns use a trained grammar. Clear the wordlists; remembered clues use a separate hint file.");
            if (attack.Increment || attack.MaskFile is not null || attack.MaskPresetId is not null || !string.IsNullOrWhiteSpace(attack.Mask))
                errors.Add("Smart password patterns use their own shape and length controls, without mask files or increment.");
            if (!string.IsNullOrEmpty(attack.LeftRule) || !string.IsNullOrEmpty(attack.RightRule)) errors.Add("Smart password patterns do not support inline left or right rules. Use a rule file instead.");
            if (pcfg.UseHints && string.IsNullOrWhiteSpace(pcfg.HintFile)) errors.Add("Enter remembered clues before using the hints grammar.");
            if (!pcfg.UseHints && !string.IsNullOrWhiteSpace(pcfg.HintFile)) errors.Add("Choose the hints grammar to use a remembered-clues file.");
            if (pcfg.MinimumLength is <= 0 or > 256 || pcfg.MaximumLength is <= 0 or > 256)
                errors.Add("Optional password lengths must be between 1 and 256 bytes.");
            if (pcfg.MinimumLength is { } minimum && pcfg.MaximumLength is { } maximum && minimum > maximum)
                errors.Add("The shortest password length cannot exceed the longest.");
            if (!string.IsNullOrWhiteSpace(pcfg.ShapeMask))
            {
                if (pcfg.MinimumLength is not null || pcfg.MaximumLength is not null) errors.Add("Choose a shape or length bounds. A shape already fixes the password length.");
                errors.AddRange(MaskValidator.Analyze(pcfg.ShapeMask, attack.CustomCharsets).Errors);
            }
            else if (attack.CustomCharsets.Count > 0) errors.Add("Custom character sets require a smart-pattern shape.");
        }
        return errors.Distinct().ToArray();
    }
}

public static class HashcatPcfgHints
{
    public static IReadOnlyList<string> ValidateWords(string words)
    {
        if (words.Length > 32768) throw new ArgumentException("Enter at most 200 remembered words or phrases.");
        var lines = words.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (lines.Length is 0 or > 200 || lines.Any(line => Encoding.UTF8.GetByteCount(line) > 128 || line.Any(c => c is '\0' or '\t')))
            throw new ArgumentException("Use 1–200 remembered words or phrases, each at most 128 UTF-8 bytes, without tabs or NUL characters.");
        return lines;
    }

    public static async Task<string> WriteAsync(string root, string words, CancellationToken cancellationToken = default)
    {
        var lines = ValidateWords(words);
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetFullPath(root), "hints");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "pcfg-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            foreach (var line in lines) await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }
}
