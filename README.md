<p align="center"><img src="assets/branding/logo_withText.png" alt="HashLynx" width="300"></p>

# HashLynx

**Experiment branch: `experiment/recovery-workflows`.** This build adds password hints, attack sequences, hardware checks, completion feedback, wordlist tools, and Office/KeePass extraction. It has a separate app title, data workspace, and publish folder. It is not merged into `main`. See [testing the experiment](docs/recovery-workflows.md).


A local Windows desktop workspace for **Hashcat**: inspect recovery targets, configure attacks, monitor jobs, and review results from one native WPF interface.

**Status: 0.1.0 — early functional bootstrap.** HashLynx orchestrates the official Hashcat executable; it does not implement a cracking engine. It is intended for legitimate password recovery, authorized security auditing, research, labs, and CTFs.

HashLynx is an independent GUI frontend. Hashcat is a separate third-party project, is not developed by HashLynx, and does not sponsor or endorse this application. Users must separately obtain and configure Hashcat. This repository and its publish output do not bundle it.

## What is implemented

- Native C#/.NET 10 WPF shell with MVVM, the supplied branding, a multi-size Windows icon, navy/teal styling, and Dark/Light/System preferences.
- **Target Inspector** with Paste Hash, Hash File, Encrypted File, and BitLocker Drive inputs; file browsing and drag/drop; source/context selection; Hashcat identification; and a searchable manual mode catalog generated from the installed release.
- Password hints with candidate previews, fixed beginning/ending patterns, remembered-word variations, and persistent sequential attack queues.
- Dictionary, Mask, both Hybrid directions, and Combinator configurations. Dictionary attacks include original Quick, Normal, Heavy, and Super rule presets, an 848-word starter list, and clickable rule examples. Expert mode supports custom rule files and multiple wordlists.
- Mask/custom charset validation, increment settings, workload/device/temperature controls, named sessions, potfile/output options, and a constrained Expert argument extension field.
- Automatic validation when starting recovery, with run options, a copyable command preview, and a separate Preflight button in Expert mode. Commands execute with `ProcessStartInfo.ArgumentList`, never through a shell.
- Asynchronous jobs, parsed JSON status metrics, stop, job history, interrupted-job detection, and restore when a saved Hashcat restore file exists.
- Session-specific recovered results, an all-sessions view, plaintext hide/reveal, copy, and export. Completion banners link to results; optional Windows notifications keep passwords hidden. Hardware discovery includes a bounded known-answer recovery test per device.
- An extensible extractor registry for PDF, ZIP, RAR, 7-Zip, BitLocker, Microsoft Office, and KeePass. All seven formats are built in and need no extractor configuration.
- Wordlist library names, cached background line counts, missing-file repair, and cancellable combine/deduplicate/byte-length filtering into new files.
- Attack profile save/load/delete, per-user JSON persistence with atomic writes, sanitized structured diagnostics, and a Windows build/test workflow.

## Target Inspector

Hashcat's `--identify` is the primary authority. A unique match is selected automatically; ambiguous matches are displayed for explicit selection. Fixed-length hexadecimal input can represent MD5, NTLM, and other algorithms, so length alone never establishes a certain type. Context is descriptive guidance and does not override the backend's candidates.

For hash files, a streaming scan counts total, blank, candidate, and problem lines, with bounded structural grouping and problem-line summaries. These structural checks do not prove that every line is a valid hash for the selected mode. The original file is never cleaned or modified in place.

Paste and extracted targets are saved as local working target files so child-process arguments do not need to contain the hash itself. Catalog parsing is isolated and cached by backend version. An older backend without optional identification or JSON-status support can still be configured; unsupported functionality produces a clear message.

## Requirements and setup

- Windows 10/11 with the **.NET 10 SDK** for development. A framework-dependent published application requires the .NET 10 Desktop Runtime.
- A complete official [Hashcat release](https://hashcat.net/hashcat/), obtained separately. A functioning compute backend/driver is required for actual recovery; merely listing a device does not guarantee that it can run a kernel.
- Optional external extractor tools and their required Python/Perl runtimes. HashLynx does not install or download these dependencies.

Clone and extract Hashcat into the exact layout below:

```powershell
git clone https://github.com/1ntrusi0n/HashLynx.git
cd HashLynx
# Download the official release yourself and extract its contents to .\hashcat\
```

```text
HashLynx/
  HashLynx.sln
  hashcat/
    hashcat.exe
    OpenCL/
    modules/
    rules/
    tools/
    ...the rest of the official release...
```

Keep the full release together. `hashcat/` is Git-ignored. HashLynx detects a development-tree `hashcat\hashcat.exe` and an application-adjacent `hashcat\hashcat.exe`. A manually configured directory overrides automatic discovery. If absent, the app opens Settings with a friendly setup message. Use **Detect Hashcat** or select the folder, then **Validate Hashcat**.

On Windows, Hashcat can create caches even during help/identification. HashLynx prepares a per-user backend workspace containing support assets and runs the configured original executable there. This protects the supplied release from cache/session writes. First validation needs time and disk space to copy support assets; later validations reuse that workspace. HashLynx does not download or replace Hashcat.

The workspace cache follows the executable's identity. If you update support assets without replacing the executable, close HashLynx and remove only `%LOCALAPPDATA%\HashLynx\cache\backend\` to rebuild it on the next validation. Never remove your original Hashcat release or job/results directories for this refresh.

## Build and run

```powershell
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project src/HashLynx.UI
```

Open the solution in Visual Studio or the repository folder in VS Code with C# support. The same commands work in VS Code's integrated terminal. The [Microsoft WPF documentation](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100) describes the .NET 10 platform used here.

To publish a framework-dependent x64 application:

```powershell
dotnet publish src/HashLynx.UI -c Release -r win-x64 --self-contained false -o artifacts/publish/recovery-workflows-test
```

Or run `./scripts/build.ps1 -Publish`. Copy the publish directory as a unit. Obtain Hashcat separately and put its complete release alongside the application under `hashcat\`, or configure its existing location in Settings. Runtime dependencies are not silently installed.

## Recovery workflow

For this experiment, launch `artifacts/publish/recovery-workflows-test/HashLynx.exe`. The app uses `%LOCALAPPDATA%\HashLynx\Experiments\RecoveryWorkflows`. On first launch it copies normal-app preferences and saved wordlist references when readable, redirects output to the experimental workspace, and leaves normal history/results separate.

Expand **What do you remember?** on Attack to enter words or a fixed beginning/ending with a total length range. Preview examples and candidate counts, then add the attempts to Queue. Or use **Add current attack to queue** / **Queue No Rules, Quick, Normal** beneath Start recovery. Start the queue explicitly; it runs one step at a time, stops a sequence when its target is fully recovered, and pauses on failure or restart. See [queue behavior and testing](docs/recovery-workflows.md).

1. Add a pasted hash, hash file, or encrypted file in Target Inspector.
2. For a Dictionary attack, use the included starter wordlist or choose your own. **No Rules** is selected by default, so words are tried unchanged. Choose a preset if you want variations.
3. Press **Start recovery**. HashLynx identifies the target when needed and checks the configuration automatically. If several hash modes match, choose the correct mode and press Start again.
4. On **Jobs**, select the session and click **View recovered passwords**. HashLynx opens its recovered **Hash** and **Password** table and reveals the passwords you requested. Select a row to copy its password, or export the results to CSV.

Selecting a session directly on **Results** loads its results automatically, with passwords initially hidden. Use **Show passwords** to reveal or hide them; **Refresh results** checks for additional recoveries from an ongoing job.

Each session shows only passwords written to its own output file. Hashcat's shared potfile is a cache of previously known passwords, so its matches are not assigned to later sessions. If a session has no output, it shows no recovered rows; results can still be read without the original target or an installed backend. Missing output files cannot be reconstructed with reliable session ownership from the shared cache.

Check **Show All Sessions Results** to combine results from all sessions currently in history. The **Session** column identifies each row's source; CSV exports include the session name and ID. Uncheck it to return to the selected session. New attacks require a separate, unused output file; restore continues using the original session's file. Ambiguous legacy output files shared by multiple sessions are excluded with a message instead of assigning their passwords to both sessions.

To clean up history, select an inactive session on **Jobs** and click **Delete session**. **Undo delete** restores removed entries while HashLynx remains open. Deletion removes the history entry only: target, output, checkpoint, and potfile data remain on disk. Running and paused sessions cannot be deleted.

Basic mode manages devices, session names, result paths, and other run settings. Enable **Expert mode** to select custom rule files, adjust run options, or use the separate Preflight and command-preview controls. Existing profiles with custom rules or advanced options open Expert mode so their saved behavior remains visible. Mask, Hybrid, and Combinator attacks remain available for users who need them.

### Built-in rules and wordlists

A wordlist supplies starting words; rules transform each word into password candidates. For example, the rule `c$1$!` turns `river` into `River1!`. Click **?** beside the rule preset for an explanation and more examples.

| Preset | Rules per word | Added coverage |
| --- | ---: | --- |
| No Rules (default) | 0 | Original words, without transformations |
| Quick | 64 | Common case changes, short numbers, symbols, and substitutions |
| Normal | 512 | Two-digit endings, years, and simple prefixes |
| Heavy | 4,096 | Three-digit endings, more combinations, and selected position changes |
| Super | 16,384 | Four-digit endings, broader prefixes, and paired substitutions |

Each tier contains the smaller tiers and uses one rule file. These are effort budgets, not measured recovery-rate rankings; candidates can coincide, and larger tiers take more work. With the 848-word starter list, Normal applies 434,176 rules in total. See [preset design and regeneration](docs/rule-presets.md).

The starter list is an original, small educational list embedded in the app (about 6 KB). A larger list appropriate to the target will often be more useful. **Add wordlists…** accepts multiple local files and remembers their locations. Dropped wordlists are remembered too. On later runs, choose a **Saved wordlist** to use it immediately. Basic mode uses one chosen list; Expert mode appends selections to its ordered attack inputs. Combinator's left/right selectors also offer saved paths. **Remove from attack** only changes the current attack; **Forget saved list** removes its library entry without deleting the source file or changing the current attack. Missing files stay listed and produce a warning when selected; add their new location or reconnect the drive. The library stores references, not copies of wordlist contents. Expand **Manage wordlist library** for names, counts, repair and new-file transformations; see [wordlist tools](docs/wordlist-tools.md).

When `wordlist/HashLynx_Wordlist.txt` exists beside the app or in the development checkout, **Use local full list** selects and remembers it. The supplied combined list is about 506 MiB and 47.4 million lines; it stays local and is not copied into the repository or publish output. The root `rules/` and `wordlist/` source collections are Git-ignored.

The application never adds `--force`. Backend warnings and driver incompatibilities must be resolved normally. Expert arguments are restricted to an explicit set of additional options; they cannot replace managed target/mode/session/output settings or enable network features.

If jobs immediately fail with a compute-runtime or "no usable device" error, see [compute troubleshooting](docs/compute-troubleshooting.md). Installation validation and device enumeration do not prove that a device can run recovery kernels. In **Hardware**, refresh devices and choose **Use this device by default** on the CPU or GPU you want to use. The saved choice applies immediately to new Basic attacks and survives restart. The Attack page shows the effective selection. Explicit Expert device IDs override the saved default; leaving them blank uses it. **Use automatic selection** in Hardware clears the preference. Refresh and reselect after runtime or hardware changes because device IDs can change.

With the verified Hashcat 7.1.2 mappings, rule files apply to Dictionary; Hybrid and Combinator expose inline left/right rules. Dictionary loopback requires a rule file or preset. Expert custom rule files replace the preset; selecting several custom files causes Hashcat to combine their transformations multiplicatively. Choose **No Rules** in Basic or Expert mode to run an unmodified dictionary. Expert arguments use one separated argument per line, for example `--runtime=60`; the preview remains read-only.

Pause/resume/checkpoint controls are disabled unless the backend's interactive transport is known to work. This Windows bootstrap does not claim verified console-key control through redirected pipes. **Stop** terminates the child process tree. Resume after exit is available only if Hashcat already wrote a usable restore file; stopping cannot guarantee a fresh checkpoint. Job history records completion, exhaustion, interruption, failure, and stop outcomes separately.

## Extractors

All seven supported format families are built in and selected through Target Inspector. The Extractors settings page is hidden when no external adapters need configuration. Legacy tool paths for built-in formats remain stored but no longer override native extraction.

| Family | Adapter | Required separately |
| --- | --- | --- |
| PDF | Built-in Standard password encryption (RC4/AES) | None |
| ZIP | Built-in ZipCrypto and WinZip AES | None |
| RAR | Built-in RAR3/RAR5 | None |
| 7-Zip | Built-in AES with Copy/LZMA/LZMA2/Deflate | None |
| BitLocker | Built-in user-password protector reader | None |
| Microsoft Office | Built-in encrypted OOXML Standard/Agile profiles | None |
| KeePass | Built-in password-only KDBX 3/4 profiles | None |

Office covers supported AES-encrypted DOCX/XLSX/PPTX profiles; KeePass covers password-only KDBX profiles. Legacy Office encryption, vault key files and additional factors are unsupported. KDBX 4 AES-KDF requires mode 34301, which is absent from the tested Hashcat 7.1.2 release. See [format coverage, research and fixtures](docs/extractor-expansion.md).

Header inspection and extension checks guide selection. Adapter results normalize supported John output into hash strings and feed the normal identification workflow. Suggested modes are hints, not a replacement for Hashcat identification. External tool output formats may differ by release; unsupported or failed output is diagnosed rather than presented as successful extraction.

PDF supports Standard Security Handler revisions 2 through 6: RC4-40, RC4-128, AES-128 and AES-256, targeting the document open password. It reads classic, stream and hybrid cross-references, including incremental updates, without Python or John. Documents up to 64 MiB are supported; certificate encryption, unusual key lengths and unsupported metadata encodings produce an explanation. See [PDF coverage and limits](docs/extractors.md#built-in-pdf-extraction).

ZIP works immediately without configuration. It supports stored/deflated ZipCrypto, AES-128/192/256, ZIP64 and data descriptors. It selects the smallest supported encrypted member; recovery verifies that member, since other members may have different passwords. Full-data limits are 320 KiB per ZipCrypto member and less than 8 MiB of AES ciphertext. Target Inspector retains extraction notes and selects a uniquely suggested mode only after Hashcat confirms it.

RAR and 7-Zip also work without configuration. RAR supports RAR3 encrypted headers and stored/compressed members, and RAR5 password verifiers. 7-Zip supports encrypted headers, compressed metadata, and ordinary or solid encrypted streams using Copy, LZMA, LZMA2 or Deflate. They read archives locally without unpacking files. Unsupported variants produce diagnostics. To use another extractor, run it separately and import its Hashcat-compatible output through Hash File. See [format coverage and limits](docs/extractors.md).

BitLocker can read a connected volume through **BitLocker Drive > Refresh drives > Extract and analyze (admin)**. Approve the UAC prompt for the bundled read-only helper; the main app and Hashcat remain unelevated. Keep the complete application folder together, including `DriveReader/`. See [drive extraction and limits](docs/bitlocker-drives.md).

BitLocker also reads raw Windows 7+ partition images with a user-password protector, including BitLocker To Go and used-space-only layouts. It needs no Python or external script. It emits authenticated type-1 records for mode 22100 and can use a backup when primary metadata is damaged. It does not mount or decrypt disks. TPM/PIN protectors, recovery-password cracking, Vista metadata and automatic whole-disk partition discovery are unsupported. See [extractor details](docs/extractors.md) and [third-party notices](THIRD_PARTY_NOTICES.md).

## Privacy, data, and diagnostics

The application works locally/offline. It has no telemetry and does not upload hashes, recovered passwords, encrypted files, wordlists, or results. Configured external tools run locally with ordinary user permissions.

This experiment stores mutable data under `%LOCALAPPDATA%\HashLynx\Experiments\RecoveryWorkflows\`:

| Location | Contents |
| --- | --- |
| `settings.json` | Paths and UI/default preferences |
| `profiles.json` | Saved attack configuration, without target contents or recovered results |
| `wordlists.json` | Saved wordlist file locations; no wordlist contents |
| `queue.json`, `queue/` | Saved sequences, target snapshots and per-sequence potfiles |
| `hints/` | Locally generated candidate files from remembered words |
| `jobs.json` | Job configuration, lifecycle, exit codes, and status snapshots |
| `targets/` | Working copies of pasted/extracted targets |
| `jobs/` | Managed job/session state and fallback per-job outputs |
| `results/` | Default result files and potfile |
| `cache/` | Versioned mode catalog and backend support workspace |
| `rule-presets/` | Materialized, versioned built-in rule files |
| `wordlists/` | Materialized bundled starter wordlist |
| `logs/` | Sanitized JSON diagnostic events |

These files are **not encrypted by HashLynx**. Result files and potfiles contain recoverable credentials; hexadecimal output is encoding, not encryption. Protect them using your normal Windows account and disk controls. Data is retained for job recovery/history until you remove it. Review exports before sharing. Diagnostic logs exclude raw command lines, target values, recovered plaintext, and candidate data. Invalid saved JSON is preserved and reported rather than silently overwritten.

## Architecture

| Project | Responsibility |
| --- | --- |
| `src/HashLynx.UI` | WPF views, view models, navigation, themes, dialogs, application composition |
| `src/HashLynx.Core` | Domain/job/profile models, mask validation, streaming file inspection |
| `src/HashLynx.Hashcat` | Discovery/capabilities, command construction, preflight, process execution, status/catalog/device/result parsers |
| `src/HashLynx.Extractors` | `IHashExtractor`, registry, native PDF/ZIP/RAR/7z/BitLocker readers, header inspection, external adapters, dependency checks |
| `src/HashLynx.Persistence` | Per-user paths, atomic JSON stores, structured logging |
| `tests/` | xUnit unit tests, opt-in backend integration checks, WPF smoke harness |
| `assets/branding` | PNG resources and multi-size ICO |
| `assets/wordlists` | Original bundled starter wordlist |
| `docs/`, `scripts/` | Integration notes and build/validation tools |

Attack family identifiers are strings; backend mode IDs are obtained from installed help and kept in the integration layer. Extractors are selected through a registry, allowing new formats without file-type logic in the GUI. There is no webview shell or network service.

## Validation and current limits

Normal tests and Windows CI need neither Hashcat nor a GPU. An optional installed-backend check exercises discovery, catalog, ambiguous identification, and `--show` using generated synthetic data:

```powershell
$env:HASHLYNX_TEST_HASHCAT = (Resolve-Path .\hashcat\hashcat.exe).Path
dotnet test tests/HashLynx.Integration.Tests -c Release --filter Category=Integration
```

This is an integration check, not a claim of recovery support on every driver. Local validation used Hashcat **v7.1.2**. Its initial Intel runtime failed; after an explicitly approved Intel CPU runtime 2026.0 installation, known-answer MD5 and NTLM CPU recovery passed, including the WPF Start → Jobs → Results flow. The legacy GPU and interactive controls remain unverified. No warning-suppression flags were used. Native ZIP extraction, Hashcat identification, CPU recovery, and session result reading also passed for real 7-Zip-generated stored/deflated ZipCrypto and AES-128/192/256 fixtures.

The same extraction → identification → CPU recovery → session password checks passed for six RAR cases (RAR3 stored/compressed members and encrypted headers; RAR5 encrypted headers and file verifiers with CRC32/BLAKE2 file checksums) and six 7z cases (encrypted headers, Copy/LZMA/LZMA2/Deflate, and a multi-file solid stream with compressed metadata). Broader archive-producer and hardware coverage is still needed. See [archive test instructions](docs/extractors.md) for the optional encrypted-archive checks.

Native BitLocker output also matched Hashcat's reference script, and identification, CPU recovery and session result reading passed using synthetic partition metadata around the public mode-22100 self-test vector. Direct Windows-volume extraction and mode identification passed on the user-selected BitLocker test device; the user subsequently confirmed the module worked successfully.

Run `dotnet run --project tests/HashLynx.UI.Smoke -c Release -- artifacts/ui-smoke` for isolated missing-backend startup, all page/template loading, attack/input/theme layouts, Basic/Expert control visibility, bundled content, legacy/custom profiles, populated manual-catalog expansion/selection/scrolling, and WPF binding checks. With `HASHLYNX_TEST_HASHCAT` set, it also checks catalog search, automatic validation, and preset command generation. Screenshots and the pass/fail report stay under the ignored artifacts directory. CI also runs this harness.

To additionally exercise a real, bounded NTLM recovery with the starter list and Normal preset, set `HASHLYNX_TEST_DEVICE` to a current device ID before running the connected WPF smoke test. The check saves that device through Hardware, then runs Start → Jobs → Results in Basic mode without loading a profile. This opt-in check uses a public known-answer fixture and isolated application data; it requires a working compute runtime.

Status, ETA, temperatures, and device utilization are shown only when supplied by Hashcat. Candidate strings are deliberately omitted from persisted monitoring data to reduce secret duplication. Large result sets currently load into memory when refreshed; very large recovery outputs may need a future paged results viewer. File structural analysis is bounded and best-effort. The first release does not include installers, automatic updates, automatic dependency downloads, or new attack families beyond those listed above.

## Screenshots

Screenshot placeholder: capture the Attack, Jobs, and Results pages after configuring a local test installation. Use synthetic inputs and redact personal paths before adding screenshots to `docs/` or issues. The WPF smoke harness writes local screenshots under ignored `artifacts/` for review.

## License and contributions

HashLynx is licensed under [MIT](LICENSE). External software retains its own licenses; see [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). See [CONTRIBUTING.md](CONTRIBUTING.md), [AGENTS.md](AGENTS.md), and [CHANGELOG.md](CHANGELOG.md) for development expectations. Report vulnerabilities according to [SECURITY.md](SECURITY.md); never put real hashes/passwords/secrets in public issues.
