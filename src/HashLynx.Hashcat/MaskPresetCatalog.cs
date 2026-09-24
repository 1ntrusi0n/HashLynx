using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HashLynx.Hashcat;

public sealed record MaskPreset(string Id, string DisplayName, string Description, int MaskCount,
    BigInteger CandidateCount, int MinLength, int MaxLength, string MaskFilePath)
{
    public string CandidateCountLabel => $"Up to {CandidateCount:N0} candidates across {MaskCount:N0} patterns";
    public override string ToString() => DisplayName;
}

/// <summary>Immutable, licensed structure-only mask selections, independent of the user's backend and input collections.</summary>
public sealed class MaskPresetCatalog
{
    public const string Common1000Id = "common-1000-v1";
    public const string Common1000Sha256 = "1F69FC0F7CA02497256C0F4426B128C16212914C3191ED1DBF5FCF5262B777B2";
    private readonly string _managedDirectory;
    private static readonly Lazy<byte[]> Asset = new(ReadAsset);
    private static readonly Lazy<IReadOnlyList<string>> Masks = new(() => Array.AsReadOnly(
        Encoding.ASCII.GetString(Asset.Value).Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => !line.StartsWith('#')).Select(line => line.TrimEnd('\r')).ToArray()));
    public IReadOnlyList<MaskPreset> Presets { get; }

    public MaskPresetCatalog(string? managedDirectory = null)
    {
        _managedDirectory = managedDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HashLynx", "mask-presets");
        var masks = Masks.Value;
        Presets = Array.AsReadOnly(new[]
        {
            new MaskPreset(Common1000Id, "Common patterns (1,000)",
                "A historical corpus-based selection of letter, number and symbol patterns. Nearly one trillion candidates; use remembered hints for a smaller search.",
                masks.Count, masks.Aggregate(BigInteger.Zero, (total, mask) => total + CountCandidates(mask)),
                masks.Min(mask => mask.Length / 2), masks.Max(mask => mask.Length / 2),
                Path.Combine(_managedDirectory, Common1000Id + ".hcmask"))
        });
    }

    public MaskPreset GetById(string id) => Presets.FirstOrDefault(preset => preset.Id == id)
        ?? throw new ArgumentException("This mask preset is unavailable. Select a built-in preset again or use a custom mask.", nameof(id));

    public IReadOnlyList<string> GetMasks(string id)
    {
        _ = GetById(id);
        return Masks.Value;
    }

    // The command builder also resolves presets on its background preflight path.
    public string GetMaskFilePath(string id)
    {
        var preset = GetById(id);
        var bytes = Asset.Value;
        if (File.Exists(preset.MaskFilePath) && new FileInfo(preset.MaskFilePath).Length == bytes.Length &&
            SHA256.HashData(File.ReadAllBytes(preset.MaskFilePath)).AsSpan().SequenceEqual(SHA256.HashData(bytes)))
            return preset.MaskFilePath;
        Directory.CreateDirectory(_managedDirectory);
        var temporary = preset.MaskFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, preset.MaskFilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return preset.MaskFilePath;
    }

    public async Task<string> GetMaskFilePathAsync(string id, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var preset = GetById(id);
        var bytes = Asset.Value;
        if (File.Exists(preset.MaskFilePath) && new FileInfo(preset.MaskFilePath).Length == bytes.Length)
        {
            var cached = await File.ReadAllBytesAsync(preset.MaskFilePath, cancellationToken).ConfigureAwait(false);
            if (SHA256.HashData(cached).AsSpan().SequenceEqual(SHA256.HashData(bytes))) return preset.MaskFilePath;
        }
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_managedDirectory);
        var temporary = preset.MaskFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, preset.MaskFilePath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return preset.MaskFilePath;
    }

    /// <summary>Counts full combinations of the four disjoint ASCII character classes used by the built-in catalogue.</summary>
    public static BigInteger CountCandidates(string mask)
    {
        if (string.IsNullOrEmpty(mask) || mask.Length % 2 != 0)
            throw new ArgumentException("A built-in mask must contain complete character-class tokens.", nameof(mask));
        var result = BigInteger.One;
        for (var index = 0; index < mask.Length; index += 2)
        {
            if (mask[index] != '?') throw new ArgumentException("Unsupported built-in mask syntax.", nameof(mask));
            result *= mask[index + 1] switch
            {
                'l' or 'u' => 26,
                'd' => 10,
                's' => 33,
                _ => throw new ArgumentException("Unsupported built-in mask character class.", nameof(mask))
            };
        }
        return result;
    }

    private static byte[] ReadAsset()
    {
        using var stream = typeof(MaskPresetCatalog).Assembly.GetManifestResourceStream($"HashLynx.Hashcat.MaskPresets.{Common1000Id}.hcmask")
            ?? throw new InvalidOperationException("The built-in mask asset is missing from this application build.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        if (Convert.ToHexString(SHA256.HashData(bytes)) != Common1000Sha256)
            throw new InvalidOperationException("The built-in mask asset failed its version integrity check.");
        return bytes;
    }
}
