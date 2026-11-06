using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>Hashcat 8 retains these command-line aliases while publishing only canonical recipe IDs.</summary>
public static class HashcatModeAliases
{
    // hashcat v8.0.0 docs/changes.txt, "Hash mode numbers", ordered new IDs 40009 through 40078.
    private static readonly int[] PreviousModes =
    [
        1310, 1320, 5200, 5720, 6050, 6060, 6400, 6500, 6700, 7100, 7200, 7900, 9200, 10000,
        10300, 10900, 10901, 11000, 11750, 11760, 11850, 11860, 11900, 12000, 12001, 12100,
        12150, 12300, 12800, 15100, 18500, 18800, 19210, 20200, 20300, 20400, 20600, 20720,
        20730, 21300, 21420, 21600, 22800, 23200, 24200, 26300, 28600, 29100, 32000, 32010,
        32020, 32030, 32031, 32040, 32041, 32050, 32060, 32070, 32410, 32420, 32600, 32900,
        33100, 33300, 33650, 33660, 33900, 34400, 34500, 35000
    ];

    public static int CanonicalMode(HashcatInstallation installation, int mode)
    {
        var index = installation.IsVersion8OrLater ? Array.IndexOf(PreviousModes, mode) : -1;
        return index >= 0 ? 40009 + index : mode;
    }

    public static bool IsSupported(HashcatInstallation installation, int mode, IReadOnlyList<HashMode> catalog) =>
        catalog.Any(item => item.Mode == mode || item.Mode == CanonicalMode(installation, mode));
}
