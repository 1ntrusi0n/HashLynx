# Bootstrap validation — 2026-09-23

Validated on Windows 10 with .NET SDK 10.0.400 and the user's separately installed Hashcat v7.1.2.

## Completed checks

- Solution restore and Release build: successful, zero warnings and errors.
- Normal xUnit suite: 131 passed (18 Core, 60 Hashcat, 42 Extractors, 11 Persistence). The installed-backend integration test is explicitly skipped without `HASHLYNX_TEST_HASHCAT`.
- Opt-in installed-backend integration: passed using a disposable release copy and generated synthetic inputs. Checked version/capabilities, a catalog with more than 100 modes, ambiguous MD5/NTLM identification, catalog cache reuse, and `--show` with a synthetic potfile.
- WPF smoke: passed all six page templates, four attack-family layouts, three target input layouts, three themes, narrow window layout, missing-backend setup, profile save/load/delete, preflight error handling, and zero binding errors.
- Connected WPF smoke: passed actual identification/manual mode selection, mask preflight/preview, result loading, default plaintext masking, explicit reveal, and target invalidation.
- Actual application startup: launched successfully, stayed responsive, connected to Hashcat, and closed cleanly.
- Framework-dependent `win-x64` publish: successful. Backend/extractor binaries are not included.
- Original Hashcat file verification: all 3,100 original file contents matched the recorded SHA-256 baseline. An early Hashcat probe created an empty `hashcat/kernels/` directory; no original release file was changed. Later probes use managed runtime workspaces.

## Evidence boundaries

The available Intel OpenCL runtime reported `CL_INVALID_VALUE`; CPU recovery was rejected as an outdated/broken runtime (5.2.0.10094), and GPU recovery reported no usable devices. No `--force` flag or driver/security modification was used. Successful cracking, real device-status streaming during an attack, and Windows interactive pause/resume/checkpoint controls could not be validated on this hardware. Controls requiring an unverified interactive transport remain disabled.

Process-lifecycle tests use an explicitly test-only console executable to verify real child-process argument boundaries, concurrent stdout/stderr handling, JSON events, sanitized diagnostics, exit-state mapping, cancellation, and repeated Stop calls. They validate HashLynx orchestration, not a cracking engine.

BitLocker availability and script usage validation passed with the existing Hashcat script and Python 3.14.3. PDF, ZIP, RAR, and 7-Zip dependencies were absent and correctly reported unavailable. No real encrypted-file extraction fixtures were supplied, so format-specific end-to-end extraction remains to be checked with configured external tools and authorized samples.

The GitHub Actions workflow is included; this local report does not claim a remote CI run result. Local screenshots and private test artifacts remain in the ignored `artifacts/` directory.
