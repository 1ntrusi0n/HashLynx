# Changelog

All notable changes to HashLynx are documented here. This file follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and uses semantic versions. Material changes to functionality, behavior, architecture, or dependencies require an entry.

## [Unreleased]

### Fixed

- Recovery device choices can now be saved in Hardware and are honored in Basic mode and after restart. The Attack page shows the effective selection; explicit Expert IDs override the saved default. This avoids falling back to a failing automatic GPU choice when a working CPU was only saved in an attack profile.
- Populated Jobs pages now bind progress and other display-only values one-way, avoiding read-only property exceptions during job selection and status updates.
- Explicit backend device IDs now also allow all OpenCL device types, so selecting a CPU is honored even when a GPU is present. The selected IDs still restrict execution; automatic device selection is unchanged.
- Backend failure messages distinguish invalid OpenCL queries, missing runtimes, unavailable devices, and rejected old runtimes while excluding private target and credential data.
- Opening the populated manual hash-mode catalog no longer interrupts page layout or makes the scrollbar disappear. Read-only catalog labels now use one-way bindings, and WPF smoke checks exercise expansion, scrolling, mode selection, and installed-catalog search.

### Added

- Original, embedded Quick (64), Normal (512), Heavy (4,096), and Super (16,384) Dictionary rule presets, with deterministic regeneration and versioned profile identifiers.
- An original 848-word starter list, automatic selection for new configurations, and direct access to an optional local full wordlist.
- Clickable rule help with Hashcat syntax and before/after examples.
- Tests for preset generation, command selection, validation, asset repair, starter-list discovery, and Basic/Expert workflows; optional real Hashcat rule-output validation.

### Changed

- New Dictionary attacks default to **No Rules**. The dropdown also offers Quick, Normal, Heavy, and Super; No Rules passes no rule file to Hashcat. Saved preset/custom-rule choices are preserved, and no-rule profiles load with No Rules selected.
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
