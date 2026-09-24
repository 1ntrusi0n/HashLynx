using System.Text.Json;
using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class MaskPresetCommandTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "HashLynx-mask-commands-" + Guid.NewGuid().ToString("N"));
    private readonly HashcatCommandBuilder _builder;
    private readonly HashcatInstallation _installation;
    private readonly HashcatJob _job;

    public MaskPresetCommandTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "backend"));
        var executable = Path.Combine(_root, "backend", "hashcat.exe");
        var target = Path.Combine(_root, "target.hash");
        File.WriteAllText(executable, "fixture"); File.WriteAllText(target, "fixture");
        _installation = new(executable, "test", new() { StatusJson = true, AttackModes = new()
        {
            [AttackFamilies.Dictionary] = 0, [AttackFamilies.Mask] = 3,
            [AttackFamilies.HybridWordlistMask] = 6, [AttackFamilies.HybridMaskWordlist] = 7
        } });
        _builder = new(Path.Combine(_root, "jobs"));
        _job = new() { TargetPath = target, HashMode = 0, Attack = new() { Kind = AttackFamilies.Mask, MaskPresetId = MaskPresetCatalog.Common1000Id } };
    }

    [Fact]
    public void PresetResolvesToOneManagedFileWithoutChangingTheSavedConfiguration()
    {
        var before = JsonSerializer.Serialize(_job.Attack);
        var validation = new JobValidator(_builder).Validate(_installation, _job);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors.Select(error => error.Message)));
        Assert.Contains(validation.Warnings, warning => warning.Message.Contains("trillion"));
        var command = _builder.Build(_installation, _job);
        var separator = command.Arguments.ToList().IndexOf("--");
        Assert.Equal([_job.TargetPath, _builder.MaskPresets.GetById(MaskPresetCatalog.Common1000Id).MaskFilePath], command.Arguments.Skip(separator + 1));
        Assert.Equal(before, JsonSerializer.Serialize(_job.Attack));
        Assert.DoesNotContain("--increment", command.Arguments);
        Assert.DoesNotContain(command.Arguments, value => value.StartsWith("--custom-charset", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("text")]
    [InlineData("file")]
    [InlineData("increment")]
    [InlineData("charset")]
    [InlineData("dictionary")]
    [InlineData("hybrid-wordlist")]
    [InlineData("hybrid-mask")]
    public void InvalidPresetCombinationsFailValidationAndCommandConstruction(string issue)
    {
        switch (issue)
        {
            case "unknown": _job.Attack.MaskPresetId = "unavailable-v2"; break;
            case "text": _job.Attack.Mask = "?d?d"; break;
            case "file": _job.Attack.MaskFile = Path.Combine(_root, "custom.hcmask"); break;
            case "increment": _job.Attack.Increment = true; break;
            case "charset": _job.Attack.CustomCharsets[1] = "?d"; break;
            case "dictionary": _job.Attack.Kind = AttackFamilies.Dictionary; break;
            case "hybrid-wordlist": _job.Attack.Kind = AttackFamilies.HybridWordlistMask; break;
            case "hybrid-mask": _job.Attack.Kind = AttackFamilies.HybridMaskWordlist; break;
        }
        Assert.Contains(new JobValidator(_builder).Validate(_installation, _job).Errors, error => error.Field == "Mask");
        Assert.Throws<ArgumentException>(() => _builder.Build(_installation, _job));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("restore")]
    [InlineData("potfile")]
    public void OutputFilesCannotOverwriteTheMaterializedMaskList(string type)
    {
        var path = _builder.MaskPresets.GetById(MaskPresetCatalog.Common1000Id).MaskFilePath;
        if (type == "output") _job.Options.OutputPath = path;
        else if (type == "restore") _job.Options.RestorePath = path;
        else _job.Options.PotfilePath = path;
        Assert.Contains(new JobValidator(_builder).Validate(_installation, _job).Errors,
            error => error.Message.Contains("must not overwrite an input"));
    }

    [Fact]
    public void LegacyCustomMasksAndFilesRemainCustomAfterSerialization()
    {
        var legacy = JsonSerializer.Deserialize<AttackConfiguration>("{\"Kind\":\"mask\",\"Mask\":\"?d?d\"}")!;
        Assert.Null(legacy.MaskPresetId);
        _job.Attack = legacy;
        Assert.Equal("?d?d", _builder.Build(_installation, _job).Arguments[^1]);
        var file = Path.Combine(_root, "custom file.hcmask"); File.WriteAllText(file, "?u?d\n");
        legacy.Mask = null; legacy.MaskFile = file;
        Assert.Equal(file, _builder.Build(_installation, _job).Arguments[^1]);
        var roundTrip = JsonSerializer.Deserialize<AttackConfiguration>(JsonSerializer.Serialize(_job.Attack))!;
        Assert.Null(roundTrip.MaskPresetId);
        Assert.Equal(file, roundTrip.MaskFile);
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
