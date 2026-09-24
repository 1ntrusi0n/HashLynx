using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class WordlistItemViewModel(WordlistEntry entry) : ObservableObject
{
    private WordlistEntry _entry = entry;
    private bool _include;
    private string _availability = "Not checked";
    public WordlistEntry Entry => _entry;
    public string Path => _entry.Path;
    public string DisplayName => _entry.DisplayName;
    public string Size => _entry.FileSizeBytes is { } size ? size >= 1024 * 1024 ? $"{size / (1024d * 1024):N1} MiB" : $"{size:N0} bytes" : "-";
    public string Lines => _entry.LineCount is { } count ? count.ToString("N0") : "Not counted";
    public string Availability { get => _availability; private set => Set(ref _availability, value); }
    public bool Include { get => _include; set => Set(ref _include, value); }
    public override string ToString() => DisplayName;
    internal void Update(WordlistEntry value, string? availability = null)
    {
        _entry = value;
        Raise(nameof(Path)); Raise(nameof(DisplayName)); Raise(nameof(Size)); Raise(nameof(Lines));
        if (availability is not null) Availability = availability;
    }
}

public sealed class WordlistLibraryViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly WordlistTools _tools;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _operation;
    private WordlistItemViewModel? _selected;
    private bool _ready, _busy, _removeDuplicates = true, _metadataDirty;
    private string _status = "Add wordlists to keep them available for future attacks.", _friendlyName = "", _minimumLength = "0", _maximumLength = "256";
    public ObservableCollection<WordlistItemViewModel> Entries { get; } = [];
    public event Action? Changed;
    public bool IsReady => _ready;
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public WordlistItemViewModel? Selected
    {
        get => _selected;
        set { if (Set(ref _selected, value)) FriendlyName = value?.DisplayName ?? ""; }
    }
    public string FriendlyName { get => _friendlyName; set => Set(ref _friendlyName, value); }
    public string MinimumLength { get => _minimumLength; set => Set(ref _minimumLength, value); }
    public string MaximumLength { get => _maximumLength; set => Set(ref _maximumLength, value); }
    public bool RemoveDuplicates { get => _removeDuplicates; set => Set(ref _removeDuplicates, value); }
    public ICommand AddCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand RenameCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand ForgetCommand { get; }
    public ICommand TransformCommand { get; }
    public ICommand CancelCommand { get; }

    public WordlistLibraryViewModel(AppServices services)
    {
        _services = services; _tools = new(services.Store.Paths);
        AddCommand = new AsyncCommand(_ => RememberAsync(services.Dialogs.OpenFiles("Add saved wordlists", true)), ReportError, _ => _ready && !IsBusy);
        RefreshCommand = new AsyncCommand(_ => RefreshAsync(), ReportError, _ => _ready && !IsBusy);
        RenameCommand = new AsyncCommand(_ => RenameAsync(), ReportError, _ => _ready && !IsBusy && Selected is not null);
        RepairCommand = new AsyncCommand(_ => RepairAsync(), ReportError, _ => _ready && !IsBusy && Selected is not null);
        ForgetCommand = new AsyncCommand(_ => Selected is { } item ? ForgetAsync(item.Path) : Task.CompletedTask, ReportError, _ => _ready && !IsBusy && Selected is not null);
        TransformCommand = new AsyncCommand(_ => TransformAsync(), ReportError, _ => _ready && !IsBusy && Entries.Any(row => row.Include));
        CancelCommand = new RelayCommand(_ => _operation?.Cancel(), _ => IsBusy);
    }

    public async Task InitializeAsync()
    {
        try
        {
            var library = await _services.Store.LoadWordlistLibraryAsync(_services.LifetimeToken);
            foreach (var entry in library.Entries) Entries.Add(new(entry));
            _ready = true; Raise(nameof(IsReady)); Changed?.Invoke();
            _ = RefreshSafelyAsync();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            Status = "The saved library could not be loaded and has been preserved. Check wordlists.json in your data directory.";
            _services.ReportError(ex);
        }
    }

    public async Task RememberAsync(IEnumerable<string> paths)
    {
        var available = paths.Where(File.Exists).Select(System.IO.Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (available.Length == 0) return;
        await _gate.WaitAsync(_services.LifetimeToken);
        try
        {
            RequireReady();
            var entries = Entries.Select(row => row.Entry).ToList();
            foreach (var path in available)
                if (!entries.Any(entry => string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))) entries.Add(new() { Path = path });
            await SaveAsync(entries, _services.LifetimeToken);
            foreach (var entry in entries)
                if (!Entries.Any(row => string.Equals(row.Path, entry.Path, StringComparison.OrdinalIgnoreCase))) Entries.Add(new(entry));
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
        await RefreshSafelyAsync();
    }

    public async Task ForgetAsync(string path)
    {
        await _gate.WaitAsync(_services.LifetimeToken);
        try
        {
            RequireReady();
            var item = Entries.FirstOrDefault(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;
            await SaveAsync(Entries.Where(row => row != item).Select(row => row.Entry).ToList(), _services.LifetimeToken);
            Entries.Remove(item);
            if (Selected == item) Selected = null;
            Status = "Forgot the saved entry. Its file and any existing attack selection were kept.";
            Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    private async Task RenameAsync()
    {
        var item = Selected;
        if (item is null) return;
        var name = FriendlyName.Trim();
        if (name.Length is < 1 or > 200 || name.Any(char.IsControl)) throw new InvalidOperationException("Enter a friendly name of 1 to 200 characters.");
        await ChangeEntryAsync(item, item.Entry with { Name = name });
        Status = "Friendly name saved. The file name on disk is unchanged.";
    }

    private async Task RepairAsync()
    {
        var item = Selected;
        if (item is null) return;
        var path = _services.Dialogs.OpenFiles("Locate this wordlist at its new location").FirstOrDefault();
        if (path is null) return;
        if (Entries.Any(other => other != item && string.Equals(other.Path, path, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("That file is already in your library. Select its existing entry.");
        await ChangeEntryAsync(item, item.Entry with { Path = System.IO.Path.GetFullPath(path), FileSizeBytes = null, LineCount = null, LastWriteUtc = null });
        Status = "Saved location repaired. Select this entry again to use its new location in an attack.";
        await RefreshSafelyAsync();
    }

    private async Task ChangeEntryAsync(WordlistItemViewModel item, WordlistEntry replacement)
    {
        await _gate.WaitAsync(_services.LifetimeToken);
        try
        {
            RequireReady();
            await SaveAsync(Entries.Select(row => row == item ? replacement : row.Entry).ToList(), _services.LifetimeToken);
            item.Update(replacement); Changed?.Invoke();
        }
        finally { _gate.Release(); }
    }

    private async Task RefreshSafelyAsync()
    {
        try { await RefreshAsync(); }
        catch (OperationCanceledException) { Status = "Counting cancelled. Existing library entries were kept."; }
        catch (Exception ex) { ReportError(ex); }
    }

    public async Task RefreshAsync()
    {
        await _gate.WaitAsync(_services.LifetimeToken);
        var originalEntries = Entries.Select(row => row.Entry).ToArray();
        var metadataSaved = false;
        try
        {
            RequireReady();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
            _operation = operation; IsBusy = true;
            var missing = 0;
            foreach (var item in Entries)
            {
                operation.Token.ThrowIfCancellationRequested();
                try
                {
                    var file = await Task.Run(() => new FileInfo(item.Path), operation.Token);
                    // Metadata and line counts are refreshed away from the dispatcher, including slow/network files.
                    var snapshot = await Task.Run(() => (file.Exists, Size: file.Exists ? file.Length : 0, Modified: file.Exists ? file.LastWriteTimeUtc : default), operation.Token);
                    if (!snapshot.Exists) { item.Update(item.Entry with { FileSizeBytes = null, LineCount = null, LastWriteUtc = null }, "Missing - repair location"); missing++; continue; }
                    if (item.Entry.FileSizeBytes == snapshot.Size && item.Entry.LastWriteUtc == snapshot.Modified && item.Entry.LineCount is not null)
                    { item.Update(item.Entry, "Available"); continue; }
                    item.Update(item.Entry with { FileSizeBytes = snapshot.Size, LastWriteUtc = snapshot.Modified, LineCount = null }, "Counting...");
                    Status = "Counting wordlist lines in the background. You can cancel this scan.";
                    var stats = await WordlistTools.InspectAsync(item.Path, operation.Token);
                    item.Update(item.Entry with { FileSizeBytes = stats.FileSizeBytes, LineCount = stats.LineCount, LastWriteUtc = stats.LastWriteUtc }, "Available");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { item.Update(item.Entry with { LineCount = null }, "Cannot read - check location or permissions"); missing++; }
            }
            var refreshedEntries = Entries.Select(row => row.Entry).ToList();
            _metadataDirty |= !originalEntries.SequenceEqual(refreshedEntries);
            if (_metadataDirty) await SaveAsync(refreshedEntries, operation.Token);
            _metadataDirty = false; metadataSaved = true;
            Status = missing == 0 ? "Library ready. Counts are physical lines; duplicates and blank lines are included." : $"{missing} saved list(s) could not be read. Repair their location or reconnect the drive.";
            Changed?.Invoke();
        }
        finally
        {
            // Keep freshly observed counts in memory, but retry persistence if saving or scanning was interrupted.
            if (!metadataSaved) _metadataDirty |= !originalEntries.SequenceEqual(Entries.Select(row => row.Entry));
            foreach (var item in Entries.Where(row => row.Availability == "Counting...")) item.Update(item.Entry, "Not counted");
            _operation = null; IsBusy = false; _gate.Release();
        }
    }

    private async Task TransformAsync()
    {
        if (!int.TryParse(MinimumLength, out var minimum) || !int.TryParse(MaximumLength, out var maximum))
            throw new InvalidOperationException("Enter whole numbers for the minimum and maximum byte length.");
        var inputs = Entries.Where(row => row.Include).Select(row => row.Path).ToArray();
        var output = _services.Dialogs.SaveFile("Save as a NEW wordlist file", "combined-wordlist.txt");
        if (output is null) return;
        string? completedPath = null;
        await _gate.WaitAsync(_services.LifetimeToken);
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
            _operation = operation; IsBusy = true;
            var progress = new Progress<WordlistTransformProgress>(update => Status = $"{update.Stage}: {update.LinesRead:N0} input lines, {update.LinesWritten:N0} output lines.");
            var result = await _tools.TransformAsync(new(inputs, output, RemoveDuplicates, minimum, maximum), progress, operation.Token);
            completedPath = result.OutputPath;
            Status = $"Created {result.LinesWritten:N0} lines from {result.LinesRead:N0} input lines. Original files were preserved.";
        }
        catch (OperationCanceledException) { Status = "Wordlist creation cancelled. No output was published; original files were preserved."; }
        finally { _operation = null; IsBusy = false; _gate.Release(); }
        if (completedPath is not null)
        {
            var resultNotice = Status;
            await RememberAsync([completedPath]);
            Selected = Entries.FirstOrDefault(row => string.Equals(row.Path, completedPath, StringComparison.OrdinalIgnoreCase));
            Status = resultNotice + " The new list is saved in your library.";
        }
    }

    private Task SaveAsync(List<WordlistEntry> entries, CancellationToken ct) =>
        _services.Store.SaveWordlistLibraryAsync(new WordlistLibrary { Paths = entries.Select(entry => entry.Path).ToList(), Entries = entries }, ct);
    private void RequireReady()
    {
        if (!_ready) throw new InvalidOperationException("The saved library could not be loaded. Its original file has been preserved; check Diagnostics before changing it.");
    }
    private void ReportError(Exception exception)
    {
        if (exception is OperationCanceledException) { Status = "Operation cancelled."; return; }
        Status = exception is IOException or UnauthorizedAccessException ? "Check file permissions, available disk space, and that your output is a new file." : exception.Message;
        _services.ReportError(exception);
    }
}
