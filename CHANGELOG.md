# Changelog

All notable changes to HashLynx are documented here. This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and uses semantic versions. Material changes to functionality, behavior, architecture, or dependencies require an entry.

## [Unreleased]

### Added

- Original, embedded Quick (64), Normal (512), Heavy (4,096), and Super (16,384) Dictionary rule presets, with deterministic regeneration and versioned profile identifiers.
- An original 848-word starter list, automatic selection for new configurations, and direct access to an optional local full wordlist.
- Clickable rule help with Hashcat syntax and before/after examples.
- Tests for preset generation, command selection, validation, asset repair, starter-list discovery, and Basic/Expert workflows; optional real Hashcat rule-output validation.

### Changed

- Start recovery performs target identification and preflight automatically; ambiguous hashes still require an explicit mode choice.
- Run options, custom rule files, separate Preflight, and command preview are Expert controls. Basic mode uses managed defaults and excludes hidden Expert settings from jobs.
- Legacy profiles preserve their custom/no-rule behavior and reveal advanced settings in Expert mode. Edits during asynchronous launch checks require a fresh Start.
- Large local source rule and wordlist collections are excluded from Git and publish output; the compact original assets ship in the application.

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
