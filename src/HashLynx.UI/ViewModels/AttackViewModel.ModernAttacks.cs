using HashLynx.Core;

namespace HashLynx.UI.ViewModels;

public sealed partial class AttackViewModel
{
    private string _wordlistPattern = "?w?d?d", _pcfgHintWords = "", _pcfgMinimumLength = "", _pcfgMaximumLength = "", _pcfgShape = "";
    private string? _preparedPcfgHintFile;
    private bool _pcfgUseHints, _rulesConcatenated;
    private int _pcfgConstraint;
    public bool IsPcfg => Family == 4;
    public bool IsWordlistPattern => Family == 5;
    public bool ShowWordlistLibrary => UsesWordlists || IsCombinator;
    public bool PcfgSupported => _services.Installation?.Capabilities.AttackModes.ContainsKey(AttackFamilies.Pcfg) == true;
    public bool WordlistPatternSupported => _services.Installation?.Capabilities.AttackModes.ContainsKey(AttackFamilies.WordlistPattern) == true;
    public bool ShowPcfgMethod => _services.Installation is null || PcfgSupported;
    public bool ShowWordlistPatternMethod => Expert && (_services.Installation is null || WordlistPatternSupported);
    public bool RulesAvailable => IsDictionary || Expert && _services.Installation?.Capabilities.ExpandedRules == true;
    public bool CanConcatenateRules => Expert && _services.Installation?.Capabilities.RulesConcatenation == true;
    public bool ShowLoopback => Expert && IsDictionary;
    public bool WorkloadSupported => _services.Installation?.Capabilities.WorkloadProfilesEffective != false;
    public string WorkloadGuidance => WorkloadSupported
        ? "Higher workload can make the computer less responsive. Start with the default."
        : "Hashcat 8 manages workload automatically. Saved workload preferences remain available for older backends.";
    public string MethodDescription => Family switch
    {
        0 => "Try words you choose, with optional changes such as capitals or added digits. A focused wordlist is a good first attempt.",
        1 => "Try combinations that match a remembered password structure. Each extra unknown character can multiply the time needed.",
        2 => "Attach a character pattern to the beginning or end of each word. For example, a word plus two digits tries river00 through river99.",
        3 => "Join every word in the left list with every word in the right list. A list of 100 words and a list of 200 words creates 20,000 pairs.",
        4 => "Let Hashcat try likely password structures in probability order using its included model. No wordlist is needed. This is a statistical guesser, not a guarantee of recovery.",
        5 => "Place whole words inside a pattern using ?w for the first list and ?q for the second. Select one or two lists in that order.",
        _ => "Choose a recovery method."
    };
    public string MethodAvailability => IsPcfg && !PcfgSupported ? "This method needs a complete Hashcat 8 release. Select and validate that release in Settings."
        : IsWordlistPattern && !WordlistPatternSupported ? "Wordlist patterns need Hashcat 8. Select and validate that release in Settings." : "";
    public string WordlistPattern { get => _wordlistPattern; set => Set(ref _wordlistPattern, value); }
    public bool RulesConcatenated { get => _rulesConcatenated; set => Set(ref _rulesConcatenated, value); }
    public bool PcfgUseHints { get => _pcfgUseHints; set { if (Set(ref _pcfgUseHints, value)) _preparedPcfgHintFile = null; } }
    public string PcfgHintWords { get => _pcfgHintWords; set { if (Set(ref _pcfgHintWords, value)) _preparedPcfgHintFile = null; } }
    public int PcfgConstraint { get => _pcfgConstraint; set { if (Set(ref _pcfgConstraint, value)) { Raise(nameof(PcfgUsesLength)); Raise(nameof(PcfgUsesShape)); } } }
    public IReadOnlyList<string> PcfgConstraints { get; } = ["No extra restriction", "Limit password length", "Match a character shape"];
    public bool PcfgUsesLength => PcfgConstraint == 1;
    public bool PcfgUsesShape => PcfgConstraint == 2;
    public string PcfgMinimumLength { get => _pcfgMinimumLength; set => Set(ref _pcfgMinimumLength, value); }
    public string PcfgMaximumLength { get => _pcfgMaximumLength; set => Set(ref _pcfgMaximumLength, value); }
    public string PcfgShape { get => _pcfgShape; set => Set(ref _pcfgShape, value); }

    private PcfgConfiguration BuildPcfgConfiguration() => new()
    {
        UseHints = Expert && PcfgUseHints,
        HintFile = Expert && PcfgUseHints ? _preparedPcfgHintFile : null,
        MinimumLength = Expert && PcfgUsesLength ? OptionalLength(PcfgMinimumLength) : null,
        MaximumLength = Expert && PcfgUsesLength ? OptionalLength(PcfgMaximumLength) : null,
        ShapeMask = Expert && PcfgUsesShape ? PcfgShape : null
    };

    private static int? OptionalLength(string text) => string.IsNullOrWhiteSpace(text) ? null
        : int.TryParse(text, out var value) && value > 0 ? value
        : throw new InvalidOperationException("PCFG lengths must be positive whole numbers, or blank for no limit.");

    private void RaiseFamilyVisibility()
    {
        Raise(nameof(UsesWordlists)); Raise(nameof(UsesMask)); Raise(nameof(IsHybrid)); Raise(nameof(IsCombinator)); Raise(nameof(ShowWordlistLibrary));
        Raise(nameof(IsDictionary)); Raise(nameof(IsPcfg)); Raise(nameof(IsWordlistPattern)); Raise(nameof(UsesSingleRules)); Raise(nameof(ShowLoopback));
        Raise(nameof(PcfgSupported)); Raise(nameof(WordlistPatternSupported)); Raise(nameof(ShowPcfgMethod)); Raise(nameof(ShowWordlistPatternMethod)); Raise(nameof(WorkloadSupported));
        Raise(nameof(WorkloadGuidance)); Raise(nameof(MethodDescription)); Raise(nameof(MethodAvailability));
        RaiseRuleVisibility(); RaiseMaskVisibility();
    }
}
