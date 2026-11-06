using System.Reflection;
using System.IO;

namespace HashLynx.UI.Services;

public static class ApplicationInfo
{
    public static string Version { get; } = typeof(ApplicationInfo).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(ApplicationInfo).Assembly.GetName().Version?.ToString(3) ?? "Unknown";

    public static string Changelog { get; } = ReadChangelog();

    private static string ReadChangelog()
    {
        using var stream = typeof(ApplicationInfo).Assembly.GetManifestResourceStream("HashLynx.Changelog.md");
        if (stream is null) return "Release notes are unavailable in this build.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
