using HashLynx.Hashcat;

namespace HashLynx.UI.ViewModels;

public enum MaskInputKind { BuiltIn, Text, File }

public sealed record MaskSourceChoice(MaskInputKind Kind, string DisplayName)
{
    public static MaskSourceChoice BuiltIn { get; } = new(MaskInputKind.BuiltIn, "Built-in common structures (1,000 masks)");
    public static MaskSourceChoice Text { get; } = new(MaskInputKind.Text, "Write a mask");
    public static MaskSourceChoice File { get; } = new(MaskInputKind.File, "Use my mask file");
    public override string ToString() => DisplayName;
}

public sealed partial class AttackViewModel
{
    private readonly MaskPresetCatalog _maskPresets = new();
    private MaskSourceChoice _maskSource = MaskSourceChoice.BuiltIn;
    private MaskSourceChoice _hybridMaskSource = MaskSourceChoice.Text;
    public IReadOnlyList<MaskSourceChoice> MaskSources => IsHybrid
        ? [MaskSourceChoice.Text, MaskSourceChoice.File]
        : [MaskSourceChoice.BuiltIn, MaskSourceChoice.Text, MaskSourceChoice.File];
    public MaskSourceChoice SelectedMaskSource
    {
        get => IsHybrid ? _hybridMaskSource : _maskSource;
        set
        {
            if (value is null || (IsHybrid && value.Kind == MaskInputKind.BuiltIn)) return;
            if (IsHybrid) { if (!Set(ref _hybridMaskSource, value)) return; }
            else if (!Set(ref _maskSource, value)) return;
            RaiseMaskVisibility();
        }
    }
    public bool IsBuiltInMask => Family == 1 && SelectedMaskSource.Kind == MaskInputKind.BuiltIn;
    public bool ShowMaskText => UsesMask && SelectedMaskSource.Kind == MaskInputKind.Text;
    public bool ShowMaskFile => UsesMask && SelectedMaskSource.Kind == MaskInputKind.File;
    public bool UsesCustomMaskOptions => UsesMask && !IsBuiltInMask;
    public string? EffectiveMaskPresetId => IsBuiltInMask ? MaskPresetCatalog.Common1000Id : null;
    public string? EffectiveMask => ShowMaskText ? Mask : null;
    public string? EffectiveMaskFile => ShowMaskFile ? MaskFile : null;
    private MaskPreset BuiltInMaskPreset => _maskPresets.GetById(MaskPresetCatalog.Common1000Id);
    public string BuiltInMaskDescription => BuiltInMaskPreset.Description;
    public string BuiltInMaskSummary => $"{BuiltInMaskPreset.MaskCount:N0} structures, {BuiltInMaskPreset.MinLength}-{BuiltInMaskPreset.MaxLength} characters. No mask file needed.";
    public string BuiltInMaskEffort => BuiltInMaskPreset.CandidateCountLabel + ". Some structures search enormous numbers of candidates and may take impractically long, especially for encrypted files. You can stop at any time.";

    private void RaiseMaskVisibility()
    {
        Raise(nameof(MaskSources)); Raise(nameof(SelectedMaskSource));
        Raise(nameof(IsBuiltInMask)); Raise(nameof(ShowMaskText)); Raise(nameof(ShowMaskFile)); Raise(nameof(UsesCustomMaskOptions));
    }
}
