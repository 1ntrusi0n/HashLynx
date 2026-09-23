namespace HashLynx.Extractors;

internal static class ToolLocator
{
    public static string? Resolve(string? configuredPath, params string[] candidateNames)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath)) return ResolveOne(configuredPath);
        foreach (var candidate in candidateNames)
        {
            var result = ResolveOne(candidate);
            if (result is not null) return result;
        }
        return null;
    }

    private static string? ResolveOne(string name)
    {
        try
        {
            if (Path.IsPathRooted(name) || name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
                return File.Exists(name) ? Path.GetFullPath(name) : null;
            foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                if (!Path.HasExtension(name) && File.Exists(candidate + ".exe")) return Path.GetFullPath(candidate + ".exe");
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException) { return null; }
        return null;
    }

    public static bool IsSafeExecutable(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        string[] shells = ["cmd", "powershell", "pwsh", "wscript", "cscript", "bash", "sh"];
        return Path.IsPathFullyQualified(path)
            && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase)
            && !shells.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
}
