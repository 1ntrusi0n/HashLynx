using HashLynx.UI.ViewModels;
using System.ComponentModel;
using System.Windows;

namespace HashLynx.UI;

public partial class MainWindow : Window
{
    private bool _closeApproved;
    private bool _closing;
    public MainWindow() { InitializeComponent(); Closing += OnClosing; }
    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_closeApproved || DataContext is not ShellViewModel shell) return;
        args.Cancel = true;
        if (_closing) return;
        _closing = true;
        try { if (await shell.RequestCloseAsync()) { _closeApproved = true; Close(); } }
        catch (Exception exception) { shell.Services.ReportError(exception); }
        finally { _closing = false; }
    }
}
