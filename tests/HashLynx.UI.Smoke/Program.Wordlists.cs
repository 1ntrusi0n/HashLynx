using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.ViewModels;

namespace HashLynx.UI.Smoke;

internal sealed partial class SmokeApplication
{
    private async Task CheckWordlistManagerAsync(ShellViewModel shell, PersistenceStore store, Window window)
    {
        var attack = shell.Attack;
        var manager = attack.WordlistLibrary;
        var source = Path.Combine(store.Paths.Root, "manager-synthetic-" + Guid.NewGuid().ToString("N") + ".txt");
        const string original = "synthetic-first\r\n\r\nsynthetic-last";
        await File.WriteAllTextAsync(source, original, new UTF8Encoding(false));
        attack.Family = 0; attack.Expert = false;
        await ((AsyncCommand)attack.DropWordlistsCommand).ExecuteAsync(new[] { source });
        var entry = manager.Entries.Single(item => item.Path == source);
        Require(entry.Entry.LineCount == 3 && entry.Entry.FileSizeBytes == Encoding.UTF8.GetByteCount(original), "Background counting must include blank and unterminated lines without changing their contents.");
        Require(entry.Availability == "Available" && !manager.IsBusy, "Completed background counts must leave the library usable.");

        shell.Selected = shell.Navigation.Single(page => page.Page == attack);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var expander = Find<Expander>(window, "WordlistManagerExpander");
        expander.IsExpanded = true;
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        var grid = Find<DataGrid>(window, "WordlistLibraryGrid");
        grid.SelectedItem = entry; grid.ScrollIntoView(entry);
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(manager.Selected == entry, "Selecting a manager row must select its library entry.");
        var row = grid.ItemContainerGenerator.ContainerFromItem(entry) as DataGridRow
            ?? throw new InvalidOperationException("The selected wordlist row was not realized.");
        var include = Find<CheckBox>(row, "IncludeWordlistCheckBox");
        Require(include.IsEnabled && !entry.Include, "The template checkbox must be usable inside the read-only grid.");
        include.IsChecked = true;
        Require(entry.Include && manager.TransformCommand.CanExecute(null), "Checking a real library row must enable creation of a new list.");
        include.IsChecked = false;
        Require(!entry.Include, "Unchecking the library row must update transformation selection.");

        var name = Find<TextBox>(window, "WordlistFriendlyName");
        name.Text = "Synthetic friendly list";
        var rename = (AsyncCommand)Find<Button>(window, "SaveWordlistNameButton").Command;
        await rename.ExecuteAsync();
        Require(entry.DisplayName == "Synthetic friendly list", "A friendly rename must update the same picker entry.");
        Require((await store.LoadWordlistLibraryAsync()).Entries.Single(item => item.Path == source).Name == "Synthetic friendly list", "The friendly label must survive a library read.");
        Require(attack.SelectedSavedWordlistEntry == entry && attack.SelectedSavedWordlist == source, "Renaming must preserve the selected attack path.");
        Require(File.Exists(source) && await File.ReadAllTextAsync(source) == original, "Friendly labels must not rename or modify the source file.");

        // Failed metadata saves must be retried even when the in-memory line count is already current.
        var libraryPath = Path.Combine(store.Paths.Root, "wordlists.json");
        var backup = libraryPath + ".manager-smoke-backup";
        File.Move(libraryPath, backup);
        Directory.CreateDirectory(libraryPath);
        try
        {
            name.Text = "Must not be saved";
            await rename.ExecuteAsync();
            Require(entry.DisplayName == "Synthetic friendly list", "A failed rename must retain the saved label in memory.");
            await File.AppendAllTextAsync(source, "\nsynthetic-added", new UTF8Encoding(false));
            var failed = false;
            try { await manager.RefreshAsync(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
            Require(failed && !manager.IsBusy, "A failed background metadata save must end its busy state and report failure.");
        }
        finally { Directory.Delete(libraryPath); File.Move(backup, libraryPath); }
        await manager.RefreshAsync();
        var saved = (await store.LoadWordlistLibraryAsync()).Entries.Single(item => item.Path == source);
        Require(saved.LineCount == 4 && saved.Name == "Synthetic friendly list", "A retry must persist fresh counts without losing the saved friendly label.");
        name.Text = entry.DisplayName;

        var moved = source + ".moved";
        File.Move(source, moved);
        await manager.RefreshAsync();
        Require(entry.Availability.Contains("Missing", StringComparison.Ordinal) && entry.Entry.LineCount is null, "Refresh must expose a missing file and invalidate its cached count.");
        Require((await store.LoadWordlistLibraryAsync()).Paths.Contains(source), "Missing entries must remain available for path repair.");
        File.Move(moved, source);
        await manager.RefreshAsync();
        Require(entry.Availability == "Available" && entry.Entry.LineCount == 4, "A reconnected list must become usable and receive a fresh count.");

        var scroll = Find<ScrollViewer>(window, "AttackScrollViewer");
        window.UpdateLayout();
        scroll.ScrollToVerticalOffset(scroll.VerticalOffset + expander.TranslatePoint(new Point(0, 0), scroll).Y - 30);
        await RenderAsync(window, "Wordlist-library-manager");
        Require(scroll.ScrollableHeight > 0, "Expanding the library manager must retain page scrolling.");
        expander.IsExpanded = false;
        await manager.ForgetAsync(source);
        await ((AsyncCommand)attack.UseStarterCommand).ExecuteAsync();
        scroll.ScrollToTop();
    }
}
