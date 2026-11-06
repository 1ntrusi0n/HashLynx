namespace HashLynx.Core;

/// <summary>Keeps physical source lines separate from amplified password-check work.</summary>
public sealed record JobWorkSummary(bool UsesWordlists, decimal? TotalLines, long? CurrentLines,
    int? CurrentWordlistIndex, double? PassPercent, double? OverallPercent, bool WholeAttempt,
    string ScopeNote)
{
    public static JobWorkSummary For(JobRecord record)
    {
        var attack = record.Configuration.Attack;
        var lists = attack.Wordlists;
        var usesLists = lists.Count > 0 && attack.Kind is AttackFamilies.Dictionary or AttackFamilies.Combinator
            or AttackFamilies.HybridMaskWordlist or AttackFamilies.HybridWordlistMask or AttackFamilies.WordlistPattern;
        var status = record.LatestStatus;
        double? percent = status is { ProgressTotal: > 0, Progress: >= 0 }
            ? (double)Math.Clamp(100m * status.Progress / status.ProgressTotal, 0m, 100m)
            : status is { ProgressPercent: > 0 and <= 100 } ? status.ProgressPercent : null;
        var counts = record.WordlistStatistics;
        var matchingCounts = usesLists && counts is not null && counts.Count == lists.Count && counts.All(item => item is not null)
            && counts.Select(item => item.Path).SequenceEqual(lists, StringComparer.OrdinalIgnoreCase);
        decimal? total = matchingCounts && counts!.All(item => item.LineCount is >= 0)
            ? counts!.Sum(item => (decimal)item.LineCount!.Value) : null;
        var validScope = ValidQueue(status?.SourceCount, status?.SourceIndex) && ValidQueue(status?.ModifierCount, status?.ModifierIndex);
        int? current = usesLists && lists.Count == 1 && validScope ? 0 : null;
        var simpleDictionary = validScope && attack.Kind == AttackFamilies.Dictionary && !attack.Loopback
            && record.Configuration.Options.ExtraArguments.Count == 0 && status?.ModifierCount is not > 1;
        if (simpleDictionary && status?.SourceCount == lists.Count && status.SourceIndex is > 0
            && status.SourceIndex <= lists.Count)
            current = (int)status.SourceIndex.Value - 1;
        var singlePass = validScope && !attack.Loopback && !attack.Increment && string.IsNullOrWhiteSpace(attack.MaskFile)
            && attack.MaskPresetId is null && record.Configuration.Options.ExtraArguments.Count == 0
            && status?.SourceCount is not > 1 && status?.ModifierCount is not > 1
            && (attack.Kind != AttackFamilies.Dictionary || lists.Count <= 1);
        double? overall = singlePass ? percent : null;
        if (simpleDictionary && current is { } index && total is > 0 && percent is { } fraction)
        {
            var before = counts!.Take(index).Sum(item => (decimal)item.LineCount!.Value);
            overall = (double)Math.Clamp((before + counts![index].LineCount!.Value * (decimal)fraction / 100m) / total.Value * 100m, 0m, 100m);
        }
        var note = singlePass ? "Progress includes rule variations and rejected guesses. Finding the password can end the attempt early."
            : "Time estimates cover the current pass only. A new wordlist or pattern starts a new estimate.";
        if (!singlePass && overall is not null)
            note += " Overall wordlist progress is approximate, weighted by each file's line count.";
        return new(usesLists, total, matchingCounts && current is { } selected && counts![selected].LineCount is >= 0 ? counts[selected].LineCount : null,
            current, percent, overall, singlePass, note);
    }

    private static bool ValidQueue(long? count, long? index) => count is null && index is null
        || count is > 0 && index is > 0 && index <= count;
}
