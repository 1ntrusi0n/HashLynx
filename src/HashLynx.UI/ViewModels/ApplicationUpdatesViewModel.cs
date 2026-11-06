using HashLynx.Persistence;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using HashLynx.Updates;
using System.IO;
using System.Net.Http;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public sealed class ApplicationUpdatesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private UpdateRelease? _release;
    private StagedApplicationUpdate? _staged;
    private CancellationTokenSource? _operation;
    private bool _busy;
    private string _status = "Updates are optional. Check GitHub for a newer HashLynx release.";
    private double _progress;
    public Func<StagedApplicationUpdate, Task<bool>>? RestartRequested { get; set; }
    public bool IsBusy { get => _busy; private set { Set(ref _busy, value); CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public bool HasRelease => _release is not null;
    public string ReleaseTitle => _release is null ? "" : $"HashLynx {_release.Version} is available";
    public string ReleaseNotes => _release?.ReleaseNotes ?? "";
    public string ReleasePageUrl => _release?.ReleasePageUrl.ToString() ?? "https://github.com/1ntrusi0n/HashLynx/releases";
    public string DownloadSize => _release is null ? "" : $"Download: {_release.Package.Size / 1024d / 1024d:N1} MB";
    public bool IsReadyToInstall => _staged is not null;
    public ICommand CheckCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand CopyReleaseLinkCommand { get; }

    public ApplicationUpdatesViewModel(AppServices services)
    {
        _services = services;
        CheckCommand = new AsyncCommand(_ => CheckAsync(), ReportError, _ => !IsBusy && services.CanStartWork);
        DownloadCommand = new AsyncCommand(_ => DownloadAsync(), ReportError, _ => !IsBusy && HasRelease && !IsReadyToInstall && services.CanStartWork);
        RestartCommand = new AsyncCommand(_ => RestartAsync(), ReportError,
            _ => !IsBusy && IsReadyToInstall && !services.IsBusyForUpdate && services.CanStartWork && RestartRequested is not null);
        CancelCommand = new RelayCommand(_ => _operation?.Cancel(), _ => IsBusy && _operation is not null);
        CopyReleaseLinkCommand = new RelayCommand(_ => services.Dialogs.Copy(ReleasePageUrl));
    }

    public async Task CheckAutomaticallyAsync()
    {
        if (!_services.SettingsLoaded || !_services.Settings.CheckForUpdatesAutomatically) return;
        try { await CheckAsync(automatic: true); }
        catch (Exception exception) { ReportError(exception); }
    }

    public async Task CheckAsync(bool automatic = false)
    {
        if (IsBusy || !_services.CanStartWork) return;
        if (automatic && (!_services.SettingsLoaded || !_services.Settings.CheckForUpdatesAutomatically)) return;
        using var work = _services.BeginOperation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
        _operation = cancellation;
        IsBusy = true;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var canSaveHistory = true;
            UpdateCheckState state;
            try { state = await _services.Store.LoadUpdateCheckStateAsync(cancellation.Token); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                canSaveHistory = false;
                state = new();
                if (automatic) { Status = "Automatic checks could not read their local history. It was preserved; use Check for Updates to check manually."; return; }
            }
            if (automatic && state.LastAttemptUtc is { } previous && previous <= now && now - previous < TimeSpan.FromHours(24)) return;
            Status = "Checking GitHub for a newer release…";
            if (canSaveHistory)
            {
                state.LastAttemptUtc = now;
                try { await _services.Store.SaveUpdateCheckStateAsync(state, cancellation.Token); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    if (automatic) { Status = "Automatic checks could not save their daily check time. Use Check for Updates to check manually."; return; }
                }
            }
            var result = await _services.Updates.CheckAsync(ApplicationInfo.Version, cancellation.Token);
            _release = result.Release;
            _staged = null;
            Status = result.StatusMessage;
            RaiseRelease();
            if (automatic && HasRelease) _services.Notice = $"{ReleaseTitle}. Open Settings / About to review and download it.";
        }
        finally { _operation = null; IsBusy = false; }
    }

    public async Task DownloadAsync()
    {
        if (_release is null || IsBusy || !_services.CanStartWork) return;
        using var work = _services.BeginOperation();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
        _operation = cancellation;
        IsBusy = true;
        Progress = 0;
        Status = "Downloading and verifying the application update…";
        try
        {
            var progress = new System.Progress<UpdateProgress>(value =>
            {
                if (_operation != cancellation) return;
                Status = value.Message;
                Progress = value.TotalBytes is > 0 ? Math.Clamp(100d * value.BytesReceived / value.TotalBytes.Value, 0, 100) : 0;
            });
            _staged = await _services.Updates.DownloadAsync(_release, AppContext.BaseDirectory,
                _services.Store.Paths.UpdatesDirectory, progress, cancellation.Token);
            Progress = 100;
            Status = "Update verified. Finish or stop active work, then choose Restart and update. Hashcat and your recovery data stay in place.";
            RaiseRelease();
        }
        finally { _operation = null; IsBusy = false; }
    }

    private async Task RestartAsync()
    {
        if (_staged is null || RestartRequested is null) return;
        IsBusy = true;
        try
        {
            if (!await RestartRequested(_staged)) Status = "Finish active work before restarting to update. The verified download is ready when you are.";
        }
        finally { IsBusy = false; }
    }

    private void RaiseRelease()
    {
        Raise(nameof(HasRelease)); Raise(nameof(ReleaseTitle)); Raise(nameof(ReleaseNotes));
        Raise(nameof(ReleasePageUrl)); Raise(nameof(DownloadSize)); Raise(nameof(IsReadyToInstall));
        CommandManager.InvalidateRequerySuggested();
    }

    private void ReportError(Exception exception)
    {
        if (_services.LifetimeToken.IsCancellationRequested)
        {
            Status = "HashLynx began shutting down but could not complete the update handoff. Close and reopen the application before trying again.";
            return;
        }
        Status = exception switch
        {
            OperationCanceledException => "Update operation cancelled. Your installed application was not changed.",
            HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.TooManyRequests } => "GitHub temporarily limited update requests. Try again later; recovery is still available.",
            HttpRequestException => "GitHub could not be reached. Check your connection and try again later.",
            _ => exception.Message
        };
        if (Status.Length > 600) Status = Status[..600];
    }
}
