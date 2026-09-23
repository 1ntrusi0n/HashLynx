using System.Globalization;

namespace HashLynx.Extractors;

public sealed record ParsedExtractorOutput(IReadOnlyList<string> Hashes, IReadOnlyList<int> SuggestedHashcatModes, IReadOnlyList<string> Diagnostics);

/// <summary>Removes John filename/login fields while preserving the actual Hashcat token, including ZIP terminators.</summary>
public static class ExtractorOutputParser
{
    private static readonly IReadOnlyDictionary<string, string[]> Signatures = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
    {
        ["pdf"] = ["$pdf$"], ["zip"] = ["$pkzip2$", "$pkzip$", "$zip2$"],
        ["rar"] = ["$RAR3$", "$rar5$"], ["7z"] = ["$7z$"], ["bitlocker"] = ["$bitlocker$"]
    };

    public static ParsedExtractorOutput Parse(string extractorId, string output)
    {
        if (!Signatures.TryGetValue(extractorId, out var signatures)) return new([], [], ["No parser is registered for this extractor."]);
        var hashes = new List<string>();
        var modes = new HashSet<int>();
        var diagnostics = new HashSet<string>();
        using var reader = new StringReader(output);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            foreach (var signature in signatures)
            {
                var start = line.IndexOf(signature, StringComparison.Ordinal);
                if (start < 0 || (start > 0 && line[start - 1] != ':')) continue;
                var hash = line[start..];
                var end = hash.IndexOf(':');
                if (end >= 0) hash = hash[..end];
                hash = hash.Trim();
                if (hash.Any(char.IsWhiteSpace) || hash.Contains('\0') || hash.Length < signature.Length + 5)
                {
                    diagnostics.Add("An incomplete or unsupported extractor token was ignored.");
                    continue;
                }
                if (signature is "$pkzip$" or "$pkzip2$" or "$zip2$")
                {
                    var terminator = "$/" + signature[1..];
                    var terminatorPosition = hash.IndexOf(terminator, StringComparison.Ordinal);
                    if (terminatorPosition < 0)
                    {
                        diagnostics.Add("An incomplete ZIP token was ignored; the closing marker is missing.");
                        continue;
                    }
                    hash = hash[..(terminatorPosition + terminator.Length)];
                }
                if (hash.StartsWith("$RAR3$*1*", StringComparison.Ordinal))
                {
                    var fields = hash.Split('*');
                    if (fields.Length < 9 || fields[6] != "1")
                    {
                        diagnostics.Add("RAR output referring to external archive data is unsupported; configure a tool that emits inline data.");
                        continue;
                    }
                }
                if (extractorId == "bitlocker" && !hash.StartsWith("$bitlocker$0$", StringComparison.Ordinal) && !hash.StartsWith("$bitlocker$1$", StringComparison.Ordinal))
                {
                    diagnostics.Add("Only BitLocker user-password hash versions 0 and 1 are supported by this adapter.");
                    continue;
                }
                if (!hashes.Contains(hash, StringComparer.Ordinal)) hashes.Add(hash);
                foreach (var mode in SuggestModes(hash)) modes.Add(mode);
                break;
            }
        }
        if (hashes.Count == 0) diagnostics.Add("The tool returned no supported hashes. The file may be unencrypted, unsupported, or require a different extractor.");
        return new(hashes, modes.Order().ToArray(), diagnostics.ToArray());
    }

    private static IEnumerable<int> SuggestModes(string hash)
    {
        if (hash.StartsWith("$pdf$", StringComparison.Ordinal))
        {
            var fields = hash[5..].Split('*');
            if (fields.Length > 2 && int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var revision))
                return revision switch
                {
                    2 => [10400],
                    3 or 4 when fields[2] == "40" => [10510],
                    3 or 4 => [10500, 25400],
                    5 => [10600],
                    6 => [10700],
                    _ => []
                };
        }
        if (hash.StartsWith("$zip2$", StringComparison.Ordinal)) return [13600];
        if (hash.StartsWith("$pkzip", StringComparison.Ordinal)) return [17200, 17210, 17220, 17225, 17230];
        if (hash.StartsWith("$RAR3$*0*", StringComparison.Ordinal)) return [12500];
        if (hash.StartsWith("$RAR3$*1*", StringComparison.Ordinal)) return hash.EndsWith("*30", StringComparison.Ordinal) ? [23700] : [23800];
        if (hash.StartsWith("$rar5$", StringComparison.Ordinal)) return [13000];
        if (hash.StartsWith("$7z$", StringComparison.Ordinal)) return [11600];
        if (hash.StartsWith("$bitlocker$", StringComparison.Ordinal)) return [22100];
        return [];
    }
}
