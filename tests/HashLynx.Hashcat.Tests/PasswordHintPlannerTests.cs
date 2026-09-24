using HashLynx.Core;
using HashLynx.Hashcat;

namespace HashLynx.Hashcat.Tests;

public sealed class PasswordHintPlannerTests
{
    private static PasswordHints Pattern(string prefix = "Summer", string suffix = "!", int min = 11, int max = 11, string characters = "Digits")
        => new("", false, true, prefix, suffix, min, max, characters);

    [Fact]
    public void KnownBeginningAndEndingStayFixedAtEveryLength()
    {
        var plan = PasswordHintPlanner.Build(Pattern(min: 10, max: 11));
        Assert.Equal(new[] { "Summer?d?d?d!", "Summer?d?d?d?d!" }, plan.Attacks.Select(item => item.Attack.Mask));
        Assert.All(plan.Attacks, item => Assert.False(item.Attack.Increment));
        Assert.Equal(11000, plan.CandidateEstimate);
        Assert.Equal(new[] { "Summer000!", "Summer0000!" }, plan.Examples);
    }

    [Fact]
    public void LiteralQuestionMarksCannotInjectMaskTokens()
    {
        var plan = PasswordHintPlanner.Build(Pattern("-?d", "?l", 6, 6));
        Assert.Equal("-??d?d??l", plan.Attacks.Single().Attack.Mask);
        Assert.Equal(6, MaskValidator.Analyze(plan.Attacks.Single().Attack.Mask).Length);
        Assert.Equal(10, plan.CandidateEstimate);
    }

    [Fact]
    public void WordsKeepSpacesUnicodeAndCaseAndDefaultToNoRules()
    {
        var plan = PasswordHintPlanner.Build(new(" hello \nHELLO\n hello \ncaf\u00e9", false, false, "", "", 0, 0, "Digits"));
        Assert.Equal(new[] { " hello ", "HELLO", "caf\u00e9" }, plan.Words);
        Assert.Null(plan.Attacks.Single().Attack.RulePresetId);
        Assert.Equal(3, plan.CandidateEstimate);
    }

    [Fact]
    public void OptionalWordVariationsUseVersionedPresets()
    {
        var plan = PasswordHintPlanner.Build(new("example", true, false, "", "", 0, 0, "Digits"));
        Assert.Equal(new string?[] { null, RulePresetCatalog.QuickId, RulePresetCatalog.NormalId }, plan.Attacks.Select(item => item.Attack.RulePresetId));
        Assert.Equal(577, plan.CandidateEstimate);
    }

    [Theory]
    [InlineData(0, 3)] [InlineData(5, 4)] [InlineData(1, 17)] [InlineData(32, 33)]
    public void InvalidLengthRangesAreRejected(int minimum, int maximum)
        => Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(Pattern("", "", minimum, maximum)));

    [Fact]
    public void RejectsPrefixLongerThanTotalAndNonAsciiPatterns()
    {
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(Pattern(min: 6, max: 6)));
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(Pattern("\u00e9")));
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(Pattern("line\nbreak")));
    }

    [Fact]
    public void BroadPatternCountDoesNotOverflow()
    {
        var plan = PasswordHintPlanner.Build(Pattern("", "", 32, 32, "Printable characters"));
        Assert.Equal(System.Numerics.BigInteger.Pow(95, 32), plan.CandidateEstimate);
    }

    [Fact]
    public void LettersAndDigitsUseAnExplicitCustomCharset()
    {
        var plan = PasswordHintPlanner.Build(Pattern("", "", 2, 2, "Letters and digits"));
        var attack = plan.Attacks.Single().Attack;
        Assert.Equal("?1?1", attack.Mask); Assert.Equal("?l?u?d", attack.CustomCharsets[1]);
        Assert.Equal(3844, plan.CandidateEstimate);
    }

    [Fact]
    public void LargeOrEmptyWordInputIsRejected()
    {
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(new("", false, false, "", "", 0, 0, "Digits")));
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(new(new string('x', 129), false, false, "", "", 0, 0, "Digits")));
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.Build(new("one\0two", false, false, "", "", 0, 0, "Digits")));
    }

    [Theory]
    [InlineData("--hex-wordlist")] [InlineData("--hex-charset")] [InlineData("--encoding-from=UTF-16LE")]
    [InlineData("--skip=100")] [InlineData("--limit")]
    public void ExpertOptionsCannotChangeHintSemantics(string option)
        => Assert.Throws<ArgumentException>(() => PasswordHintPlanner.ConfigureOptions(new() { ExtraArguments = [option] }, true));

    [Fact]
    public void GeneratedWordlistsUseLiteralUtf8RatherThanAutohex()
    {
        var options = new CommonOptions(); PasswordHintPlanner.ConfigureOptions(options, true); PasswordHintPlanner.ConfigureOptions(options, true);
        Assert.Equal(new[] { "--wordlist-autohex-disable" }, options.ExtraArguments);
        Assert.Throws<ArgumentException>(() => PasswordHintPlanner.ConfigureOptions(new() { OptimizedKernel = true }, true));
    }
}
