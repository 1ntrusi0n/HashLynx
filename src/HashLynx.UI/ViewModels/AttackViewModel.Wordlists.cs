using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed partial class AttackViewModel
{
    private readonly SemaphoreSlim _wordlistLibraryGate = new(1, 1);
    private bool _wordlistLibraryReady;
    private string? _selectedSavedWordlist;
    public ObservableCollection<string> SavedWordlists { get; } = [];
    public string WordlistLibraryNotice { get; private set; } = "";
    public ICommand ForgetWordlistCommand { get; private set; } = null!;
    public string? SelectedSavedWordlist
    {
        get => _selectedSavedWordlist;
        set
        {
            if (value is null || value == _selectedSavedWordlist || !SavedWordlists.Contains(value)) return;
            if (!Expert || (Wordlists.Count == 1 && Wordlists[0] == _starterWordlist)) Wordlists.Clear();
            if (!Wordlists.Contains(value, StringComparer.OrdinalIgnoreCase)) Wordlists.Add(value);
            Set(ref _selectedSavedWordlist, value);
            Raise(nameof(WordlistSummary));
        }
    }

    private async Task InitializeWordlistLibraryAsync()
    {
        ForgetWordlistCommand = new AsyncCommand(_ => ForgetWordlistAsync(), _services.ReportError,
            _ => _wordlistLibraryReady && SelectedSavedWordlist is not null);
        Raise(nameof(ForgetWordlistCommand));
        try
        {
            var library = await _services.Store.LoadWordlistLibraryAsync(_services.LifetimeToken);
            foreach (var path in library.Paths) SavedWordlists.Add(path);
            _wordlistLibraryReady = true;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            WordlistLibraryNotice = "Saved wordlists could not be loaded. The library was preserved. Check wordlists.json in your HashLynx data directory.";
            Raise(nameof(WordlistLibraryNotice));
            _services.ReportError(ex);
        }
    }

    private void SyncSavedWordlistSelection()
    {
        var selected = Wordlists.Count == 1 ? SavedWordlists.FirstOrDefault(path => string.Equals(path, Wordlists[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (_selectedSavedWordlist != selected) { _selectedSavedWordlist = selected; Raise(nameof(SelectedSavedWordlist)); }
    }

    private async Task RememberWordlistsAsync(IEnumerable<string> paths)
    {
        var available = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length == 0) return;
        await _wordlistLibraryGate.WaitAsync(_services.LifetimeToken);
        try
        {
            if (!_wordlistLibraryReady) throw new InvalidOperationException("The saved wordlist library could not be loaded. Its original file has been preserved; check Diagnostics before changing it.");
            var updated = SavedWordlists.Concat(available).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await _services.Store.SaveWordlistLibraryAsync(new WordlistLibrary { Paths = updated }, _services.LifetimeToken);
            foreach (var path in updated) if (!SavedWordlists.Contains(path, StringComparer.OrdinalIgnoreCase)) SavedWordlists.Add(path);
        }
        finally { _wordlistLibraryGate.Release(); }
    }

    private async Task AddWordlistsAsync(IEnumerable<string> paths)
    {
        var available = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await RememberWordlistsAsync(available);
        SelectWordlists(available);
        SyncSavedWordlistSelection();
    }

    private async Task ForgetWordlistAsync()
    {
        var selected = SelectedSavedWordlist;
        if (selected is null) return;
        await _wordlistLibraryGate.WaitAsync(_services.LifetimeToken);
        try
        {
            var updated = SavedWordlists.Where(path => !string.Equals(path, selected, StringComparison.OrdinalIgnoreCase)).ToList();
            await _services.Store.SaveWordlistLibraryAsync(new WordlistLibrary { Paths = updated }, _services.LifetimeToken);
            SavedWordlists.Remove(selected);
            SyncSavedWordlistSelection();
            _services.Notice = "Wordlist forgotten. Its file and current attack selection were kept.";
        }
        finally { _wordlistLibraryGate.Release(); }
    }

    private async Task BrowseCombinatorWordlistAsync(bool left)
    {
        var path = _services.Dialogs.OpenFiles(left ? "Left wordlist" : "Right wordlist").FirstOrDefault();
        if (path is null) return;
        await RememberWordlistsAsync([path]);
        if (left) { LeftWordlist = path; Raise(nameof(LeftWordlist)); }
        else { RightWordlist = path; Raise(nameof(RightWordlist)); }
    }
}
