# Validation

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

Actual `--stdout` candidate verification was attempted but the local Intel runtime reported `CL_INVALID_VALUE` and produced no candidates. The strict output test remains available through `HASHLYNX_TEST_HASHCAT_CANDIDATES` on a working compute runtime; it was not counted as a pass. No recovery-effectiveness benchmark or successful cracking result is claimed for these presets. Original Hashcat and source-corpus files were not changed.

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
