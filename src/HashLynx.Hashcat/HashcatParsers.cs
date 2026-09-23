using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using HashLynx.Core;

namespace HashLynx.Hashcat;

public static class HashcatHelpParser
{
    public static HashcatCapabilities ParseCapabilities(string help)
    {
        var capabilities = new HashcatCapabilities
        {
            Identify = help.Contains("--identify", StringComparison.Ordinal),
            Status = help.Contains("--status ", StringComparison.Ordinal),
            StatusJson = help.Contains("--status-json", StringComparison.Ordinal),
            HashInfo = help.Contains("--hash-info", StringComparison.Ordinal),
            BackendInfo = help.Contains("--backend-info", StringComparison.Ordinal),
            Restore = help.Contains("--restore ", StringComparison.Ordinal),
            CustomCharsetCount = help.Contains("--custom-charset8", StringComparison.Ordinal) ? 8 : 4
        };
        var section = Section(help, "Attack Modes");
        foreach (var line in section.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(\d+)\s*\|\s*(.+?)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var label = match.Groups[2].Value.Trim();
            var key = label switch
            {
                "Straight" => AttackFamilies.Dictionary,
                "Combination" => AttackFamilies.Combinator,
                "Brute-force" => AttackFamilies.Mask,
                "Hybrid Wordlist + Mask" => AttackFamilies.HybridWordlistMask,
                "Hybrid Mask + Wordlist" => AttackFamilies.HybridMaskWordlist,
                _ => "hashcat:" + id.ToString(CultureInfo.InvariantCulture)
            };
            capabilities.AttackModes[key] = id;
        }
        return capabilities;
    }

    internal static string Section(string help, string title)
    {
        var start = help.IndexOf("[ " + title + " ]", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";
        var end = help.IndexOf("- [ ", start + title.Length + 4, StringComparison.Ordinal);
        return end < 0 ? help[start..] : help[start..end];
    }
}

public static class HashModeParser
{
    public static IReadOnlyList<HashMode> ParseIdentification(string output) => ParseTable(output);
    public static IReadOnlyList<HashMode> ParseHelpCatalog(string output) => ParseTable(HashcatHelpParser.Section(output, "Hash Modes"));

    private static IReadOnlyList<HashMode> ParseTable(string text)
    {
        var modes = new Dictionary<int, HashMode>();
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(\d+)\s*\|\s*([^|]+?)\s*\|\s*(.*?)\s*$", RegexOptions.CultureInvariant);
            if (!match.Success) continue;
            var mode = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            modes[mode] = new(mode, match.Groups[2].Value.Trim(), match.Groups[3].Value.Trim());
        }
        return modes.Values.OrderBy(m => m.Mode).ToList();
    }

    public static IReadOnlyList<HashMode> ParseHashInfo(string text)
    {
        var modes = new List<HashMode>();
        int? id = null;
        string name = "", category = "";
        void Add() { if (id is { } value && name.Length > 0) modes.Add(new(value, name, category)); }
        foreach (var line in text.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*Hash mode #(\d+)", RegexOptions.CultureInvariant);
            if (match.Success) { Add(); id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); name = ""; category = ""; continue; }
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().Trim('.').Trim();
            var valueText = line[(colon + 1)..].Trim();
            if (key.Equals("Name", StringComparison.OrdinalIgnoreCase)) name = valueText;
            if (key.Equals("Category", StringComparison.OrdinalIgnoreCase)) category = valueText;
        }
        Add();
        return modes;
    }
}

public static class HashcatStatusParser
{
    public static bool TryParse(string line, out JobStatusSnapshot? status)
    {
        status = null;
        var start = line.IndexOf('{');
        if (start < 0) return false;
        try
        {
            using var document = JsonDocument.Parse(line[start..]);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var state)) return false;
            var progress = Pair(root, "progress");
            var recovered = Pair(root, "recovered_hashes");
            status = new()
            {
                State = state.ValueKind == JsonValueKind.String ? state.GetString() ?? "Unknown" : StateName((int)Number(state)),
                Progress = progress.First,
                ProgressTotal = progress.Second,
                ProgressPercent = progress.Second > 0 ? Math.Clamp(100d * progress.First / progress.Second, 0, 100) : 0,
                RecoveredHashes = recovered.First,
                TotalHashes = recovered.Second,
                RejectedCandidates = Integer(root, "rejected"),
                RestorePoint = Integer(root, "restore_point"),
                StartedAt = Timestamp(root, "time_start"),
                EstimatedCompletion = Timestamp(root, "estimated_stop")
            };
            if (root.TryGetProperty("devices", out var devices) && devices.ValueKind == JsonValueKind.Array)
                foreach (var device in devices.EnumerateArray())
                {
                    var speed = Double(device, "speed");
                    status.Devices.Add(new()
                    {
                        Id = (int)Integer(device, "device_id"), Name = String(device, "device_name"),
                        SpeedHashesPerSecond = speed,
                        Temperature = OptionalInt(device, "temp"), Utilization = OptionalInt(device, "util")
                    });
                }
            status.SpeedHashesPerSecond = status.Devices.Sum(d => d.SpeedHashesPerSecond);
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentOutOfRangeException) { return false; }
    }

    private static string StateName(int value) => value switch { 0 => "Initializing", 1 => "Autotuning", 2 => "Self-test", 3 => "Running", 4 => "Paused", 5 => "Exhausted", 6 => "Cracked", 7 => "Aborted", 8 => "Quit", 9 => "Bypass", 10 => "Checkpoint", 11 => "Runtime limit", 13 => "Error", 14 => "Finished", 16 => "Autodetecting", _ => "Unknown" };
    private static double Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : double.TryParse(value.ToString(), CultureInfo.InvariantCulture, out var number) ? number : 0;
    private static double Double(JsonElement root, string key) => root.TryGetProperty(key, out var value) ? Number(value) : 0;
    private static long Integer(JsonElement root, string key) => (long)Double(root, key);
    private static int? OptionalInt(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind != JsonValueKind.Null ? (int)Number(value) : null;
    private static string String(JsonElement root, string key) => root.TryGetProperty(key, out var value) ? value.ToString() : "";
    private static (long First, long Second) Pair(JsonElement root, string key) => root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Array && value.GetArrayLength() >= 2 ? ((long)Number(value[0]), (long)Number(value[1])) : (0, 0);
    private static DateTimeOffset? Timestamp(JsonElement root, string key)
    {
        var seconds = Integer(root, key);
        return seconds > 0 && seconds < 253402300799 ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
    }
}

public static class BackendInfoParser
{
    public static IReadOnlyList<BackendDevice> Parse(string output)
    {
        var devices = new List<BackendDevice>();
        BackendDevice? current = null;
        string backend = "";
        foreach (var line in output.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^\s*(CUDA|HIP|OpenCL|Metal) Info:", RegexOptions.CultureInvariant)) { backend = line.Trim().Split(' ')[0]; current = null; }
            if (line.Contains("Platform ID #", StringComparison.Ordinal)) current = null;
            var match = Regex.Match(line, @"Backend Device ID #(\d+)", RegexOptions.CultureInvariant);
            if (match.Success) { current = new() { Id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), Backend = backend }; devices.Add(current); continue; }
            if (current is null) continue;
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim().TrimEnd('.');
            var value = line[(colon + 1)..].Trim();
            switch (key) { case "Name": current.Name = value; break; case "Type": current.Type = value; break; case "Memory.Total": current.Memory = value; break; case "Driver.Version": current.Driver = value; break; }
        }
        return devices;
    }
}

public static class RecoveredResultParser
{
    // Hashcat --outfile-format 1,3 emits hash:hex_plain, so the final delimiter remains unambiguous even for salted hashes or passwords with colons.
    public static IReadOnlyList<RecoveredResult> Parse(string output)
    {
        var results = new List<RecoveredResult>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var value = line.TrimEnd('\r');
            var separator = value.LastIndexOf(':');
            if (separator < 0) continue;
            var hex = value[(separator + 1)..];
            try { results.Add(new(value[..separator], Encoding.UTF8.GetString(Convert.FromHexString(hex)), hex)); }
            catch (FormatException) { /* Non-result diagnostic lines are intentionally excluded. */ }
        }
        return results;
    }
}
