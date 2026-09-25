using HashLynx.Core;

namespace HashLynx.Hashcat;

/// <summary>Interpret sanitized backend diagnostics without exposing their contents in the friendly summary.</summary>
public static class HashcatFailureGuidance
{
    public static JobFailureGuidance For(string? diagnostic)
    {
        var text = diagnostic ?? "";
        bool Contains(params string[] terms) => terms.Any(term => text.Contains(term, StringComparison.OrdinalIgnoreCase));
        if (Contains("did not load any hashes", "No hashes loaded", "does not match the selected hash mode", "Token length", "Separator unmatched", "Select a hash mode", "hash mode is not supported"))
            return new("The target could not be read with the selected hash type. Review the target and choose the matching hash type before trying again.", JobNextAction.ReviewInputs);
        if (Contains("file permission", "Permission denied", "Access denied", "wordlist", "mask file", "rule file", "No such file", "Target does not exist", "Target is not readable", "output or session path"))
            return new("A required file could not be used. Review the target, wordlist or mask, and output location; make sure the files still exist and are accessible.", JobNextAction.ReviewInputs);
        if (Contains("could not start", "could not be started", "executable", "backend changed", "Validate the backend"))
            return new("The recovery backend could not start. Check its location and validate the installation in Settings before trying again.", JobNextAction.CheckSettings);
        if (Contains("OpenCL", "CUDA", "HIP runtime", "compute runtime", "driver", "device IDs", "recovery devices", "No device", "no usable compute device", "available memory", "Hardware", "sample check"))
            return new("The recovery device could not run this attempt. Open Hardware to check available devices and their sample-test results, then select a working device or address its reported driver problem.", JobNextAction.CheckHardware);
        if (Contains("installation"))
            return new("The recovery backend could not start. Check its location and validate the installation in Settings before trying again.", JobNextAction.CheckSettings);
        return new("This attempt could not finish. Review the target and attack settings. Expand Technical details for the recorded error before trying again.", JobNextAction.ReviewInputs);
    }
}
