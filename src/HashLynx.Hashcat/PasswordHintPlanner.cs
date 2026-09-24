using System.Numerics;
using System.Text;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public sealed record PasswordHints(string Words, bool TryWordVariations, bool UsePattern,
    string Prefix, string Suffix, int MinimumLength, int MaximumLength, string UnknownCharacters);
public sealed record HintAttack(string Name, AttackConfiguration Attack, BigInteger Candidates);
public sealed record PasswordHintPlan(IReadOnlyList<string> Words, IReadOnlyList<HintAttack> Attacks, IReadOnlyList<string> Examples)
{
    public BigInteger CandidateEstimate => Attacks.Aggregate(BigInteger.Zero, (total, attack) => total + attack.Candidates);
}

/// <summary>Converts explicit recollections into bounded attack descriptions; never guesses what the user meant.</summary>
public static class PasswordHintPlanner
{
    public static readonly IReadOnlyList<string> CharacterChoices = ["Digits", "Lowercase letters", "Uppercase letters", "Letters", "Letters and digits", "Printable characters"];

    public static PasswordHintPlan Build(PasswordHints hints)
    {
        if (hints.Words.Length > 32768) throw new ArgumentException("Enter at most 200 remembered words or phrases.");
        var words = hints.Words.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray();
        if (words.Length > 200 || words.Any(word => Encoding.UTF8.GetByteCount(word) > 128 || word.Contains('\0')))
            throw new ArgumentException("Use at most 200 words or phrases, each at most 128 UTF-8 bytes, without NUL characters.");
        var attacks = new List<HintAttack>(); var examples = words.Take(3).ToList();
        if (words.Length > 0)
        {
            attacks.Add(new("Remembered words unchanged", new() { Kind = AttackFamilies.Dictionary }, words.Length));
            if (hints.TryWordVariations)
            {
                attacks.Add(new("Remembered words + Quick rules", new() { Kind = AttackFamilies.Dictionary, RulePresetId = "quick-v1" }, words.Length * 64));
                attacks.Add(new("Remembered words + Normal rules", new() { Kind = AttackFamilies.Dictionary, RulePresetId = "normal-v1" }, words.Length * 512));
            }
        }
        if (hints.UsePattern)
        {
            if ((hints.Prefix + hints.Suffix).Any(c => c < ' ' || c > '~'))
                throw new ArgumentException("Pattern beginnings and endings currently support printable ASCII. Put other text in remembered words instead.");
            if (hints.MinimumLength < 1 || hints.MaximumLength > 32 || hints.MaximumLength < hints.MinimumLength || hints.MaximumLength - hints.MinimumLength > 15)
                throw new ArgumentException("Choose total lengths from 1 to 32, spanning at most 16 lengths.");
            var knownLength = hints.Prefix.Length + hints.Suffix.Length;
            if (knownLength > hints.MinimumLength) throw new ArgumentException("The shortest total length cannot be shorter than the known beginning and ending together.");
            var (token, alphabet, charset) = hints.UnknownCharacters switch
            {
                "Digits" => ("?d", "0123456789", (string?)null),
                "Lowercase letters" => ("?l", "abcdefghijklmnopqrstuvwxyz", null),
                "Uppercase letters" => ("?u", "ABCDEFGHIJKLMNOPQRSTUVWXYZ", null),
                "Letters" => ("?1", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ", "?l?u"),
                "Letters and digits" => ("?1", "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "?l?u?d"),
                "Printable characters" => ("?a", new string(Enumerable.Range(32, 95).Select(n => (char)n).ToArray()), null),
                _ => throw new ArgumentException("Choose the characters to try in the unknown middle.")
            };
            for (var length = hints.MinimumLength; length <= hints.MaximumLength; length++)
            {
                var missing = length - knownLength;
                var mask = Escape(hints.Prefix) + string.Concat(Enumerable.Repeat(token, missing)) + Escape(hints.Suffix);
                attacks.Add(new($"Remembered pattern - {length} characters", new()
                {
                    Kind = AttackFamilies.Mask, Mask = mask,
                    CustomCharsets = charset is null ? [] : new() { [1] = charset }
                }, BigInteger.Pow(alphabet.Length, missing)));
                if (examples.Count < 6) examples.Add(hints.Prefix + new string(alphabet[0], missing) + hints.Suffix);
            }
        }
        if (attacks.Count == 0) throw new ArgumentException("Enter a remembered word, or enable a pattern and describe its length and characters.");
        return new(words, attacks, examples);
    }

    private static string Escape(string text) => text.Replace("?", "??", StringComparison.Ordinal);

    public static void ConfigureOptions(CommonOptions options, bool hasWords)
    {
        var incompatible = new[] { "--hex-wordlist", "--hex-charset", "--encoding-from", "--encoding-to", "--skip", "--limit" };
        if (options.ExtraArguments.Any(argument => incompatible.Contains(argument.Split('=')[0], StringComparer.Ordinal)))
            throw new ArgumentException("Clear Expert encoding, hex, skip and limit arguments before using remembered hints; they change the previewed candidates.");
        if (options.OptimizedKernel) throw new ArgumentException("Turn off optimized kernels before using remembered hints so their lengths are not restricted by that option.");
        if (hasWords && !options.ExtraArguments.Contains("--wordlist-autohex-disable")) options.ExtraArguments.Add("--wordlist-autohex-disable");
    }
}
