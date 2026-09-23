# HashLynx engineering rules

- Always run `dotnet build -c Release` before declaring work complete, and run relevant tests after changes. Full bootstrap/release validation includes `dotnet restore` and `dotnet test -c Release`.
- Update CHANGELOG.md for material behavior, architecture, dependency, and user-visible changes. Update README.md when setup or user-visible behavior changes.
- Never modify, reformat, delete, or replace the user's local `hashcat/` release. Never commit it or any downloaded backend/extractor binaries. Use a disposable copy for integration tests that could create backend cache/session files.
- Keep the user's root `rules/` and `wordlist/` source collections local and unmodified. Built-in presets come from original versioned recipes in `RulePresetRecipes.cs`; regenerate their embedded assets with `scripts/HashLynx.RulePresetGenerator` and retain old version IDs when adding changed recipes.
- Keep process execution shell-free: `ProcessStartInfo.UseShellExecute = false` and `ArgumentList`. Preview text is presentation only. Never execute it as a shell command.
- Preserve MVVM: business logic belongs in Core and integration services; WPF code-behind is limited to view lifecycle and interaction.
- Keep Hashcat-specific options, mode IDs, discovery, parsing, and runtime behavior in HashLynx.Hashcat, not the UI.
- Keep extractor integrations behind `IHashExtractor`. External tools are separate processes and retain their licenses.
- Add meaningful tests for command generation, validation, parser, and extractor changes. Normal tests must not require Hashcat or a GPU; opt-in integration checks must be labeled.
- Preserve third-party license notices. Do not copy GPL extractor implementations into the MIT application.
- Never log recovered plaintext credentials, entire hash files, target values, candidates, or raw commands. Diagnostic events should use sanitized summaries.
- Never silently download or execute third-party tooling. Run configured tools only for the user's requested validation, extraction, or recovery workflow. Do not elevate or change Windows security settings.
- The user-authorized `experiment/bitlocker-drive-reader` feature has one elevation exception: the bundled `HashLynx.DriveReader` helper, launched for an explicitly selected volume through the Windows UAC prompt. Its fixed-executable `ShellExecuteExW` launcher uses only validated pipe-name and PID arguments; it never invokes a command shell. Keep Hashcat and the main app unelevated. Device access must be `GENERIC_READ`, bounded to the chosen partition and metadata budget; never unlock, dismount, alter protectors, or write device data. Live tests require an explicitly identified user test volume. Publish this branch separately and keep its history in the experimental data directory.
- Keep application data under LocalApplicationData. Preserve corrupt/unsupported settings and report the problem instead of overwriting them. Avoid breaking existing profiles/settings without migration handling.
- Use cancellation and asynchronous I/O for long operations. Do not block the WPF dispatcher. Handle process errors and absence of dependencies as actionable UI states.
- Do not add telemetry or external transmission of targets, credentials, encrypted files, wordlists, or job data.
- Build for .NET 10 / `net10.0-windows`. Prefer native WPF and BCL components over large dependencies.
