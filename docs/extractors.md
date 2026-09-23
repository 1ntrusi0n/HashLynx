# Encrypted-file extractors

HashLynx extracts password-verification material locally and passes it into the normal Hashcat identification workflow. It does not decrypt files or implement a password-cracking engine. Source files are opened read-only by HashLynx; external tools run with the current user's permissions.

## Included adapters

| Adapter ID | Input | External dependency | Suggested Hashcat modes |
| --- | --- | --- | --- |
| `pdf` | PDF documents | `pdf2john.exe`, `.py`, or `.pl` | PDF revision-dependent modes |
| `zip` | ZIP/ZIPX archives | None for built-in formats; optional `zip2john.exe` override | 17200 (deflate), 17210 (stored), 13600 (WinZip AES) |
| `rar` | RAR3/RAR5 archives | `rar2john.exe` | 12500, 13000, 23700, 23800 |
| `7z` | 7-Zip archives | `7z2john.exe`, `.pl`, or `.py` | 11600 |
| `bitlocker` | BitLocker partition images | Hashcat `tools/bitlocker2hashcat.py` and Python 3 | 22100 |

These are suggestions, not proof of a compatible mode. Hashcat remains the identification authority. John output is normalized by removing filename/login fields around the actual token. HashLynx preserves ZIP closing markers, deduplicates repeated tokens, and rejects RAR records that refer to external archive data instead of including it inline.

No John the Ripper binaries or scripts are bundled. The native C# ZIP implementation uses the ZIP specifications and the hash serialization documented in the permissively licensed `zip2john.c`; see [third-party notices](../THIRD_PARTY_NOTICES.md). Obtain external tools separately from their official project and follow their licenses. HashLynx neither downloads dependencies nor changes their source files. The Hashcat-provided BitLocker script remains part of the separately obtained Hashcat installation.

## Built-in ZIP extraction

Choose **Encrypted file** in Target Inspector, select the archive, add a wordlist, and start recovery. ZIP is available without installing another tool. HashLynx reads metadata and encrypted bytes without unpacking or modifying the archive. Hashcat still identifies the resulting hash and performs recovery.

Supported formats are traditional ZipCrypto with stored or deflated content, and WinZip AES-128/192/256 (AE-1 and AE-2). Standard single-volume ZIP64 records and data descriptors, with or without descriptor signatures, are supported. AES verification uses the ciphertext authentication code and does not require decompressing its content.

The extractor selects the smallest supported encrypted member and returns one full verification record. Target Inspector reports the selected member's directory index and encrypted-member count. ZIP files may use different passwords for different members: a recovered password verifies the selected member only. Mixed encryption methods therefore do not create an incompatible mixed-mode target file. Unsupported or oversized members are reported when another member is usable.

Limits follow the full-data Hashcat formats: ZipCrypto encrypted member data (including its 12-byte header) must be at most 320 KiB, and AES ciphertext must be less than 8 MiB. Archive size itself is not capped by these member limits. Directory processing is bounded to 100,000 entries and 64 MiB. Empty ZipCrypto members cannot supply full password verification and are skipped. Split volumes, self-extracting containers, PKWARE strong encryption, encrypted central directories, unusual directory extension records, and other ZipCrypto compression methods require another workflow. Missing or inconsistent structural records fail with an explanation; unsupported archives are not silently passed to a discovered executable.

For another ZIP variant, explicitly configure a compatible **zip2john tool path** on the ZIP card and save it. A configured tool replaces the native extractor, preserving existing external-tool configurations. Clear the path and save to return to the built-in implementation. A missing configured executable is reported instead of silently changing implementations. The registry still requires a recognizable ZIP signature; self-extracting containers must first be converted to a conventional ZIP with an appropriate archive utility.

## Configuration and validation

Open **Extractors** and configure the tool path on the adapter's card. A tool can be a native `.exe`, Python `.py` script, or Perl `.pl` script. Script adapters also accept an interpreter executable path. HashLynx uses individual process arguments; do not enter a command line or append flags to a path. Batch files and shell executables are rejected.

When a path is not configured, ZIP uses the built-in implementation; the other John adapters search `PATH` for their known tool names. The BitLocker adapter looks for `tools/bitlocker2hashcat.py` inside the configured Hashcat directory. Python discovery checks `py.exe`, `python.exe`, then `python3.exe`; the Windows launcher is invoked with `-3`. Perl discovery checks `perl.exe`. An explicit missing path is reported rather than silently replaced by another tool.

For the native ZIP extractor, **Available** and **Validate** confirm that no dependency is needed. For external adapters, **Available** means the executable was found, or a script and compatible runtime were found. It does not establish that every encrypted format or optional runtime module works. **Validate** runs the tool without a target to obtain usage output; BitLocker uses `--help`. Errors or timeouts are shown as unavailable. A representative encrypted sample is still required for a complete compatibility test.

Tool paths are stored in `AppSettings.ExtractorTools` as `ExtractorToolSettings` values keyed by the adapter ID. Application composition maps those persisted values to the extractor library's `ExtractorConfiguration` records and rebuilds the registry after configuration changes. Availability checks use runtime version output when available; a displayed Python or Perl version describes the interpreter, not a separately verified extractor release.

## File inspection and limits

The registry reads at most 4 KiB per adapter and checks file signatures. Extensions help order matching adapters, but extension-only matches are not accepted. A ZIP renamed to `.pdf` is still treated as ZIP. Detection of a file family does not imply that the file is encrypted. Self-extracting archives, multipart archives without a signature in the selected volume, and arbitrary embedded-container offsets are not supported in this first version.

BitLocker input must start at the BitLocker partition's boot sector. Whole-disk images with a partition offset, dynamic virtual disks, TPM-only protectors, and recovery-key protectors are unsupported by this adapter. Export the relevant partition to a separate raw image using appropriate tools before selecting it. The original disk or container is not modified.

Python scripts require Python 3; legacy Python 2-only extractors are unavailable. Perl scripts may require modules installed separately by the user. A successful usage check cannot guarantee that all format-specific modules are installed.

Extraction runs asynchronously and supports cancellation. For external tools, cancellation terminates the child process tree. Standard output and error are captured concurrently with limits of 16,777,216 and 65,536 characters. Truncated output and nonzero tool exits fail the extraction, preventing partial hashes from being accepted. Large archive hashes may exceed this limit and will need a different extraction workflow. Raw tool output is excluded from application diagnostics because it can contain sensitive input data. Text analysis retains up to 1 MiB per ordinary line, with a bounded 16 MiB + 256 character allowance for tagged WinZip AES records carrying inline ciphertext.

## Adding an adapter

1. Implement `IHashExtractor` in `HashLynx.Extractors` with a stable unique `Id`, display metadata, extension hints, availability/validation methods, signature matching, and asynchronous extraction.
2. Return `ExtractionResult` containing normalized hashes, suggested modes, source type, extractor name, metadata, and concise diagnostics. Propagate cancellation. Do not put hashes or recovered passwords in diagnostic text.
3. Register the implementation in `ExtractorRegistry.CreateDefault`, or provide it to the registry constructor. The UI consumes the interface and does not need format-specific branches.
4. For external tools, reuse `ExternalHashExtractor` and `IExtractorProcessRunner` where suitable, add parser signatures and mode suggestions, and keep tool acquisition external. A future native BitLocker implementation can implement the same interface.
5. Add tests for signatures, misleading extensions, missing dependencies, normalized output, safe argument boundaries, cancellation, and malformed tool output. Record material changes in the changelog.

Normal tests use fake process runners and temporary files. They do not require Hashcat, Python, Perl, John, a GPU, or an encrypted user file. Runtime discovery/help checks are separate from evidence of successful end-to-end extraction.

To run the opt-in real ZIP recovery test, set `HASHLYNX_TEST_HASHCAT` to a disposable Hashcat installation, `HASHLYNX_TEST_7ZIP` to an installed `7z.exe`, and `HASHLYNX_TEST_DEVICE` to a working backend device ID. Run `dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~NativeZipRecoveryTests`. It creates synthetic password-protected archives with 7-Zip, extracts them natively, asks Hashcat to identify and recover them, and checks the session's recovered plaintext. It covers stored/deflated ZipCrypto and all three AES strengths without using user archives.
