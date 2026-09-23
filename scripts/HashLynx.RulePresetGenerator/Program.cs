using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HashLynx.Hashcat;

if (args.Length == 4 && args[0] == "--inventory")
{
    var rows = new List<object>();
    long allRules = 0, allBytes = 0;
    var matches = new List<string>();
    foreach (var file in Directory.EnumerateFiles(args[1]).Order(StringComparer.Ordinal))
    {
        long rules = 0, comments = 0, blank = 0;
        var operations = new Dictionary<char, long>();
        using var normalizedHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var reader = new StreamReader(file))
            while (reader.ReadLine() is { } line)
            {
                normalizedHasher.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
                line = line.Trim();
                if (line.Length == 0) { blank++; continue; }
                if (line.StartsWith('#')) { comments++; continue; }
                rules++;
                operations[line[0]] = operations.GetValueOrDefault(line[0]) + 1;
            }
        using var stream = File.OpenRead(file);
        var sha = Convert.ToHexStringLower(SHA256.HashData(stream));
        var official = Path.Combine(args[2], Path.GetFileName(file));
        bool? matchesOfficial = null;
        bool? normalizedOfficialMatch = null;
        var normalizedSha = Convert.ToHexStringLower(normalizedHasher.GetHashAndReset());
        if (File.Exists(official))
        {
            using var other = File.OpenRead(official);
            matchesOfficial = sha == Convert.ToHexStringLower(SHA256.HashData(other));
            if (matchesOfficial.Value) matches.Add(Path.GetFileName(file));
            using var officialHasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var officialReader = new StreamReader(official);
            while (officialReader.ReadLine() is { } line) officialHasher.AppendData(Encoding.UTF8.GetBytes(line + "\n"));
            normalizedOfficialMatch = normalizedSha == Convert.ToHexStringLower(officialHasher.GetHashAndReset());
        }
        var size = new FileInfo(file).Length;
        allRules += rules; allBytes += size;
        rows.Add(new { Name = Path.GetFileName(file), Bytes = size, RuleLines = rules, CommentLines = comments, BlankLines = blank, Sha256 = sha, MatchesOfficialRelease = matchesOfficial, NormalizedSha256 = normalizedSha, NormalizedOfficialMatch = normalizedOfficialMatch, FirstOperations = operations.OrderByDescending(pair => pair.Value).Select(pair => new { Operation = pair.Key.ToString(), Count = pair.Value }).ToArray() });
    }
    var report = new { Files = rows.Count, Bytes = allBytes, RuleLines = allRules, ExactOfficialMatches = matches, FilesAnalyzed = rows };
    await File.WriteAllTextAsync(args[3], JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Analyzed {rows.Count} files, {allBytes:N0} bytes, {allRules:N0} non-comment rule lines; {matches.Count} exact official-release matches. Report: {args[3]}");
    return;
}

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: dotnet run --project scripts/HashLynx.RulePresetGenerator -- <output-directory>\n       ... -- --inventory <corpus-directory> <official-rules-directory> <report.json>");
    Environment.ExitCode = 2;
    return;
}

Directory.CreateDirectory(args[0]);
foreach (var preset in RulePresetRecipes.Generate())
{
    var path = Path.Combine(args[0], preset.Key + ".rule");
    await File.WriteAllTextAsync(path, RulePresetRecipes.FileContents(preset.Key, preset.Value), new UTF8Encoding(false));
    Console.WriteLine($"{preset.Key}: {preset.Value.Count:N0} rules -> {path}");
}
