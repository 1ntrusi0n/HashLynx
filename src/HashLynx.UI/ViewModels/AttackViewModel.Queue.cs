using System.Text.Json;
using System.Windows.Input;
using HashLynx.Core;
using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;

namespace HashLynx.UI.ViewModels;

public sealed partial class AttackViewModel
{
    public PasswordHintsViewModel Hints { get; private set; } = null!;
    public ICommand AddToQueueCommand { get; private set; } = null!;
    public ICommand AddProgressiveSequenceCommand { get; private set; } = null!;
    public bool AppendToSelectedSequence { get; set; }
    public void ConnectQueue(RecoveryQueueViewModel queue, Action showQueue)
    {
        Hints = new(_services, async (plan, stillCurrent) =>
        {
            using var operation = _services.BeginOperation();
            if (!Expert) throw new InvalidOperationException("Enable Expert mode to create a recovery sequence.");
            string? wordPath = null;
            string? pcfgHintPath = null;
            try
            {
                var includePcfg = Hints.IncludePcfgAttempt;
                PasswordHintPlanner.ConfigureOptions(BuildDraft("").Options, plan.Words.Count > 0);
                wordPath = await PasswordHintsViewModel.WriteWordsAsync(plan, _services.Store.Paths.Root, _services.LifetimeToken);
                var attacks = plan.Attacks.Select(item => (item.Name, Attack: CloneAttack(item.Attack))).ToList();
                foreach (var item in attacks.Where(item => item.Attack.Kind == AttackFamilies.Dictionary)) item.Attack.Wordlists = [wordPath!];
                if (includePcfg)
                {
                    if (plan.Words.Count > 0) pcfgHintPath = await HashcatPcfgHints.WriteAsync(_services.Store.Paths.Root, string.Join(Environment.NewLine, plan.Words), _services.LifetimeToken);
                    attacks.Add(("Likely passwords (PCFG)", new() { Kind = AttackFamilies.Pcfg, Pcfg = new() { UseHints = pcfgHintPath is not null, HintFile = pcfgHintPath } }));
                }
                var job = await PrepareAsync(attacks[0].Attack);
                if (job is null) throw new InvalidOperationException(StartFeedback);
                if (!Expert || !stillCurrent()) throw new InvalidOperationException("Hints or experience mode changed during validation. Preview and queue the updated hints again.");
                PasswordHintPlanner.ConfigureOptions(job.Options, plan.Words.Count > 0);
                await queue.AddAsync(job, attacks, stillCurrent: () => Expert && stillCurrent());
                wordPath = null; pcfgHintPath = null; ResetDraft(); showQueue();
            }
            finally
            {
                if (wordPath is not null && System.IO.File.Exists(wordPath)) System.IO.File.Delete(wordPath);
                if (pcfgHintPath is not null && System.IO.File.Exists(pcfgHintPath)) System.IO.File.Delete(pcfgHintPath);
            }
        }, () => Expert && queue.CanEdit);
        AddToQueueCommand = new AsyncCommand(async _ =>
        {
            using var operation = _services.BeginOperation();
            var job = await PrepareAsync(); if (job is null) return;
            if (!Expert) throw new InvalidOperationException("Enable Expert mode to create a recovery sequence.");
            await queue.AddAsync(job, [("Custom attempt", job.Attack)], AppendToSelectedSequence, () => Expert);
            ResetDraft(); showQueue();
        }, ReportAttackError, _ => Expert && queue.CanEdit && _services.CanStartWork);
        AddProgressiveSequenceCommand = new AsyncCommand(async _ =>
        {
            using var operation = _services.BeginOperation();
            if (!IsDictionary) return;
            var attack = BuildDraft("").Attack; attack.RuleFiles = []; attack.RulePresetId = null; attack.RulesConcatenated = false; attack.Loopback = false; attack.LeftRule = null; attack.RightRule = null;
            var job = await PrepareAsync(attack); if (job is null) return;
            var quick = CloneAttack(attack); quick.RulePresetId = RulePresetCatalog.QuickId;
            var normal = CloneAttack(attack); normal.RulePresetId = RulePresetCatalog.NormalId;
            if (!Expert) throw new InvalidOperationException("Enable Expert mode to create a recovery sequence.");
            await queue.AddAsync(job, [("Wordlist unchanged", attack), ("Wordlist + Quick rules", quick), ("Wordlist + Normal rules", normal)], stillCurrent: () => Expert);
            ResetDraft(); showQueue();
        }, ReportAttackError, _ => Expert && queue.CanEdit && IsDictionary && _services.CanStartWork);
        Raise(nameof(Hints)); Raise(nameof(AddToQueueCommand)); Raise(nameof(AddProgressiveSequenceCommand));
    }
    private void ResetDraft() { _draftId = Guid.NewGuid(); Session = "hashlynx-" + _draftId.ToString("N")[..10]; }
    private static AttackConfiguration CloneAttack(AttackConfiguration attack) => JsonSerializer.Deserialize<AttackConfiguration>(JsonSerializer.Serialize(attack))!;
}
