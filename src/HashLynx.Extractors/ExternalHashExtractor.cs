using System.ComponentModel;

namespace HashLynx.Extractors;

public abstract class ExternalHashExtractor : IHashExtractor
{
    private readonly ExtractorConfiguration configuration;
    private readonly IExtractorProcessRunner runner;
    private readonly string[] candidateTools;

    protected ExternalHashExtractor(ExtractorConfiguration? configuration, IExtractorProcessRunner? runner, params string[] candidateTools)
    {
        this.configuration = configuration ?? new();
        this.runner = runner ?? new ExtractorProcessRunner();
        this.candidateTools = candidateTools;
    }

    public abstract string Id { get; }
    public abstract string DisplayName { get; }
    public abstract string Description { get; }
    public virtual string ImplementationType => "External process adapter";
    public abstract IReadOnlyList<string> SupportedExtensions { get; }

    public async Task<ExtractorAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tool = ToolLocator.Resolve(configuration.ToolPath, candidateTools);
        if (tool is null) return new() { Diagnostic = "Configure the extractor tool or script path. This tool is obtained separately and is never downloaded automatically." };
        var extension = Path.GetExtension(tool).ToLowerInvariant();
        if (extension == ".exe")
            return new() { IsAvailable = ToolLocator.IsSafeExecutable(tool), ToolPath = tool, Diagnostic = ToolLocator.IsSafeExecutable(tool) ? "Executable found. Use Validate to check that it starts." : "Shell executables cannot be configured as extractors." };
        if (extension is not (".py" or ".pl"))
            return new() { ToolPath = tool, Diagnostic = "Use an .exe tool, .py script with Python, or .pl script with Perl." };
        var interpreter = extension == ".py"
            ? ToolLocator.Resolve(configuration.InterpreterPath, "py.exe", "python.exe", "python3.exe")
            : ToolLocator.Resolve(configuration.InterpreterPath, "perl.exe");
        if (interpreter is null || !ToolLocator.IsSafeExecutable(interpreter))
            return new() { ToolPath = tool, Diagnostic = $"The script was found, but a compatible {(extension == ".py" ? "Python 3" : "Perl")} executable is missing. Configure its path." };
        var availability = new ExtractorAvailability { IsAvailable = true, ToolPath = tool, InterpreterPath = interpreter };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var arguments = InterpreterArguments(interpreter).Concat(new[] { "--version" }).ToArray();
            var result = await runner.RunAsync(new(interpreter, arguments), timeout.Token);
            var versionText = result.StandardOutput + "\n" + result.StandardError;
            var version = versionText.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())
                .FirstOrDefault(x => x.StartsWith("Python ", StringComparison.OrdinalIgnoreCase) || x.StartsWith("This is perl", StringComparison.OrdinalIgnoreCase));
            var compatible = result.ExitCode == 0 && (extension != ".py" || version?.StartsWith("Python 3.", StringComparison.OrdinalIgnoreCase) == true);
            return availability with { IsAvailable = compatible, Version = version, Diagnostic = compatible ? "Script and compatible interpreter found." : "The configured interpreter did not report a compatible runtime." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return availability with { IsAvailable = false, Diagnostic = "Interpreter validation timed out." }; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        { return availability with { IsAvailable = false, Diagnostic = "The interpreter could not start. Check its path and access permissions." }; }
    }

    public async Task<ExtractorAvailability> ValidateAsync(CancellationToken cancellationToken = default)
    {
        var availability = await GetAvailabilityAsync(cancellationToken);
        if (!availability.IsAvailable) return availability;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            // The Hashcat script has argparse --help; John tools differ, but invocation without a target prints usage.
            var request = CreateRequest(availability, Id == "bitlocker" ? ["--help"] : []);
            var result = await runner.RunAsync(request, timeout.Token);
            var output = result.StandardOutput + "\n" + result.StandardError;
            var usagePrinted = output.Contains("usage", StringComparison.OrdinalIgnoreCase);
            var failed = result.OutputTruncated || result.ExitCode is not (0 or 1 or 2)
                || !usagePrinted
                || result.StandardError.Contains("Traceback", StringComparison.Ordinal)
                || result.StandardError.Contains("Can't locate", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("ModuleNotFoundError", StringComparison.Ordinal)
                || result.StandardError.Contains("SyntaxError", StringComparison.Ordinal)
                || result.StandardError.Contains("syntax error", StringComparison.OrdinalIgnoreCase)
                || result.StandardError.Contains("compilation failed", StringComparison.OrdinalIgnoreCase);
            return availability with { IsAvailable = !failed, Diagnostic = failed
                ? "Tool validation failed. Check runtime dependencies and that the configured tool is the correct extractor."
                : $"Tool started and returned usage/help (exit {result.ExitCode}). A representative encrypted file is still needed to test extraction." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return availability with { IsAvailable = false, Diagnostic = "Tool validation timed out." }; }
        catch (Exception exception) when (IsExpectedFailure(exception))
        { return availability with { IsAvailable = false, Diagnostic = "Tool validation could not start. Check the configured path and permissions." }; }
    }

    public async Task<bool> CanHandleAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try { return (await FileTypeInspector.InspectAsync(filePath, cancellationToken)).FileType == Id; }
        catch (Exception exception) when (IsExpectedFailure(exception)) { return false; }
    }

    public async Task<ExtractionResult> ExtractAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath)) return Failure("The selected source file does not exist or cannot be accessed.");
            if (!await CanHandleAsync(fullPath, cancellationToken)) return Failure("The file header does not match this extractor. Whole-disk images must first be exported as a BitLocker partition image.");
            var availability = await GetAvailabilityAsync(cancellationToken);
            if (!availability.IsAvailable) return Failure(availability.Diagnostic);
            var result = await runner.RunAsync(CreateRequest(availability, [fullPath]), cancellationToken);
            if (result.OutputTruncated) return Failure("Extractor output exceeded the safety limit. No partial hashes were accepted.");
            var parsed = ExtractorOutputParser.Parse(Id, result.StandardOutput);
            var diagnostics = parsed.Diagnostics.ToList();
            if (result.ExitCode != 0) diagnostics.Add($"The extractor exited with code {result.ExitCode}. No hashes were accepted; validate the tool and file format.");
            if (!string.IsNullOrWhiteSpace(result.StandardError))
                diagnostics.Add("The extractor also wrote diagnostic output. Raw output is kept out of application logs because it may contain sensitive file data.");
            if (Id == "bitlocker" && parsed.Hashes.Count == 0)
                diagnostics.Add("BitLocker extraction supports a user-password protector on a partition image. TPM-only and recovery-key protectors are unsupported.");
            var success = result.ExitCode == 0 && parsed.Hashes.Count > 0;
            return new()
            {
                Success = success, Hashes = success ? parsed.Hashes : [], SourceFileType = Id, ExtractorName = DisplayName,
                SuggestedHashcatModes = success ? parsed.SuggestedHashcatModes : [], Diagnostics = diagnostics,
                Metadata = new Dictionary<string, string> { ["exitCode"] = result.ExitCode.ToString(System.Globalization.CultureInfo.InvariantCulture), ["implementation"] = ImplementationType }
            };
        }
        catch (Exception exception) when (IsExpectedFailure(exception))
        { return Failure("Extraction could not read the file or start the tool. Check the source path, tool configuration, and access permissions."); }
    }

    private ExtractionResult Failure(string diagnostic) => new() { ExtractorName = DisplayName, SourceFileType = Id, Diagnostics = [diagnostic] };

    private static ExtractorProcessRequest CreateRequest(ExtractorAvailability availability, IReadOnlyList<string> arguments)
    {
        if (availability.InterpreterPath is null)
            return new(availability.ToolPath!, arguments, Path.GetDirectoryName(availability.ToolPath!));
        return new(availability.InterpreterPath,
            InterpreterArguments(availability.InterpreterPath).Concat([availability.ToolPath!]).Concat(arguments).ToArray(),
            Path.GetDirectoryName(availability.ToolPath!));
    }

    private static string[] InterpreterArguments(string interpreter) =>
        Path.GetFileName(interpreter).Equals("py.exe", StringComparison.OrdinalIgnoreCase) ? ["-3"] : [];

    private static bool IsExpectedFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or ArgumentException or Win32Exception or NotSupportedException or InvalidOperationException;
}
