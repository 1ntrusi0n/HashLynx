namespace HashLynx.Core;

public sealed record MaskAnalysis(int Length, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public static class MaskValidator
{
    public static MaskAnalysis Analyze(string? mask, IReadOnlyDictionary<int, string>? customCharsets = null)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(mask)) return new(0, ["Enter a mask or select a mask file."]);
        var validatedCharsets = new HashSet<int>();
        int Scan(string value, HashSet<int> visiting)
        {
            var length = 0;
            if (value.Any(c => c is '\0' or '\r' or '\n')) errors.Add("Masks and custom charsets cannot contain NUL or line-break characters.");
            for (var i = 0; i < value.Length; i++)
            {
                length++;
                if (value[i] != '?') continue;
                if (++i == value.Length) { errors.Add("A trailing '?' needs a charset token; use '??' for a literal question mark."); break; }
                var token = value[i];
                if ("ludhHsab?".Contains(token)) continue;
                if (token is >= '1' and <= '8')
                {
                    var key = token - '0';
                    if (customCharsets is null || !customCharsets.TryGetValue(key, out var charset) || string.IsNullOrEmpty(charset))
                        errors.Add($"Custom charset ?{token} has not been defined.");
                    else if (visiting.Contains(key)) errors.Add($"Custom charset ?{token} contains a circular reference.");
                    else if (validatedCharsets.Add(key))
                    {
                        visiting.Add(key); Scan(charset, visiting); visiting.Remove(key);
                    }
                }
                else errors.Add($"Unknown mask token '?{token}'.");
            }
            return length;
        }
        var maskLength = Scan(mask, []);
        if (customCharsets is not null)
            foreach (var pair in customCharsets)
                if (validatedCharsets.Add(pair.Key)) Scan(pair.Value, [pair.Key]);
        return new(maskLength, errors.Distinct().ToList());
    }
}
