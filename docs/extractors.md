# Encrypted-file extractors

HashLynx extracts password-verification material locally and passes it into the normal Hashcat identification workflow. It does not decrypt files or implement a password-cracking engine. Source files are opened read-only by HashLynx; external tools run with the current user's permissions.

## Included adapters

| Adapter ID | Input | External dependency | Suggested Hashcat modes |
| --- | --- | --- | --- |
| `pdf` | PDF documents | `pdf2john.exe`, `.py`, or `.pl` | PDF revision-dependent modes |
| `zip` | ZIP/ZIPX archives | None for built-in formats; optional `zip2john.exe` override | 17200 (deflate), 17210 (stored), 13600 (WinZip AES) |
| `rar` | RAR3/RAR5 archives | None for built-in formats; optional `rar2john.exe` override | 12500, 13000, 23700, 23800 |
| `7z` | 7-Zip archives | None for built-in formats; optional `7z2john.exe`, `.pl`, or `.py` override | 11600 |
| `bitlocker` | BitLocker partition images | Hashcat `tools/bitlocker2hashcat.py` and Python 3 | 22100 |

These are suggestions, not proof of a compatible mode. Hashcat remains the identification authority. John output is normalized by removing filename/login fields around the actual token. HashLynx preserves ZIP closing markers, deduplicates repeated tokens, and rejects RAR records that refer to external archive data instead of including it inline.

No John the Ripper binaries or scripts are bundled. The native C# ZIP implementation uses the ZIP specifications and the hash serialization documented in the permissively licensed `zip2john.c`; see [third-party notices](../THIRD_PARTY_NOTICES.md). Obtain external tools separately from their official project and follow their licenses. HashLynx neither downloads dependencies nor changes their source files. The Hashcat-provided BitLocker script remains part of the separately obtained Hashcat installation.

## Built-in ZIP extraction

Choose **Encrypted file** in Target Inspector, select the archive, add a wordlist, and start recovery. ZIP is available without installing another tool. HashLynx reads metadata and encrypted bytes without unpacking or modifying the archive. Hashcat still identifies the resulting hash and performs recovery.

Supported formats are traditional ZipCrypto with stored or deflated content, and WinZip AES-128/192/256 (AE-1 and AE-2). Standard single-volume ZIP64 records and data descriptors, with or without descriptor signatures, are supported. AES verification uses the ciphertext authentication code and does not require decompressing its content.

The extractor selects the smallest supported encrypted member and returns one full verification record. Target Inspector reports the selected member's directory index and encrypted-member count. ZIP files may use different passwords for different members: a recovered password verifies the selected member only. Mixed encryption methods therefore do not create an incompatible mixed-mode target file. Unsupported or oversized members are reported when another member is usable.

Limits follow the full-data Hashcat formats: ZipCrypto encrypted member data (including its 12-byte header) must be at most 320 KiB, and AES ciphertext must be less than 8 MiB. Archive size itself is not capped by these member limits. Directory processing is bounded to 100,000 entries and 64 MiB. Empty ZipCrypto members cannot supply full password verification and are skipped. Split volumes, self-extracting containers, PKWARE strong encryption, encrypted central directories, unusual directory extension records, and other ZipCrypto compression methods require another workflow. Missing or inconsistent structural records fail with an explanation; unsupported archives are not silently passed to a discovered executable.

For another ZIP variant, explicitly configure a compatible **zip2john tool path** on the ZIP card and save it. A configured tool replaces the native extractor, preserving existing external-tool configurations. Clear the path and save to return to the built-in implementation. A missing configured executable is reported instead of silently changing implementations. The registry still requires a recognizable ZIP signature; self-extracting containers must first be converted to a conventional ZIP with an appropriate archive utility.

## Built-in RAR extraction

RAR3 (including the RAR4 container generation) supports encrypted headers, stored encrypted files and independently decodable compressed encrypted files. RAR5 supports file or archive-header password verifiers, including archives with encrypted filenames. Header CRCs and RAR5 verifier checksums are validated before accepting a record. RAR5 verification does not depend on the payload compression algorithm.

RAR3 data recovery chooses a small supported member, preferring stored data, and skips solid continuations. It needs a nonempty member, up to 327,520 encrypted bytes and 655,360 unpacked bytes, fitting the current full-data Hashcat parser. Older pre-RAR3 encryption, empty encrypted files, split members/volumes, and encrypted RAR3 headers with recovery records need another workflow. RAR3 header recovery uses the final encrypted end block; appended data and damaged encrypted content cannot be fully verified until recovery. RAR5 needs an intact password-check value and a supported AES-256 encryption version with a KDF exponent from 1 to 24. Header-encrypted RAR5 recovery uses the archive verifier; volume metadata behind that encryption is not inspected.

Scans are bounded to 100,000 blocks and 64 MiB of headers, with each RAR5 header at most 2 MiB. The selected-member notice remains visible in Target Inspector. Different members may use different passwords.

## Built-in 7-Zip extraction

The native reader supports AES-256 streams with Copy, LZMA, LZMA2 or Deflate and a usable CRC. Encrypted headers can be targeted directly; ordinary LZMA-compressed metadata is decoded in memory with a small public-domain LZMA SDK component. Neither Perl nor an installed 7-Zip executable is required. Archive files are opened read-only and no archive contents are written to disk.

The reader handles simple linear coder chains and solid streams, selecting a small usable encrypted stream. It verifies the start/next-header CRCs and decoded metadata CRC. Hashcat then verifies recovered candidates using the complete stream CRC; when only member CRCs are stored, these are combined using their lengths without decrypting content. Other streams can have different passwords.

Current limits: single-volume standard archives, no AES salt (as required by Hashcat mode 11600), KDF exponent at most 24, encrypted data at most 8 MiB minus 16 bytes, and CRC verification output at most 9,999,999 bytes. Compression dictionaries used for recovery are limited to 64 MiB. Metadata is bounded to 8 MiB, with LZMA metadata dictionaries at most 8 MiB and at most two nested encoded-header layers. Stream/substream counts are bounded to 100,000, and a folder may contain at most four simple coders. Multi-input graphs, BCJ/Delta/other preprocessing filters, unsupported codecs, additional/external metadata streams, self-extracting containers and split archives require another workflow. An external extractor cannot override Hashcat's own codec/format limitations; some archives require a different recovery backend.

## Configuration and validation

ZIP, RAR and 7-Zip are built in. Leave their tool paths blank unless deliberately selecting an external override. Existing configured tool paths remain in effect; clear the path and save to switch to native extraction. Missing configured executables are reported rather than silently replaced. No extractor downloads or process launches occur during native extraction.

Open **Extractors** and configure the tool path on the adapter's card. A tool can be a native `.exe`, Python `.py` script, or Perl `.pl` script. Script adapters also accept an interpreter executable path. HashLynx uses individual process arguments; do not enter a command line or append flags to a path. Batch files and shell executables are rejected.

When a path is not configured, ZIP, RAR and 7-Zip use their built-in implementations; PDF searches `PATH` for its known tool names. The BitLocker adapter looks for `tools/bitlocker2hashcat.py` inside the configured Hashcat directory. Python discovery checks `py.exe`, `python.exe`, then `python3.exe`; the Windows launcher is invoked with `-3`. Perl discovery checks `perl.exe`. An explicit missing path is reported rather than silently replaced by another tool.

For native extractors, **Available** and **Validate** confirm that no dependency is needed. For external adapters, **Available** means the executable was found, or a script and compatible runtime were found. It does not establish that every encrypted format or optional runtime module works. **Validate** runs the tool without a target to obtain usage output; BitLocker uses `--help`. Errors or timeouts are shown as unavailable. A representative encrypted sample is still required for a complete compatibility test.

Tool paths are stored in `AppSettings.ExtractorTools` as `ExtractorToolSettings` values keyed by the adapter ID. Application composition maps those persisted values to the extractor library's `ExtractorConfiguration` records and rebuilds the registry after configuration changes. Availability checks use runtime version output when available; a displayed Python or Perl version describes the interpreter, not a separately verified extractor release.

## File inspection and limits

The registry reads at most 4 KiB per adapter and checks file signatures. Extensions help order matching adapters, but extension-only matches are not accepted. A ZIP renamed to `.pdf` is still treated as ZIP. Detection of a file family does not imply that the file is encrypted. Self-extracting archives, multipart archives without a signature in the selected volume, and arbitrary embedded-container offsets are not supported in this first version.

BitLocker input must start at the BitLocker partition's boot sector. Whole-disk images with a partition offset, dynamic virtual disks, TPM-only protectors, and recovery-key protectors are unsupported by this adapter. Export the relevant partition to a separate raw image using appropriate tools before selecting it. The original disk or container is not modified.

Python scripts require Python 3; legacy Python 2-only extractors are unavailable. Perl scripts may require modules installed separately by the user. A successful usage check cannot guarantee that all format-specific modules are installed.

Extraction runs asynchronously and supports cancellation. For external tools, cancellation terminates the child process tree. Standard output and error are captured concurrently with limits of 16,777,216 and 65,536 characters. Truncated output and nonzero tool exits fail the extraction, preventing partial hashes from being accepted. Large archive hashes may exceed this limit and will need a different extraction workflow. Raw tool output is excluded from application diagnostics because it can contain sensitive input data. Text analysis retains up to 1 MiB per ordinary line, with a bounded 16 MiB + 256 character allowance for tagged WinZip AES and 7-Zip records carrying inline ciphertext.

## Adding an adapter

1. Implement `IHashExtractor` in `HashLynx.Extractors` with a stable unique `Id`, display metadata, extension hints, availability/validation methods, signature matching, and asynchronous extraction.
2. Return `ExtractionResult` containing normalized hashes, suggested modes, source type, extractor name, metadata, and concise diagnostics. Propagate cancellation. Do not put hashes or recovered passwords in diagnostic text.
3. Register the implementation in `ExtractorRegistry.CreateDefault`, or provide it to the registry constructor. The UI consumes the interface and does not need format-specific branches.
4. For external tools, reuse `ExternalHashExtractor` and `IExtractorProcessRunner` where suitable, add parser signatures and mode suggestions, and keep tool acquisition external. A future native BitLocker implementation can implement the same interface.
5. Add tests for signatures, misleading extensions, missing dependencies, normalized output, safe argument boundaries, cancellation, and malformed tool output. Record material changes in the changelog.

Normal tests use fake process runners and temporary files. They do not require Hashcat, Python, Perl, John, a GPU, or an encrypted user file. Runtime discovery/help checks are separate from evidence of successful end-to-end extraction.

To run the opt-in real ZIP recovery test, set `HASHLYNX_TEST_HASHCAT` to a disposable Hashcat installation, `HASHLYNX_TEST_7ZIP` to an installed `7z.exe`, and `HASHLYNX_TEST_DEVICE` to a working backend device ID. Run `dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~NativeZipRecoveryTests`. It creates synthetic password-protected archives with 7-Zip, extracts them natively, asks Hashcat to identify and recover them, and checks the session's recovered plaintext. It covers stored/deflated ZipCrypto and all three AES strengths without using user archives.

The additional native archive integration checks use the same backend, device and 7-Zip environment variables:

```powershell
dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~SevenZipCreatedArchives
```

This creates encrypted-header and file-encrypted 7z archives covering Copy, LZMA, LZMA2, Deflate and a multi-file solid stream, then verifies identification, recovery and session results.

For RAR, set `HASHLYNX_TEST_RAR_FIXTURES` to a directory containing these public test archives, obtained explicitly before running the test (no test downloads anything):

- `rar3-comment-hpsw.rar`, `rar5-hpsw.rar`, `rar5-psw.rar`, `rar5-psw-blake.rar` from [rarfile's test files at d2f7df6](https://github.com/markokr/rarfile/tree/d2f7df6fc843dae356fd6b0a85971dc36fd6e757/test/files).
- `test_read_format_rar4_encrypted.rar`, decoded from the `.rar.uu` fixture in [libarchive at a7363f0](https://github.com/libarchive/libarchive/tree/a7363f0f14406a0a7c813f8ecb0733ca640b294e/libarchive/test). This uses traditional RAR3 encryption despite the container-generation name.

Both projects document the synthetic password `password` in their reading/encryption tests. Run `dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~PublicRar3AndRar5`. A stored RAR3 case is also synthesized from Hashcat's public mode-23700 self-test vector. Keep downloaded fixtures outside Git (for example under `artifacts/`). Test fixtures are not user data and are never added to the user's session history.
