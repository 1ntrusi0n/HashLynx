using System.Globalization;

namespace HashLynx.Updates;

/// <summary>Semantic versions, including prerelease ordering; build metadata does not affect ordering.</summary>
public sealed class ApplicationVersion : IComparable<ApplicationVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;
    public bool IsPrerelease => _prerelease.Length > 0;
    private ApplicationVersion(int[] numbers, string[] prerelease) { _numbers = numbers; _prerelease = prerelease; }

    public static bool TryParse(string? text, out ApplicationVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 160) return false;
        text = text.Trim();
        if (text.StartsWith('v')) text = text[1..];
        var build = text.Split('+');
        if (build.Length > 2 || (build.Length == 2 && !ValidIdentifiers(build[1], false))) return false;
        var parts = build[0].Split('-', 2);
        var numbers = parts[0].Split('.');
        if (numbers.Length != 3) return false;
        var parsed = new int[3];
        for (var i = 0; i < 3; i++)
            if (numbers[i].Length == 0 || (numbers[i].Length > 1 && numbers[i][0] == '0') ||
                !int.TryParse(numbers[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i])) return false;
        if (parts.Length == 2 && !ValidIdentifiers(parts[1], true)) return false;
        version = new(parsed, parts.Length == 2 ? parts[1].Split('.') : []);
        return true;
    }

    private static bool ValidIdentifiers(string value, bool forbidNumericLeadingZero) => value.Split('.').All(identifier =>
        identifier.Length > 0 && identifier.All(c => char.IsAsciiLetterOrDigit(c) || c == '-') &&
        !(forbidNumericLeadingZero && identifier.Length > 1 && identifier[0] == '0' && identifier.All(char.IsAsciiDigit)));

    public int CompareTo(ApplicationVersion? other)
    {
        if (other is null) return 1;
        for (var i = 0; i < 3; i++) { var comparison = _numbers[i].CompareTo(other._numbers[i]); if (comparison != 0) return comparison; }
        if (_prerelease.Length == 0 || other._prerelease.Length == 0)
            return (_prerelease.Length == 0).CompareTo(other._prerelease.Length == 0);
        for (var i = 0; i < Math.Min(_prerelease.Length, other._prerelease.Length); i++)
        {
            var left = _prerelease[i]; var right = other._prerelease[i];
            var numericLeft = left.All(char.IsAsciiDigit); var numericRight = right.All(char.IsAsciiDigit);
            var comparison = numericLeft != numericRight ? (numericLeft ? -1 : 1)
                : numericLeft && left.Length != right.Length ? left.Length.CompareTo(right.Length)
                : string.CompareOrdinal(left, right);
            if (comparison != 0) return comparison;
        }
        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }
}
