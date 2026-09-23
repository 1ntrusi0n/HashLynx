using System.Text.RegularExpressions;
using System.Text;
using System.Runtime.CompilerServices;

namespace HashLynx.Core;

public sealed record HashFileProblem(long LineNumber, string Reason);
public sealed class HashFileAnalysis
{
    public long TotalLines { get; set; }
    public long BlankLines { get; set; }
    public long CandidateLines { get; set; }
    public long ProblemLines { get; set; }
    public List<HashFileProblem> Problems { get; } = [];
    public Dictionary<string, long> StructuralGroups { get; } = [];
    public string Notice => "Structure checks do not prove validity or identify an algorithm. Use Hashcat identification and select a mode.";
}

/// <summary>Streams source data without modifying it or retaining sensitive line contents.</summary>
public sealed class HashFileAnalyzer
{
    public async Task<HashFileAnalysis> AnalyzeAsync(string path, CancellationToken cancellationToken = default)
    {
        var result = new HashFileAnalysis();
        using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan));
        await foreach (var item in ReadBoundedLinesAsync(reader, cancellationToken))
        {
            var line = item.Text;
            result.TotalLines++;
            if (!item.Truncated && string.IsNullOrWhiteSpace(line)) { result.BlankLines++; continue; }
            string? problem = null;
            if (item.Truncated) problem = "Line exceeds the analysis limit (1 MiB, or 16 MiB + 256 characters for WinZip AES records); inspect the source format. Analysis retained only a bounded prefix.";
            else if (line.Contains('\0')) problem = "Contains binary NUL data; this may not be a text hash list.";
            else if (line != line.Trim()) problem = "Leading or trailing whitespace may affect parsing.";
            if (problem is not null)
            {
                result.ProblemLines++;
                if (result.Problems.Count < 200) result.Problems.Add(new(result.TotalLines, problem));
            }
            else result.CandidateLines++;
            var group = item.Truncated ? "Oversized line" : StructuralGroup(line);
            if (result.StructuralGroups.Count >= 100 && !result.StructuralGroups.ContainsKey(group)) group = "Other structures";
            result.StructuralGroups[group] = result.StructuralGroups.GetValueOrDefault(group) + 1;
        }
        return result;
    }

    private static async IAsyncEnumerable<(string Text, bool Truncated)> ReadBoundedLinesAsync(StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        const int ordinaryMaxLength = 1_048_576;
        const int zipMaxLength = 16 * 1024 * 1024 + 256;
        var maxLength = ordinaryMaxLength;
        var buffer = new char[16384];
        var line = new StringBuilder();
        var truncated = false;
        var previousCarriageReturn = false;
        int read;
        cancellationToken.ThrowIfCancellationRequested();
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var character = buffer[index];
                if (character is '\r' or '\n')
                {
                    if (character == '\r' || !previousCarriageReturn)
                    {
                        yield return (line.ToString(), truncated);
                        line.Clear(); truncated = false;
                        maxLength = ordinaryMaxLength;
                    }
                    previousCarriageReturn = character == '\r';
                }
                else
                {
                    previousCarriageReturn = false;
                    if (line.Length < maxLength)
                    {
                        line.Append(character);
                        if (line.Length == 7 && line.ToString() == "$zip2$*") maxLength = zipMaxLength;
                    }
                    else truncated = true;
                }
            }
        }
        if (line.Length > 0 || truncated) yield return (line.ToString(), truncated);
    }

    private static string StructuralGroup(string value)
    {
        if (Regex.IsMatch(value, "^[0-9a-fA-F]+$", RegexOptions.CultureInvariant)) return $"Hexadecimal · {value.Length} characters (ambiguous)";
        if (value.StartsWith('$')) return $"Tagged format · {value.Count(c => c == '$')} separators";
        if (value.Contains(':')) return $"Colon-delimited · {value.Count(c => c == ':') + 1} fields";
        return $"Text · {value.Length} characters";
    }
}
