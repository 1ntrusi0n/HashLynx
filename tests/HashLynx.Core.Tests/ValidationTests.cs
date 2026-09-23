using HashLynx.Core;

namespace HashLynx.Core.Tests;

public sealed class ValidationTests
{
    [Theory]
    [InlineData("?l?u?d?h?H?s?a?b??", 9)]
    [InlineData("prefix?d?d", 8)]
    [InlineData(" ", 1)]
    public void StandardMasksCountTokensAndEscapedQuestionMarks(string mask, int length)
    {
        var result = MaskValidator.Analyze(mask);
        Assert.True(result.IsValid);
        Assert.Equal(length, result.Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData("?z")]
    [InlineData("?l?")]
    [InlineData("?1")]
    public void InvalidAndUndefinedTokensProduceActionableErrors(string mask) => Assert.False(MaskValidator.Analyze(mask).IsValid);

    [Fact]
    public void CustomCharsetsAreRequiredOnlyWhenReferenced()
    {
        Assert.True(MaskValidator.Analyze("?1?8", new Dictionary<int, string> { [1] = "?l?d", [8] = "abc" }).IsValid);
        Assert.False(MaskValidator.Analyze("?2", new Dictionary<int, string> { [1] = "abc" }).IsValid);
    }

    [Theory]
    [InlineData("?z")]
    [InlineData("?")]
    [InlineData("?1")]
    [InlineData("\u0000")]
    [InlineData("a\nb")]
    public void InvalidCustomCharsetDefinitionsFailPreflight(string charset)
    {
        Assert.False(MaskValidator.Analyze("?1", new Dictionary<int, string> { [1] = charset }).IsValid);
    }

    [Fact]
    public void CharsetCyclesAreRejectedAndLiteralSpacesAccepted()
    {
        Assert.False(MaskValidator.Analyze("?1", new Dictionary<int, string> { [1] = "?2", [2] = "?1" }).IsValid);
        Assert.True(MaskValidator.Analyze("?1", new Dictionary<int, string> { [1] = " " }).IsValid);
    }

    [Fact]
    public async Task FileAnalysisCountsStructuralProblemsWithoutStoringHashContents()
    {
        var path = Path.GetTempFileName();
        const string source = "00000000000000000000000000000000\n\n\n tagged \n$example$synthetic\na:b\n";
        try
        {
            await File.WriteAllTextAsync(path, source);
            var result = await new HashFileAnalyzer().AnalyzeAsync(path);
            Assert.Equal(6, result.TotalLines);
            Assert.Equal(2, result.BlankLines);
            Assert.Equal(3, result.CandidateLines);
            Assert.Equal(1, result.ProblemLines);
            Assert.Equal(4, result.Problems.Single().LineNumber);
            Assert.Contains(result.StructuralGroups.Keys, key => key.Contains("ambiguous"));
            Assert.Equal(source, await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task ProblemDetailsAreBoundedForLargeInputs()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllLinesAsync(path, Enumerable.Repeat(" malformed ", 1000));
            var result = await new HashFileAnalyzer().AnalyzeAsync(path);
            Assert.Equal(1000, result.ProblemLines);
            Assert.Equal(200, result.Problems.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task AnalysisHonorsCancellation()
    {
        var path = Path.GetTempFileName();
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new HashFileAnalyzer().AnalyzeAsync(path, new CancellationToken(true))); }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OversizedLinesAndMixedNewlinesAreAnalyzedWithBoundedStorage()
    {
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, new string('a', 1_048_580) + "\r\nabc\rdef\n");
            var result = await new HashFileAnalyzer().AnalyzeAsync(path);
            Assert.Equal(3, result.TotalLines);
            Assert.Equal(1, result.ProblemLines);
            Assert.Contains("1 MiB", result.Problems.Single().Reason);
            Assert.Equal(1, result.StructuralGroups["Oversized line"]);
        }
        finally { File.Delete(path); }
    }
}
