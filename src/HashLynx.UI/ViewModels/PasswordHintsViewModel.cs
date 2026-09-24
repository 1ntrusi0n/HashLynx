using System.IO;
using System.Text;
using System.Windows.Input;
using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;

namespace HashLynx.UI.ViewModels;

public sealed class PasswordHintsViewModel : ObservableObject
{
    private string _words = "", _prefix = "", _suffix = "", _characters = "Digits", _summary = "Describe what you remember, then preview the attempts.";
    private string _examples = "", _steps = "";
    private bool _variations, _pattern;
    private string _minimum = "8", _maximum = "10";
    private PasswordHints? _previewed;
    public string Words { get => _words; set { if (Set(ref _words, value)) Invalidate(); } }
    public string Prefix { get => _prefix; set { if (Set(ref _prefix, value)) Invalidate(); } }
    public string Suffix { get => _suffix; set { if (Set(ref _suffix, value)) Invalidate(); } }
    public string Characters { get => _characters; set { if (Set(ref _characters, value)) Invalidate(); } }
    public bool TryVariations { get => _variations; set { if (Set(ref _variations, value)) Invalidate(); } }
    public bool UsePattern { get => _pattern; set { if (Set(ref _pattern, value)) Invalidate(); } }
    public string MinimumLength { get => _minimum; set { if (Set(ref _minimum, value)) Invalidate(); } }
    public string MaximumLength { get => _maximum; set { if (Set(ref _maximum, value)) Invalidate(); } }
    public IReadOnlyList<string> CharacterChoices => PasswordHintPlanner.CharacterChoices;
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string Examples { get => _examples; private set => Set(ref _examples, value); }
    public string Steps { get => _steps; private set => Set(ref _steps, value); }
    public ICommand PreviewCommand { get; }
    public ICommand QueueCommand { get; }

    public PasswordHintsViewModel(AppServices services, Func<PasswordHintPlan, Func<bool>, Task> enqueue, Func<bool> canQueue)
    {
        PreviewCommand = new RelayCommand(_ =>
        {
            try
            {
                var request = Request(); var plan = PasswordHintPlanner.Build(request);
                Steps = string.Join(Environment.NewLine, plan.Attacks.Select((attack, index) => $"{index + 1}. {attack.Name} ({attack.Candidates:N0} candidates)"));
                Examples = string.Join(Environment.NewLine, plan.Examples);
                Summary = $"{plan.Attacks.Count} attempts; up to {plan.CandidateEstimate:N0} candidate applications. Variations can overlap."
                    + (plan.CandidateEstimate > 1_000_000_000_000L ? " This is a very large search. Narrow the length or unknown characters before running if possible." : "")
                    + " These attempts cover only the patterns you entered; recovery is not guaranteed.";
                _previewed = request; CommandManager.InvalidateRequerySuggested();
            }
            catch (ArgumentException error) { Summary = error.Message; _previewed = null; }
        });
        QueueCommand = new AsyncCommand(async _ =>
        {
            var request = Request();
            if (_previewed != request) throw new InvalidOperationException("Preview the current hints before adding their sequence.");
            await enqueue(PasswordHintPlanner.Build(request), () => _previewed == request);
        }, error => { Summary = error.Message; services.ReportError(error); }, _ => _previewed is not null && canQueue());
    }
    private PasswordHints Request()
    {
        var minimum = 1; var maximum = 1;
        if (UsePattern && (!int.TryParse(MinimumLength, out minimum) || !int.TryParse(MaximumLength, out maximum)))
            throw new ArgumentException("Enter whole numbers for the total password lengths.");
        return new(Words, TryVariations, UsePattern, Prefix, Suffix, minimum, maximum, Characters);
    }
    private void Invalidate() { _previewed = null; Steps = ""; Examples = ""; Summary = "Hints changed. Preview the attempts again."; CommandManager.InvalidateRequerySuggested(); }

    public static async Task<string?> WriteWordsAsync(PasswordHintPlan plan, string root, CancellationToken ct)
    {
        if (plan.Words.Count == 0) return null;
        var directory = Path.Combine(root, "hints"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".txt");
        try { await File.WriteAllLinesAsync(path, plan.Words, new UTF8Encoding(false), ct); }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
        return path;
    }
}
