using HashLynx.Hashcat;
using HashLynx.UI.Infrastructure;
using HashLynx.UI.Services;
using System.Windows.Input;

namespace HashLynx.UI.ViewModels;

public enum ReadinessState { Pending, Ready, NeedsAttention }

public sealed partial class AttackViewModel
{
    private static readonly HashSet<string> DraftInputProperties =
    [nameof(Family), nameof(HybridDirection), nameof(Expert), nameof(SelectedRulePreset), nameof(UseCustomRules),
     nameof(SelectedMaskSource), nameof(Mask), nameof(MaskFile), nameof(Charset1), nameof(Charset2), nameof(Charset3), nameof(Charset4),
     nameof(Increment), nameof(IncrementMinimum), nameof(IncrementMaximum), nameof(LeftWordlist), nameof(RightWordlist),
     nameof(LeftRule), nameof(RightRule), nameof(Loopback), nameof(Devices), nameof(OptimizedKernel), nameof(Workload),
     nameof(ExtraArguments), nameof(DisablePotfile), nameof(PotfilePath), nameof(OutputPath), nameof(Temperature), nameof(Session)];
    private CancellationTokenSource? _readinessCancellation;
    private string _targetReadiness = "Add a target to begin.", _inputsReadiness = "Choose a wordlist or mask.", _deviceReadiness = "Set up Hashcat in Settings.";
    private string _readinessSummary = "Complete the items below. Start recovery runs the final checks.";
    private bool _targetReady, _inputsReady, _deviceReady;
    private ReadinessState _targetReadinessState = ReadinessState.NeedsAttention, _deviceReadinessState = ReadinessState.NeedsAttention;
    public ReadinessState TargetReadinessState => _targetReadinessState;
    public ReadinessState InputsReadinessState => InputsReady ? ReadinessState.Ready : ReadinessState.NeedsAttention;
    public ReadinessState DeviceReadinessState => _deviceReadinessState;
    public ReadinessState OverallReadinessState => _services.IsComputeBusy ? ReadinessState.Pending
        : new[] { TargetReadinessState, InputsReadinessState, DeviceReadinessState }.Contains(ReadinessState.NeedsAttention) ? ReadinessState.NeedsAttention
        : TargetReady && InputsReady && DeviceReady ? ReadinessState.Ready : ReadinessState.Pending;
    public string TargetReadiness { get => _targetReadiness; private set => Set(ref _targetReadiness, value); }
    public string InputsReadiness { get => _inputsReadiness; private set => Set(ref _inputsReadiness, value); }
    public string DeviceReadiness { get => _deviceReadiness; private set => Set(ref _deviceReadiness, value); }
    public string ReadinessSummary { get => _readinessSummary; private set => Set(ref _readinessSummary, value); }
    public bool TargetReady { get => _targetReady; private set => Set(ref _targetReady, value); }
    public bool InputsReady { get => _inputsReady; private set => Set(ref _inputsReady, value); }
    public bool DeviceReady { get => _deviceReady; private set => Set(ref _deviceReady, value); }
    public string TargetActionLabel => Inspector.SelectedMode is null && Inspector.Matches.Count > 1 ? "Choose hash type" : "Review target";
    public string DeviceActionLabel => _services.Installation is null ? "Set up Hashcat" : "Check hardware";
    public ICommand ReviewTargetCommand { get; private set; } = null!;
    public ICommand ReviewInputsCommand { get; private set; } = null!;
    public ICommand ReviewDeviceCommand { get; private set; } = null!;
    public ICommand RefreshReadinessCommand { get; private set; } = null!;
    public event Action<string>? NavigationRequested;
    public event Action<string>? FocusRequested;

    private void InitializeReadiness()
    {
        ReviewTargetCommand = new RelayCommand(_ => FocusRequested?.Invoke("target"));
        ReviewInputsCommand = new RelayCommand(_ => FocusRequested?.Invoke("inputs"));
        ReviewDeviceCommand = new RelayCommand(_ => NavigationRequested?.Invoke(_services.Installation is null ? "Settings" : "Hardware"));
        RefreshReadinessCommand = new AsyncCommand(_ => RefreshReadinessAsync(), _services.ReportError);
        PropertyChanged += (_, args) =>
        {
            if (string.IsNullOrEmpty(args.PropertyName) || DraftInputProperties.Contains(args.PropertyName)) DraftChanged();
        };
        Wordlists.CollectionChanged += (_, _) => DraftChanged();
        Rules.CollectionChanged += (_, _) => DraftChanged();
        Inspector.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(InspectorViewModel.TargetRevision) or nameof(InspectorViewModel.SelectedMode) or nameof(InspectorViewModel.PreparedTargetPath)) DraftChanged();
            else if (args.PropertyName is nameof(InspectorViewModel.ValidationMessage) or nameof(InspectorViewModel.ReadingDrive)) ScheduleReadiness();
        };
        _services.DefaultDevicesChanged += DraftChanged;
        _services.BackendChanged += DraftChanged;
        _services.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(AppServices.IsComputeBusy)) ScheduleReadiness(); };
        ScheduleReadiness();
    }

    private void DraftChanged()
    {
        ScheduleReadiness();
    }

    private void ScheduleReadiness()
    {
        _readinessCancellation?.Cancel();
        _readinessCancellation?.Dispose();
        _readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
        _ = RefreshReadinessDelayedAsync(_readinessCancellation.Token);
    }

    private async Task RefreshReadinessDelayedAsync(CancellationToken token)
    {
        try { await Task.Delay(250, token); await UpdateReadinessAsync(token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) ReadinessFailed();
        }
    }

    public async Task RefreshReadinessAsync()
    {
        _readinessCancellation?.Cancel();
        _readinessCancellation?.Dispose();
        _readinessCancellation = CancellationTokenSource.CreateLinkedTokenSource(_services.LifetimeToken);
        try { await UpdateReadinessAsync(_readinessCancellation.Token); }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.IO.IOException or UnauthorizedAccessException) { ReadinessFailed(); }
    }

    private void ReadinessFailed()
    {
        InputsReady = false; DeviceReady = false;
        _deviceReadinessState = ReadinessState.Pending;
        DeviceReadiness = "Device check pending until the draft settings are corrected.";
        InputsReadiness = "Review the selected files and Expert run options; the draft could not be checked.";
        ReadinessSummary = "Some settings need attention. Review the inputs before starting.";
        RaiseReadinessStates();
    }

    private void RaiseReadinessStates()
    {
        Raise(nameof(TargetReadinessState)); Raise(nameof(InputsReadinessState));
        Raise(nameof(DeviceReadinessState)); Raise(nameof(OverallReadinessState));
    }

    private async Task UpdateReadinessAsync(CancellationToken token)
    {
        var inputMode = Inspector.InputMode;
        var textProvided = !string.IsNullOrWhiteSpace(Inspector.HashText);
        var path = Inspector.PreparedTargetPath ?? Inspector.TargetPath;
        var prepared = Inspector.PreparedTargetPath is not null;
        var driveSelected = Inspector.SelectedDrive is not null;
        var selectedMode = Inspector.SelectedMode;
        var installation = _services.Installation;
        var validationMessage = Inspector.ValidationMessage;
        var job = BuildDraft(path);
        var checks = await Task.Run(() => (Readable: AttackReadiness.IsReadableFile(path), Errors: AttackReadiness.CheckInputs(job.Attack, _rulePresets, _maskPresets)), token);
        token.ThrowIfCancellationRequested();
        var supplied = inputMode == 0 ? textProvided : inputMode == 3 ? driveSelected || prepared : checks.Readable;
        TargetReady = supplied && selectedMode is not null && (inputMode < 2 || prepared) && string.IsNullOrEmpty(validationMessage);
        TargetReadiness = !supplied ? inputMode == 3 ? "Select a BitLocker drive below." : "Add a hash, hash file or encrypted file below. Files must be readable and nonempty."
            : !string.IsNullOrEmpty(validationMessage) ? "Needs attention — review the target message below."
            : TargetReady ? $"Ready for validation — {selectedMode!.Name}."
            : selectedMode is null && Inspector.Matches.Count > 1 ? "Choose the hash type that matches your source from the detected possibilities."
            : "Target selected — Start will analyze it, or use Analyze target below.";
        InputsReady = checks.Errors.Count == 0;
        InputsReadiness = InputsReady ? "Ready for validation — your wordlist / mask and rule selections are available." : string.Join(" ", checks.Errors.Take(2));
        var previousDevice = installation is null ? null : _services.DeviceSelection.GetPreviouslyVerifiedDeviceId(installation);
        DeviceReady = previousDevice is not null && (job.Options.Devices.Count == 0 || job.Options.Devices.SequenceEqual([previousDevice.Value]));
        DeviceReadiness = installation is null ? "Setup needed — locate and validate Hashcat in Settings."
            : DeviceReady ? $"Device {previousDevice} passed an earlier sample check. Support can vary by hash type."
            : job.Options.Devices.Count > 0 ? $"Selected device(s): {string.Join(", ", job.Options.Devices)}. Test them in Hardware if needed."
            : "Automatic — a working device will be checked before recovery starts.";
        _targetReadinessState = TargetReady ? ReadinessState.Ready
            : !supplied || !string.IsNullOrEmpty(validationMessage) || selectedMode is null && Inspector.Matches.Count > 1
                ? ReadinessState.NeedsAttention : ReadinessState.Pending;
        _deviceReadinessState = DeviceReady ? ReadinessState.Ready : installation is null ? ReadinessState.NeedsAttention : ReadinessState.Pending;
        ReadinessSummary = _services.IsComputeBusy ? "A recovery or device check is running. You can prepare the next attempt here."
            : !supplied || !InputsReady || installation is null ? "Complete the items below. Start recovery runs the final checks."
            : TargetReady ? "Ready for final checks. Start recovery when you’re ready."
            : "Inputs selected. Start will analyze your target and ask if a hash type needs choosing.";
        Raise(nameof(TargetActionLabel)); Raise(nameof(DeviceActionLabel));
        RaiseReadinessStates();
    }
}
