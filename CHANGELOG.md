# Changelog

All notable changes to HashLynx are documented here. This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and uses semantic versions. Material changes to functionality, behavior, architecture, or dependencies require an entry.

## [Unreleased]

## [0.1.0] - 2026-09-23

### Added

- .NET 10 Windows WPF solution with separate UI, Core, Hashcat, Extractors, and Persistence projects.
- Branded navy/teal MVVM shell with Attack, Jobs, Results, Hardware, Extractors, and Settings/About navigation; Dark/Light/System themes; multi-size executable icon.
- Target Inspector for pasted hashes, hash files, and encrypted targets; streaming file analysis, Hashcat identification, ambiguous mode selection, and searchable version-cached catalog.
- Dictionary, Mask, Hybrid in both directions, and Combinator configuration with applicable rules, charsets, increment, common/Expert options, command preview, and preflight.
- Shell-free asynchronous Hashcat execution, typed JSON status/device/result parsers, cancellation, persisted job history, restore-file integration, and local recovered-result actions.
- Extensible PDF/ZIP/RAR/7-Zip external extractor adapters and Hashcat-supplied BitLocker adapter, dependency detection, safe invocation, and extractor configuration UI.
- Atomic per-user JSON settings/profiles/jobs, profile save/load/delete, sanitized diagnostic logging, and a managed backend support workspace protecting the original Hashcat release from runtime cache writes.
- xUnit coverage for command safety, parsing, validation, persistence, and extractor behavior; opt-in installed-backend checks and WPF smoke validation; Windows GitHub Actions CI.
- README, MIT license, third-party notices, contribution/security policies, engineering rules, and build/publish script.

### Known limitations

- Windows redirected interactive controls are disabled until verified; Stop works through process-tree cancellation and restore requires an existing Hashcat checkpoint.
- Local Intel OpenCL runtime was rejected by Hashcat v7.1.2, preventing successful cracking/live-control validation on the bootstrap machine.
- External John tools are not bundled; real encrypted-format end-to-end samples were not available during bootstrap. BitLocker starts with password-protected partition images at offset zero.
