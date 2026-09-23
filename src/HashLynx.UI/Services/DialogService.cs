using Microsoft.Win32;
using System.Windows;

namespace HashLynx.UI.Services;

public sealed class DialogService
{
    public string[] OpenFiles(string title, bool multiple = false, string filter = "All files|*.*", string? initialDirectory = null)
    {
        var dialog = new OpenFileDialog { Title = title, Multiselect = multiple, Filter = filter };
        if (System.IO.Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }
    public string? OpenFolder(string title, string? initialDirectory = null)
    {
        var dialog = new OpenFolderDialog { Title = title };
        if (System.IO.Directory.Exists(initialDirectory)) dialog.InitialDirectory = initialDirectory;
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
    public string? SaveFile(string title, string fileName, string filter = "Text files|*.txt|All files|*.*")
    {
        var dialog = new SaveFileDialog { Title = title, FileName = fileName, Filter = filter };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
    public bool Confirm(string message, string title) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    public void Copy(string value) => Clipboard.SetText(value);
}
