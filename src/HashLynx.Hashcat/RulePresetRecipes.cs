using System.Globalization;

namespace HashLynx.Hashcat;

/// <summary>Original, deterministic HashLynx recipes. These are effort budgets, not an empirical effectiveness ranking. Changed recipe output requires a new preset version ID.</summary>
public static class RulePresetRecipes
{
    private static readonly string[] Leet = ["sa@", "sa4", "se3", "si1", "so0", "ss$", "ss5", "st7"];
    private static readonly string[] Symbols = ["!", "?", ".", "@", "#", "_"];

    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Generate()
    {
        var rules = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var presets = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        void Add(string rule) { if (seen.Add(rule)) rules.Add(rule); }
        void Snapshot(string id, int expected)
        {
            if (rules.Count != expected) throw new InvalidOperationException($"Recipe {id} has {rules.Count} rules; expected {expected}.");
            presets[id] = rules.ToArray();
        }
        void Suffixes(IEnumerable<string> values, params string[] cases)
        {
            foreach (var value in values) foreach (var casing in cases) Add(casing + Append(value));
        }

        // Quick: identity, basic case/shape, single digits, six symbols, selected short numeric endings and single substitutions.
        foreach (var rule in new[] { ":", "l", "u", "c", "C", "T0", "r", "d" }) Add(rule);
        Suffixes(Numbers(0, 9, 1), "", "c");
        Suffixes(Symbols, "", "c");
        Suffixes(["12", "123", "1234", "01", "00", "69", "99", "007"], "", "c");
        foreach (var rule in Leet) Add(rule);
        Snapshot(RulePresetCatalog.QuickId, 64);

        // Normal: complete two-digit endings in original/capitalized case, bounded years, simple prefixes and extra casing.
        Suffixes(Numbers(1980, 2035, 4), "", "c");
        Suffixes(Numbers(0, 99, 2), "", "c");
        foreach (var digit in Numbers(0, 9, 1)) foreach (var casing in new[] { "", "c", "l" }) Add(casing + Prepend(digit));
        Suffixes(Numbers(0, 9, 1).Concat(Symbols), "l", "u");
        foreach (var rule in Leet) foreach (var casing in new[] { "c", "l", "u" }) Add(rule + casing);
        foreach (var year in Numbers(2020, 2026, 4)) foreach (var symbol in new[] { "!", "@", "#", "?" }) Suffixes([year + symbol], "", "c");
        Suffixes(["12345", "123456", "1234567", "123456789"], "");
        Snapshot(RulePresetCatalog.NormalId, 512);

        // Heavy: complete three-digit endings, older years, two-digit prefixes, numeric/symbol and substitution combinations.
        Suffixes(Numbers(0, 999, 3), "", "c");
        Suffixes(Numbers(1900, 1979, 4), "", "c");
        foreach (var number in Numbers(0, 99, 2)) foreach (var symbol in new[] { "!", "@", "#", "?" }) Suffixes([number + symbol], "", "c");
        foreach (var number in Numbers(0, 99, 2)) foreach (var casing in new[] { "", "c" }) Add(casing + Prepend(number));
        foreach (var rule in Leet) foreach (var casing in new[] { "", "c" })
            foreach (var suffix in Numbers(0, 9, 1).Concat(Symbols).Concat(["12", "123", "1234"])) Add(rule + casing + Append(suffix));
        for (var position = 1; position <= 7; position++) Add("T" + position);
        foreach (var rule in new[] { "[", "]", "{", "}", "f", "q" }) Add(rule);
        for (var position = 1; position <= 3; position++) foreach (var digit in Numbers(0, 9, 1)) Add("i" + position + digit);
        for (var position = 0; position <= 1; position++) foreach (var digit in Numbers(0, 9, 1)) Add("o" + position + digit);
        for (var position = 0; position <= 3; position++) foreach (var digit in Numbers(0, 9, 1)) Add("T" + position + Append(digit));
        foreach (var suffix in Numbers(0, 9, 1)) Add("r" + Append(suffix));
        foreach (var suffix in Numbers(0, 9, 1).Append("!")) Add("d" + Append(suffix));
        Snapshot(RulePresetCatalog.HeavyId, 4096);

        // Super: full four-digit suffixes, three-digit prefixes, paired substitutions and year prefixes.
        Suffixes(Numbers(0, 9999, 4), "");
        foreach (var number in Numbers(0, 999, 3)) Add(Prepend(number));
        for (var left = 0; left < Leet.Length; left++)
            for (var right = left + 1; right < Leet.Length; right++)
            {
                if (Leet[left][1] == Leet[right][1]) continue;
                foreach (var casing in new[] { "", "c", "l" })
                    foreach (var suffix in new[] { "", "!", "1", "12", "123" }) Add(Leet[left] + Leet[right] + casing + Append(suffix));
            }
        foreach (var year in Numbers(1900, 2035, 4)) foreach (var casing in new[] { "", "c" }) Add(casing + Prepend(year));
        // Spend the remaining fixed budget evenly between lower/uppercase numeric endings, in stable numeric order.
        foreach (var number in Numbers(0, 999, 3))
        {
            foreach (var casing in new[] { "l", "u" })
            {
                if (rules.Count == 16384) break;
                Add(casing + Append(number));
            }
            if (rules.Count == 16384) break;
        }
        Snapshot(RulePresetCatalog.SuperId, 16384);
        return presets;
    }

    public static string FileContents(string id, IReadOnlyList<string> rules) =>
        $"# HashLynx {id}: original deterministic recipe, MIT license.\n# {rules.Count} unique rule lines; cumulative effort tier, not a measured recovery-rate claim.\n# One rule file only. Candidate outputs can coincide or be rejected for particular words.\n" + string.Join('\n', rules) + "\n";

    private static IEnumerable<string> Numbers(int minimum, int maximum, int width) => Enumerable.Range(minimum, maximum - minimum + 1).Select(value => value.ToString("D" + width, CultureInfo.InvariantCulture));
    private static string Append(string value) => string.Concat(value.Select(character => "$" + character));
    private static string Prepend(string value) => string.Concat(value.Reverse().Select(character => "^" + character));
}
