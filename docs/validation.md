# Validation

## Recovered passwords and session history actions — 2026-09-23

- Jobs provides a View recovered passwords action and a completion message. Connected WPF smoke verifies that the action opens the matching session, loads the hash/password pair, and reveals the requested password; direct session selection remains masked initially. The session dropdown displays the job name.
- History deletion and Undo persist across store reloads, preserve output files, and reject running/paused sessions. The smoke checks deletion of the last entry, selection cleanup, rollback when the history cannot be written, and restoration with Undo. No real user session was deleted during validation.
- Result loads cancel when selection changes. Tests verify that clearing/deleting a selected session removes its results and disables stale clipboard actions, including while loading is in flight.
- An actual Basic-mode NTLM recovery on CPU device 3 passed through Start → Jobs → View recovered passwords, showing the expected public fixture password. Hiding the password again also passed.
- Release build passed with zero warnings/errors. All 166 standard tests passed, with three opt-in backend tests skipped in that run; the connected WPF checks and real recovery passed separately with zero binding errors.

## Optional rules and No Rules default — 2026-09-23

- New Dictionary configurations select No Rules before Quick, Normal, Heavy, and Super. No Rules stores a null preset ID and an empty custom rule list; no backend preset assets or IDs changed.
- Connected WPF smoke passed: default selection; No Rules profile save/load; preservation of an explicitly saved Heavy preset and legacy custom rules; no-rule legacy profiles; Hashcat command preview omits `--rules-file` for No Rules, includes the explicitly selected Normal preset, and removes it when switching back.
- Release build passed with zero warnings/errors. All 166 standard tests passed; three opt-in backend tests were skipped in that run. The connected WPF smoke passed separately with zero binding errors.

## Saved recovery device in Basic mode — 2026-09-23

- The follow-up failed job had an empty device list and therefore used Hashcat's automatic selection, which still failed on the legacy GPU. The earlier working CPU choice existed only in a profile and was not applied to that job.
- Hardware now saves a recovery default. WPF regressions verify the actual device button binding, persistence, immediate display updates, Basic mode ignoring hidden Expert IDs, explicit Expert overrides, blank Expert IDs inheriting the default, restart persistence, and clearing the preference.
- A real WPF Start → Jobs → Results run in Basic mode passed on CPU device 3 with the starter list and Normal preset, without loading a profile or using Expert device settings. The known NTLM fixture was recovered and result masking verified.
- Release build passed with zero warnings/errors; 166 standard tests passed (18 Core, 89 Hashcat, 42 Extractors, 17 Persistence), with three opt-in backend tests skipped in that run. The connected WPF smoke, including real CPU recovery, passed separately with zero binding errors.

## Immediate job failures and Jobs display — 2026-09-23

- Direct synthetic recovery attempts reproduced the reported failure outside the GUI: default device selection returned `clGetDeviceInfo(): CL_INVALID_VALUE` and no usable devices; CPU-only selection additionally rejected Intel OpenCL runtime `5.2.0.10094`. Both exited with Hashcat error `-1` before recovery.
- The separate Jobs UI exception was fixed by making progress/display bindings explicitly one-way. A new populated-Jobs smoke test verifies progress at 25%, updates to 75%, then renders a failed job and its specific backend diagnostic with zero binding errors.
- Explicit device-ID commands now enable OpenCL types `1,2,3` while retaining the exact requested IDs. Command tests cover CPU/GPU-style IDs, multiple IDs, unchanged automatic defaults, and rejected overrides.
- Normal suite: 165 passed (18 Core, 89 Hashcat, 42 Extractors, 16 Persistence); three opt-in backend tests skipped. Release build completed with zero warnings/errors. Driver diagnostic tests also verify that private input appended to backend messages is never echoed.
- With explicit user approval, Intel CPU OpenCL runtime 2026.0 was downloaded from Intel, verified with valid Intel Authenticode signatures on both EXE and MSI, and installed successfully (exit 0, no restart requested). Hashcat reports the new CPU as device 3 with runtime `2026.21.3.0.31_160000`; the legacy GPU still reports its original error.
- A direct known-answer MD5 recovery succeeded on the new CPU. An opt-in WPF run then recovered the known NTLM fixture through Start → Jobs → Results using the 848-word starter and Normal preset. Exit code, recovered plaintext, default result masking, and the populated Jobs page were verified. This validates CPU recovery on this machine, not the legacy GPU or interactive pause/checkpoint controls. See [compute troubleshooting](compute-troubleshooting.md).
- The strict installed-Hashcat candidate test also passed on CPU device 3: Quick, Normal, Heavy, and Super emitted exactly 64, 512, 4,096, and 16,384 candidates for the fixture. Representative case, substitution, numeric, insertion, deletion, prefix, and suffix transformations were verified. The harness uses `--stdout --outfile` because redirected stdout alone produced no candidate output on this host. This confirms rule behavior, not comparative recovery effectiveness.

## Manual catalog layout fix — 2026-09-23

- Reproduced the reported frozen page by expanding a catalog with 600 synthetic modes: WPF threw a `XamlParseException` because `Run.Text` defaulted to a two-way binding against the read-only `HashMode.DisplayName` property.
- Explicit one-way catalog-label bindings fixed the exception. Expanded-catalog smoke checks passed at 1,380- and 1,040-pixel window widths, including visible page scrollbars, page scrolling, mode selection, and collapse/reopen.
- Connected smoke passed against the installed Hashcat catalog, including typing `NTLM` into the search field. The existing recovery-configuration and results smoke checks also passed with zero binding errors.
- Release solution build and Windows publish succeeded with zero build warnings/errors. The harness now reports dispatcher exceptions as test failures instead of leaving a Windows crash dialog open.

## Presets and simplified workflow — 2026-09-23

- Solution restore and Release build succeeded with zero warnings/errors. The deterministic rule generator is included in the solution build.
- Normal unit suite: 145 passed (18 Core, 69 Hashcat, 42 Extractors, 16 Persistence). Three installed-backend tests are opt-in and skipped without their environment variables.
- Installed Hashcat v7.1.2 loaded all four original presets through `--total-candidates` and returned exactly 64, 512, 4,096, and 16,384 for one synthetic input word. This confirms parser acceptance and rule budgets without a compute kernel.
- Expanded WPF smoke passed: bundled starter selection; four presets with Normal default; Basic/Expert visibility; hidden Expert options excluded from Basic configurations; legacy custom/no-rule profile preservation; unsupported profiles rejected before editing; and zero binding errors.
- Connected WPF smoke passed: Start identifies an ambiguous synthetic target and blocks launch until a mode is selected; Dictionary preflight includes the preset; Mask preflight and masked/revealed synthetic results still work. Help text was reviewed at normal and narrow widths and now wraps correctly.
- Framework-dependent `win-x64` publish succeeded. The four rule assets total 172,617 bytes and the 848-word starter is 5,877 bytes, embedded in the app assemblies. Large local source collections and third-party binaries are excluded.

At this stage, actual `--stdout` candidate verification was blocked by the old Intel runtime and was not counted as a pass. The later CPU runtime installation enabled both strict candidate validation and synthetic recovery, documented above. No comparative recovery-effectiveness benchmark was performed. Original Hashcat and source-corpus files were not changed.

## Bootstrap — 2026-09-23

Validated on Windows 10 with .NET SDK 10.0.400 and the user's separately installed Hashcat v7.1.2.

### Completed checks

- Solution restore and Release build: successful, zero warnings and errors.
- Normal xUnit suite: 131 passed (18 Core, 60 Hashcat, 42 Extractors, 11 Persistence). The installed-backend integration test is explicitly skipped without `HASHLYNX_TEST_HASHCAT`.
- Opt-in installed-backend integration: passed using a disposable release copy and generated synthetic inputs. Checked version/capabilities, a catalog with more than 100 modes, ambiguous MD5/NTLM identification, catalog cache reuse, and `--show` with a synthetic potfile.
- WPF smoke: passed all six page templates, four attack-family layouts, three target input layouts, three themes, narrow window layout, missing-backend setup, profile save/load/delete, preflight error handling, and zero binding errors.
- Connected WPF smoke: passed actual identification/manual mode selection, mask preflight/preview, result loading, default plaintext masking, explicit reveal, and target invalidation.
- Actual application startup: launched successfully, stayed responsive, connected to Hashcat, and closed cleanly.
- Framework-dependent `win-x64` publish: successful. Backend/extractor binaries are not included.
- Original Hashcat file verification: all 3,100 original file contents matched the recorded SHA-256 baseline. An early Hashcat probe created an empty `hashcat/kernels/` directory; no original release file was changed. Later probes use managed runtime workspaces.

### Evidence boundaries

The available Intel OpenCL runtime reported `CL_INVALID_VALUE`; CPU recovery was rejected as an outdated/broken runtime (5.2.0.10094), and GPU recovery reported no usable devices. No `--force` flag or driver/security modification was used. Successful cracking, real device-status streaming during an attack, and Windows interactive pause/resume/checkpoint controls could not be validated on this hardware. Controls requiring an unverified interactive transport remain disabled.

Process-lifecycle tests use an explicitly test-only console executable to verify real child-process argument boundaries, concurrent stdout/stderr handling, JSON events, sanitized diagnostics, exit-state mapping, cancellation, and repeated Stop calls. They validate HashLynx orchestration, not a cracking engine.

BitLocker availability and script usage validation passed with the existing Hashcat script and Python 3.14.3. PDF, ZIP, RAR, and 7-Zip dependencies were absent and correctly reported unavailable. No real encrypted-file extraction fixtures were supplied, so format-specific end-to-end extraction remains to be checked with configured external tools and authorized samples.

The GitHub Actions workflow is included; this local report does not claim a remote CI run result. Local screenshots and private test artifacts remain in the ignored `artifacts/` directory.
