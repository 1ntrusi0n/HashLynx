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
            string? wordPath = null;
            try
            {
                PasswordHintPlanner.ConfigureOptions(BuildDraft("").Options, plan.Words.Count > 0);
                wordPath = await PasswordHintsViewModel.WriteWordsAsync(plan, _services.Store.Paths.Root, _services.LifetimeToken);
                var attacks = plan.Attacks.Select(item => (item.Name, Attack: CloneAttack(item.Attack))).ToArray();
                foreach (var item in attacks.Where(item => item.Attack.Kind == AttackFamilies.Dictionary)) item.Attack.Wordlists = [wordPath!];
                var job = await PrepareAsync(attacks[0].Attack);
                if (job is null) throw new InvalidOperationException(StartFeedback);
                if (!stillCurrent()) throw new InvalidOperationException("Hints changed during validation. Preview and queue the updated hints again.");
                PasswordHintPlanner.ConfigureOptions(job.Options, plan.Words.Count > 0);
                await queue.AddAsync(job, attacks, stillCurrent: stillCurrent);
                wordPath = null; ResetDraft(); showQueue();
            }
            finally { if (wordPath is not null && System.IO.File.Exists(wordPath)) System.IO.File.Delete(wordPath); }
        }, () => queue.CanEdit);
        AddToQueueCommand = new AsyncCommand(async _ =>
        {
            var job = await PrepareAsync(); if (job is null) return;
            await queue.AddAsync(job, [("Custom attempt", job.Attack)], AppendToSelectedSequence);
            ResetDraft(); showQueue();
        }, ReportAttackError, _ => queue.CanEdit);
        AddProgressiveSequenceCommand = new AsyncCommand(async _ =>
        {
            if (!IsDictionary) return;
            var attack = BuildDraft("").Attack; attack.RuleFiles = []; attack.RulePresetId = null; attack.Loopback = false; attack.LeftRule = null; attack.RightRule = null;
            var job = await PrepareAsync(attack); if (job is null) return;
            var quick = CloneAttack(attack); quick.RulePresetId = RulePresetCatalog.QuickId;
            var normal = CloneAttack(attack); normal.RulePresetId = RulePresetCatalog.NormalId;
            await queue.AddAsync(job, [("Wordlist unchanged", attack), ("Wordlist + Quick rules", quick), ("Wordlist + Normal rules", normal)]);
            ResetDraft(); showQueue();
        }, ReportAttackError, _ => queue.CanEdit && IsDictionary);
        Raise(nameof(Hints)); Raise(nameof(AddToQueueCommand)); Raise(nameof(AddProgressiveSequenceCommand));
    }
    private void ResetDraft() { _draftId = Guid.NewGuid(); Session = "hashlynx-" + _draftId.ToString("N")[..10]; }
    private static AttackConfiguration CloneAttack(AttackConfiguration attack) => JsonSerializer.Deserialize<AttackConfiguration>(JsonSerializer.Serialize(attack))!;
}
