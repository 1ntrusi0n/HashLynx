using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Hashcat;

public sealed record RulePreset(string Id, string DisplayName, string Description, int RuleCount, string Coverage, string RuleFilePath)
{
    public string CandidateCountLabel => $"Up to {RuleCount:N0} candidates per input word";
    public override string ToString() => DisplayName;
}

/// <summary>Versioned embedded presets are materialized under user-owned application data, independently of the Hashcat installation.</summary>
public sealed class RulePresetCatalog
{
    public const string QuickId = "quick-v1";
    public const string NormalId = "normal-v1";
    public const string HeavyId = "heavy-v1";
    public const string SuperId = "super-v1";
    private readonly string _managedDirectory;
    public IReadOnlyList<RulePreset> Presets { get; }

    public RulePresetCatalog(string? managedDirectory = null)
    {
        _managedDirectory = managedDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx", "rule-presets");
        RulePreset Preset(string id, string name, string description, int count, string coverage) => new(id, name, description, count, coverage, Path.Combine(_managedDirectory, id + ".rule"));
        Presets = new[]
        {
            Preset(QuickId, "Quick", "A small first pass for common case changes and short endings.", 64, "Original word, case changes, reverse/duplicate, single digits, symbols, selected short numbers and single substitutions."),
            Preset(NormalId, "Normal", "A balanced starting point with broader numeric and year variations.", 512, "Everything in Quick, two-digit endings, years 1980–2035, single-digit prefixes and extra case/substitution combinations."),
            Preset(HeavyId, "Heavy", "More combinations for a longer dictionary pass.", 4096, "Everything in Normal, three-digit endings, years 1900–1979, two-digit prefixes, number/symbol combinations and selected position changes."),
            Preset(SuperId, "Super", "The largest built-in budget; best used with a focused wordlist.", 16384, "Everything in Heavy, all four-digit endings, three-digit and year prefixes, paired substitutions and additional numeric case variants.")
        };
    }

    public RulePreset GetById(string id) => Presets.FirstOrDefault(preset => preset.Id == id) ?? throw new ArgumentException("This rule preset is unavailable. Select a built-in preset again or use custom rule files.", nameof(id));

    public IReadOnlyList<string> GetRules(string id)
    {
        var contents = Encoding.UTF8.GetString(ReadAsset(GetById(id).Id));
        return contents.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(line => !line.StartsWith('#')).Select(line => line.TrimEnd('\r')).ToArray();
    }

    public string GetRuleFilePath(string id)
    {
        var preset = GetById(id);
        var bytes = ReadAsset(preset.Id);
        if (File.Exists(preset.RuleFilePath) && new FileInfo(preset.RuleFilePath).Length == bytes.Length && SHA256.HashData(File.ReadAllBytes(preset.RuleFilePath)).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return preset.RuleFilePath;
        Directory.CreateDirectory(_managedDirectory);
        var temporary = preset.RuleFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, preset.RuleFilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return preset.RuleFilePath;
    }

    private static byte[] ReadAsset(string id)
    {
        using var stream = typeof(RulePresetCatalog).Assembly.GetManifestResourceStream($"HashLynx.Hashcat.RulePresets.{id}.rule")
            ?? throw new InvalidOperationException("The built-in rule preset asset is missing from this application build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
