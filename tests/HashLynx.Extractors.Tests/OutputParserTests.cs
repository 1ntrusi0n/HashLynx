using HashLynx.Extractors;

namespace HashLynx.Extractors.Tests;

public sealed class OutputParserTests
{
    [Theory]
    [InlineData("pdf", "C:\\private\\document.pdf:$pdf$1*2*40*-1*0*16*001122*32*aabb*32*ccdd::::document", "$pdf$1*2*40*-1*0*16*001122*32*aabb*32*ccdd", 10400)]
    [InlineData("zip", "archive.zip:$zip2$*0*1*0*abcd*12*0**abcdef*$/zip2$:contents.txt:archive.zip", "$zip2$*0*1*0*abcd*12*0**abcdef*$/zip2$", 13600)]
    [InlineData("zip", "archive.zip:$pkzip2$1*1*2*0*abc*$/pkzip2$:::::archive.zip", "$pkzip2$1*1*2*0*abc*$/pkzip2$", 17200)]
    [InlineData("rar", "archive.rar:$RAR3$*0*abcdef*012345::::archive.rar", "$RAR3$*0*abcdef*012345", 12500)]
    [InlineData("rar", "archive.rar:$rar5$16$abc$15$0000$8$abcd", "$rar5$16$abc$15$0000$8$abcd", 13000)]
    [InlineData("rar", "archive.rar:$RAR3$*1*abc*0123*16*14*1*abcd*30:filename", "$RAR3$*1*abc*0123*16*14*1*abcd*30", 23700)]
    [InlineData("rar", "archive.rar:$RAR3$*1*abc*0123*16*14*1*abcd*33:filename", "$RAR3$*1*abc*0123*16*14*1*abcd*33", 23800)]
    [InlineData("7z", "archive.7z:$7z$0$14$0$$11$abc$123$16$12$abcd", "$7z$0$14$0$$11$abc$123$16$12$abcd", 11600)]
    public void StripsJohnMetadataButKeepsHashToken(string id, string output, string expected, int mode)
    {
        var result = ExtractorOutputParser.Parse(id, output);
        Assert.Equal(expected, Assert.Single(result.Hashes));
        Assert.Contains(mode, result.SuggestedHashcatModes);
    }

    [Theory]
    [InlineData("2", "40", 10400)]
    [InlineData("3", "40", 10510)]
    [InlineData("4", "128", 10500)]
    [InlineData("5", "256", 10600)]
    [InlineData("6", "256", 10700)]
    public void SuggestsPdfModeFromEncryptionRevision(string revision, string bits, int mode)
    {
        var result = ExtractorOutputParser.Parse("pdf", $"$pdf$5*{revision}*{bits}*-1*0*16*001122*32*aabb*32*ccdd");
        Assert.Contains(mode, result.SuggestedHashcatModes);
    }

    [Fact]
    public void BitLockerDiagnosticLinesAreIgnoredAndDuplicateHashesRemoved()
    {
        const string hash = "$bitlocker$0$16$abcdef$1048576$12$012345$60$abcdef";
        var result = ExtractorOutputParser.Parse("bitlocker", $"Parsing stretch key...\nSalt: abcdef\n{hash}\nThe following hashcat hashes were found:\n{hash}\n");
        Assert.Equal(hash, Assert.Single(result.Hashes));
        Assert.Equal(22100, Assert.Single(result.SuggestedHashcatModes));
    }

    [Fact]
    public void UnsupportedRarExternalDataReferencesAreNotPassedToHashcat()
    {
        var result = ExtractorOutputParser.Parse("rar", "file:$RAR3$*1*abc*0123*16*14*0*archive.rar*30");
        Assert.Empty(result.Hashes);
        Assert.Contains(result.Diagnostics, x => x.Contains("external archive data", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingZipTerminatorIsRejected()
    {
        var result = ExtractorOutputParser.Parse("zip", "archive.zip:$pkzip2$1*1*2*0*abc");
        Assert.Empty(result.Hashes);
    }

    [Fact]
    public void OtherExtractorSignatureIsIgnored()
    {
        Assert.Empty(ExtractorOutputParser.Parse("pdf", "$bitlocker$0$16$abcdef$1048576$12$012345$60$abcdef").Hashes);
    }

    [Fact]
    public void DiagnosticReferencesToHashPrefixesAreNotTreatedAsHashes()
    {
        Assert.Empty(ExtractorOutputParser.Parse("pdf", "Please provide a $pdf$1*2*40 token").Hashes);
    }
}
