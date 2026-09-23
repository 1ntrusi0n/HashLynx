namespace HashLynx.Core.Tests;

public sealed class ArchiveHashAnalysisTests
{
    [Theory]
    [InlineData("$zip2$*", 1_100_000, 0)]
    [InlineData("$zip2$*", 16 * 1024 * 1024 + 300, 1)]
    [InlineData("$7z$", 1_100_000, 0)]
    [InlineData("$7z$", 16 * 1024 * 1024 + 300, 1)]
    [InlineData("ordinary", 1_100_000, 1)]
    public async Task InlineAesHasABoundedAllowanceThatDoesNotApplyToFollowingLines(string prefix, int length, int firstProblems)
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, prefix + new string('a', length) + "\n" + new string('b', 1_100_000) + "\n");
            var analysis = await new HashFileAnalyzer().AnalyzeAsync(path);
            Assert.Equal(2, analysis.TotalLines);
            Assert.Equal(firstProblems + 1, analysis.ProblemLines);
        }
        finally { File.Delete(path); }
    }
}
