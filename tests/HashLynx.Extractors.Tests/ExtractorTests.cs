using System.Text;
using HashLynx.Extractors;

namespace HashLynx.Extractors.Tests;

public sealed class ExtractorTests : IDisposable
{
    private readonly string temporaryDirectory = Path.Combine(Path.GetTempPath(), "HashLynx-ExtractorTests-" + Guid.NewGuid().ToString("N"));

    public ExtractorTests() => Directory.CreateDirectory(temporaryDirectory);

    [Fact]
    public async Task RegistryUsesHeaderDespiteMisleadingExtension()
    {
        var path = WriteFile("actually-pdf.zip", "%PDF-1.7\n"u8.ToArray());
        var matches = await ExtractorRegistry.CreateDefault().FindCandidatesAsync(path);
        Assert.Equal("pdf", Assert.Single(matches).Id);
    }

    [Theory]
    [InlineData("504B0304", "zip")]
    [InlineData("504B0506", "zip")]
    [InlineData("526172211A0700", "rar")]
    [InlineData("526172211A070100", "rar")]
    [InlineData("377ABCAF271C", "7z")]
    [InlineData("EB58902D4656452D46532D", "bitlocker")]
    public async Task RegistryFindsKnownMagicWithoutExtension(string headerHex, string expected)
    {
        var path = WriteFile("target", Convert.FromHexString(headerHex));
        var matches = await ExtractorRegistry.CreateDefault().FindCandidatesAsync(path);
        Assert.Equal(expected, Assert.Single(matches).Id);
    }

    [Fact]
    public async Task ExtensionAloneDoesNotIdentifyArchive()
    {
        var path = WriteFile("not-a-zip.zip", Encoding.UTF8.GetBytes("ordinary text"));
        Assert.Empty(await ExtractorRegistry.CreateDefault().FindCandidatesAsync(path));
    }

    [Fact]
    public void ArchiveHeaderWinsOverEmbeddedPdfMarker()
    {
        Assert.Equal("zip", FileTypeInspector.Identify("PK\u0003\u0004member.pdf %PDF-1.4"u8));
    }

    [Fact]
    public void OrdinaryFatHeaderIsNotBitLocker()
    {
        var header = new byte[512];
        "MSWIN4.1"u8.CopyTo(header.AsSpan(3));
        Assert.Equal("unknown", FileTypeInspector.Identify(header));
        Convert.FromHexString("3BD66749292ED84A8399F6A339E3D001").CopyTo(header, 0x1a8);
        Assert.Equal("bitlocker", FileTypeInspector.Identify(header));
    }

    [Fact]
    public async Task MissingConfiguredToolIsUnavailableWithoutLaunchingAnything()
    {
        var runner = new FakeRunner();
        var extractor = new ZipHashExtractor(new() { ToolPath = Path.Combine(temporaryDirectory, "absent.exe") }, runner);
        var availability = await extractor.GetAvailabilityAsync();
        var result = await extractor.ExtractAsync(WriteFile("archive.zip", "PK\u0003\u0004"u8.ToArray()));
        Assert.False(availability.IsAvailable);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Empty(runner.Requests);
    }

    [Fact]
    public async Task MissingConfiguredInterpreterIsUnavailable()
    {
        var extractor = new PdfHashExtractor(new()
        {
            ToolPath = WriteFile("pdf2john.py", []),
            InterpreterPath = Path.Combine(temporaryDirectory, "missing-python.exe")
        });
        Assert.False((await extractor.GetAvailabilityAsync()).IsAvailable);
    }

    [Fact]
    public async Task ValidationRejectsPythonSyntaxError()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new(0, "Python 3.14.0", ""));
        runner.Results.Enqueue(new(1, "", "SyntaxError: Missing parentheses in call to 'print'"));
        var extractor = new PdfHashExtractor(new()
        {
            ToolPath = WriteFile("pdf2john.py", []), InterpreterPath = WriteFile("python.exe", [])
        }, runner);
        Assert.False((await extractor.ValidateAsync()).IsAvailable);
    }

    [Fact]
    public async Task ValidationAcceptsUsageFromNativeToolWithNoTarget()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new(1, "", "Usage: zip2john archive.zip"));
        var extractor = new ZipHashExtractor(new() { ToolPath = WriteFile("zip2john.exe", []) }, runner);
        Assert.True((await extractor.ValidateAsync()).IsAvailable);
    }

    [Fact]
    public async Task PythonLauncherUsesVersionThreeAndScriptPathAsSeparateArguments()
    {
        var script = WriteFile("pdf2john.py", []);
        var interpreter = WriteFile("py.exe", []);
        var target = WriteFile("unsafe & name; $(echo).pdf", "%PDF-1.4\n"u8.ToArray());
        var runner = new FakeRunner();
        runner.Results.Enqueue(new(0, "Python 3.14.0", ""));
        runner.Results.Enqueue(new(0, "filename:$pdf$1*2*40*-1*0*16*001122*32*aabb*32*ccdd::::", ""));
        var extractor = new PdfHashExtractor(new() { ToolPath = script, InterpreterPath = interpreter }, runner);
        var result = await extractor.ExtractAsync(target);
        Assert.True(result.Success);
        Assert.Equal(new[] { "-3", "--version" }, runner.Requests[0].Arguments);
        Assert.Equal(new[] { "-3", script, target }, runner.Requests[1].Arguments);
        Assert.Single(result.Hashes);
    }

    [Fact]
    public void ProcessArgumentsAreShellFreeAndPreserveSpecialCharacters()
    {
        var executable = Path.Combine(temporaryDirectory, "zip2john.exe");
        string[] arguments = ["C:\\spaces & symbols\\a;$(echo).zip", "a\"b"];
        var info = ExtractorProcessRunner.CreateStartInfo(new(executable, arguments));
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardOutput);
        Assert.True(info.RedirectStandardError);
        Assert.Equal(arguments, info.ArgumentList);
        Assert.Empty(info.Arguments);
    }

    [Theory]
    [InlineData("cmd.exe")]
    [InlineData("powershell.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("extract.cmd")]
    public void ProcessRunnerRejectsShellsAndBatchFiles(string filename)
    {
        Assert.Throws<ArgumentException>(() => ExtractorProcessRunner.CreateStartInfo(new(Path.Combine(temporaryDirectory, filename), [])));
    }

    [Fact]
    public async Task NonzeroExitRejectsPartialExtraction()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new(1, "file:$7z$0$14$0$$11$abc$123$16$12$abc", "failure"));
        var extractor = new SevenZipHashExtractor(new() { ToolPath = WriteFile("7z2john.exe", []) }, runner);
        var result = await extractor.ExtractAsync(WriteFile("archive.7z", Convert.FromHexString("377ABCAF271C")));
        Assert.False(result.Success);
        Assert.Empty(result.Hashes);
        Assert.Contains(result.Diagnostics, x => x.Contains("code 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TruncatedOutputNeverReturnsPartialHashes()
    {
        var runner = new FakeRunner();
        runner.Results.Enqueue(new(0, "file:$7z$0$14$0$$11$abc$123$16$12$abc", "", true));
        var extractor = new SevenZipHashExtractor(new() { ToolPath = WriteFile("7z2john.exe", []) }, runner);
        var result = await extractor.ExtractAsync(WriteFile("archive.7z", Convert.FromHexString("377ABCAF271C")));
        Assert.False(result.Success);
        Assert.Empty(result.Hashes);
    }

    [Fact]
    public async Task CancellationPropagatesInsteadOfBecomingAFailureResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var extractor = new PdfHashExtractor();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extractor.GetAvailabilityAsync(cancellation.Token));
    }

    [Fact]
    public void DuplicateIdsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => new ExtractorRegistry([new PdfHashExtractor(), new PdfHashExtractor()]));
    }

    private string WriteFile(string name, byte[] contents)
    {
        var path = Path.Combine(temporaryDirectory, name);
        File.WriteAllBytes(path, contents);
        return path;
    }

    public void Dispose() => Directory.Delete(temporaryDirectory, recursive: true);

    private sealed class FakeRunner : IExtractorProcessRunner
    {
        public List<ExtractorProcessRequest> Requests { get; } = [];
        public Queue<ExtractorProcessResult> Results { get; } = new();
        public Task<ExtractorProcessResult> RunAsync(ExtractorProcessRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new(0, "", ""));
        }
    }
}
