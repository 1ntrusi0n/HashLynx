using HashLynx.Core;

namespace HashLynx.Hashcat.Tests;

public sealed class FailureGuidanceTests
{
    [Theory]
    [InlineData("OpenCL could not query device information (CL_INVALID_VALUE).", JobNextAction.CheckHardware)]
    [InlineData("Hashcat has no usable compute device.", JobNextAction.CheckHardware)]
    [InlineData("No device passed the built-in sample check.", JobNextAction.CheckHardware)]
    [InlineData("The backend has insufficient available memory for this job.", JobNextAction.CheckHardware)]
    [InlineData("Hashcat did not load any hashes. Check the selected mode and target format.", JobNextAction.ReviewInputs)]
    [InlineData("A target line does not match the selected hash mode (separator).", JobNextAction.ReviewInputs)]
    [InlineData("Hashcat encountered a file permission error.", JobNextAction.ReviewInputs)]
    [InlineData("Target does not exist or cannot be read.", JobNextAction.ReviewInputs)]
    [InlineData("Wordlist does not exist or cannot be read.", JobNextAction.ReviewInputs)]
    [InlineData("Hashcat could not start. Validate the backend and review the preflight inputs.", JobNextAction.CheckSettings)]
    [InlineData("The backend changed during the hardware check.", JobNextAction.CheckSettings)]
    [InlineData("Hashcat exited with code -1. Check the target, mode, attack settings and backend device availability.", JobNextAction.ReviewInputs)]
    public void KnownFailuresHaveRelevantActions(string message, JobNextAction expected)
        => Assert.Equal(expected, HashcatFailureGuidance.For(message).Action);

    [Fact]
    public void FriendlySummaryNeverEchoesUntrustedDiagnosticContents()
    {
        var guidance = HashcatFailureGuidance.For("Private-sample-hash:PrivateSamplePassword --output=C:\\private-folder\\result.txt");
        Assert.DoesNotContain("Private", guidance.Detail);
        Assert.DoesNotContain("private-folder", guidance.Detail);
        Assert.Equal(JobNextAction.ReviewInputs, guidance.Action);
    }

    [Fact]
    public void SpecificInputFailureWinsOverGenericExitAdvice()
    {
        var guidance = HashcatFailureGuidance.For("Hashcat exited with code -1. Check target, mode and backend device availability.\nA target line does not match the selected hash mode (token length).");
        Assert.Equal(JobNextAction.ReviewInputs, guidance.Action);
        Assert.Contains("hash type", guidance.Detail);
    }
}
