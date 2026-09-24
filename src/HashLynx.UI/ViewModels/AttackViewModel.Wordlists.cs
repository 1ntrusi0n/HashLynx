using HashLynx.UI.Infrastructure;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed partial class AttackViewModel
{
    private WordlistLibraryViewModel? _wordlistLibrary;
    private string? _selectedSavedWordlist;
    public WordlistLibraryViewModel WordlistLibrary => _wordlistLibrary ??= new(_services);
    public ObservableCollection<string> SavedWordlists { get; } = [];
    public ObservableCollection<WordlistItemViewModel> SavedWordlistEntries => WordlistLibrary.Entries;
    public string WordlistLibraryNotice => WordlistLibrary.IsReady ? "" : WordlistLibrary.Status;
    public ICommand ForgetWordlistCommand { get; private set; } = null!;
    public WordlistItemViewModel? SelectedSavedWordlistEntry
    {
        get => SavedWordlistEntries.FirstOrDefault(entry => string.Equals(entry.Path, SelectedSavedWordlist, StringComparison.OrdinalIgnoreCase));
        set { if (value is not null) SelectedSavedWordlist = value.Path; }
    }
    public string? SelectedSavedWordlist
    {
        get => _selectedSavedWordlist;
        set
        {
            if (value is null || value == _selectedSavedWordlist || !SavedWordlists.Contains(value)) return;
            if (!Expert || (Wordlists.Count == 1 && Wordlists[0] == _starterWordlist)) Wordlists.Clear();
            if (!Wordlists.Contains(value, StringComparer.OrdinalIgnoreCase)) Wordlists.Add(value);
            Set(ref _selectedSavedWordlist, value);
            Raise(nameof(SelectedSavedWordlistEntry));
            Raise(nameof(WordlistSummary));
        }
    }

    private async Task InitializeWordlistLibraryAsync()
    {
        ForgetWordlistCommand = new AsyncCommand(_ => ForgetWordlistAsync(), _services.ReportError,
            _ => WordlistLibrary.IsReady && !WordlistLibrary.IsBusy && SelectedSavedWordlist is not null);
        Raise(nameof(ForgetWordlistCommand));
        WordlistLibrary.Changed += SynchronizeSavedWordlists;
        WordlistLibrary.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(WordlistLibrary.Status) or nameof(WordlistLibrary.IsReady)) Raise(nameof(WordlistLibraryNotice)); };
        await WordlistLibrary.InitializeAsync();
        SynchronizeSavedWordlists();
    }

    private void SynchronizeSavedWordlists()
    {
        var paths = SavedWordlistEntries.Select(entry => entry.Path).ToArray();
        foreach (var path in SavedWordlists.Where(path => !paths.Contains(path, StringComparer.OrdinalIgnoreCase)).ToArray()) SavedWordlists.Remove(path);
        foreach (var path in paths) if (!SavedWordlists.Contains(path, StringComparer.OrdinalIgnoreCase)) SavedWordlists.Add(path);
        SyncSavedWordlistSelection();
        Raise(nameof(SelectedSavedWordlistEntry)); Raise(nameof(WordlistLibraryNotice));
    }

    private void SyncSavedWordlistSelection()
    {
        var selected = Wordlists.Count == 1 ? SavedWordlists.FirstOrDefault(path => string.Equals(path, Wordlists[0], StringComparison.OrdinalIgnoreCase)) : null;
        if (_selectedSavedWordlist != selected)
        {
            _selectedSavedWordlist = selected;
            Raise(nameof(SelectedSavedWordlist)); Raise(nameof(SelectedSavedWordlistEntry));
        }
    }

    private Task RememberWordlistsAsync(IEnumerable<string> paths) => WordlistLibrary.RememberAsync(paths);

    private async Task AddWordlistsAsync(IEnumerable<string> paths)
    {
        var available = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        await RememberWordlistsAsync(available);
        SelectWordlists(available);
        SyncSavedWordlistSelection();
    }

    private async Task ForgetWordlistAsync()
    {
        if (SelectedSavedWordlist is not { } selected) return;
        await WordlistLibrary.ForgetAsync(selected);
        _services.Notice = "Wordlist forgotten. Its file and current attack selection were kept.";
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
