# Encrypted-file extractors

HashLynx extracts password-verification material locally and passes it into the normal Hashcat identification workflow. It does not decrypt files or implement a password-cracking engine. Source files are opened read-only by HashLynx; external tools run with the current user's permissions.

## Included adapters

| Adapter ID | Input | External dependency | Suggested Hashcat modes |
| --- | --- | --- | --- |
| `pdf` | PDF documents | None | 10400, 10500, 10600, 10700 |
| `zip` | ZIP/ZIPX archives | None | 17200 (deflate), 17210 (stored), 13600 (WinZip AES) |
| `rar` | RAR3/RAR5 archives | None | 12500, 13000, 23700, 23800 |
| `7z` | 7-Zip archives | None | 11600 |
| `bitlocker` | Raw BitLocker partition images | None | 22100 |

These are suggestions, not proof of a compatible mode. Hashcat remains the identification authority. John output is normalized by removing filename/login fields around the actual token. HashLynx preserves ZIP closing markers, deduplicates repeated tokens, and rejects RAR records that refer to external archive data instead of including it inline.

No John the Ripper binaries or scripts are bundled. The native C# ZIP implementation uses the ZIP specifications and the hash serialization documented in the permissively licensed `zip2john.c`; see [third-party notices](../THIRD_PARTY_NOTICES.md). Obtain external tools separately from their official project and follow their licenses. HashLynx neither downloads dependencies nor changes their source files. BitLocker extraction is implemented in C# using the referenced format and Hashcat hash layout.

## Built-in PDF extraction

Choose **Encrypted file** in Target Inspector and select the PDF. No Python, Perl, John or PDF viewer is needed. The original C# reader extracts Standard Security Handler verification fields without rendering pages, executing PDF actions, decrypting content or changing the source file. Recovery targets the user/open password, not the separate owner password used for permission restrictions. A permissions-only PDF may already open with an empty user password.

Supported combinations are revision 2 with a 40-bit RC4 key (mode 10400), revision 3 with 128-bit RC4 or revision 4 with 128-bit RC4/AES (10500), and revision 5/6 with AES-256 (10600/10700). Hashcat confirms the suggested mode before automatic selection. The output includes encryption version/revision, signed permissions, metadata-encryption flag, the first document identifier, U/O verification fields and, for AES-256, OE/UE encrypted keys.

The reader follows the final `startxref` and active cross-reference chain, including incremental and hybrid updates. Newer entries take precedence over older ones, including freed objects; malformed latest metadata never silently falls back to an older encryption dictionary. Classic tables and uncompressed or Flate-compressed cross-reference streams with PNG predictors are supported. Strings support hexadecimal/literal forms, escapes and octal bytes. Linearized PDFs are covered by independent fixtures. Page content and compressed page-object streams need not be decoded to read encryption metadata.

Limits: 64 MiB per document, 250,000 cross-reference entries across at most 64 sections, 8 MiB per encoded/decoded cross-reference stream, 32 syntax nesting levels and 16 metadata reference hops. Encryption fields must have the lengths expected by the chosen Hashcat mode. Certificates/custom security handlers, non-128-bit revision-3/4 keys, other cross-reference compression filters, compressed encryption-field objects, missing identifiers, damaged references and trailing non-whitespace after the final EOF marker are rejected with a diagnostic. Unsupported PDFs can be processed with a compatible external extractor separately and imported through **Hash File**.

## Built-in ZIP extraction

Choose **Encrypted file** in Target Inspector, select the archive, add a wordlist, and start recovery. ZIP is available without installing another tool. HashLynx reads metadata and encrypted bytes without unpacking or modifying the archive. Hashcat still identifies the resulting hash and performs recovery.

Supported formats are traditional ZipCrypto with stored or deflated content, and WinZip AES-128/192/256 (AE-1 and AE-2). Standard single-volume ZIP64 records and data descriptors, with or without descriptor signatures, are supported. AES verification uses the ciphertext authentication code and does not require decompressing its content.

The extractor selects the smallest supported encrypted member and returns one full verification record. Target Inspector reports the selected member's directory index and encrypted-member count. ZIP files may use different passwords for different members: a recovered password verifies the selected member only. Mixed encryption methods therefore do not create an incompatible mixed-mode target file. Unsupported or oversized members are reported when another member is usable.

Limits follow the full-data Hashcat formats: ZipCrypto encrypted member data (including its 12-byte header) must be at most 320 KiB, and AES ciphertext must be less than 8 MiB. Archive size itself is not capped by these member limits. Directory processing is bounded to 100,000 entries and 64 MiB. Empty ZipCrypto members cannot supply full password verification and are skipped. Split volumes, self-extracting containers, PKWARE strong encryption, encrypted central directories, unusual directory extension records, and other ZipCrypto compression methods require another workflow. Missing or inconsistent structural records fail with an explanation; unsupported archives are not silently passed to a discovered executable.

For unsupported ZIP variants, use a compatible external tool separately and import its Hashcat-compatible hash output. The built-in registry requires a recognizable ZIP signature; self-extracting containers must first be converted to a conventional ZIP with an appropriate archive utility.

## Built-in RAR extraction

RAR3 (including the RAR4 container generation) supports encrypted headers, stored encrypted files and independently decodable compressed encrypted files. RAR5 supports file or archive-header password verifiers, including archives with encrypted filenames. Header CRCs and RAR5 verifier checksums are validated before accepting a record. RAR5 verification does not depend on the payload compression algorithm.

RAR3 data recovery chooses a small supported member, preferring stored data, and skips solid continuations. It needs a nonempty member, up to 327,520 encrypted bytes and 655,360 unpacked bytes, fitting the current full-data Hashcat parser. Older pre-RAR3 encryption, empty encrypted files, split members/volumes, and encrypted RAR3 headers with recovery records need another workflow. RAR3 header recovery uses the final encrypted end block; appended data and damaged encrypted content cannot be fully verified until recovery. RAR5 needs an intact password-check value and a supported AES-256 encryption version with a KDF exponent from 1 to 24. Header-encrypted RAR5 recovery uses the archive verifier; volume metadata behind that encryption is not inspected.

Scans are bounded to 100,000 blocks and 64 MiB of headers, with each RAR5 header at most 2 MiB. The selected-member notice remains visible in Target Inspector. Different members may use different passwords.

## Built-in 7-Zip extraction

The native reader supports AES-256 streams with Copy, LZMA, LZMA2 or Deflate and a usable CRC. Encrypted headers can be targeted directly; ordinary LZMA-compressed metadata is decoded in memory with a small public-domain LZMA SDK component. Neither Perl nor an installed 7-Zip executable is required. Archive files are opened read-only and no archive contents are written to disk.

The reader handles simple linear coder chains and solid streams, selecting a small usable encrypted stream. It verifies the start/next-header CRCs and decoded metadata CRC. Hashcat then verifies recovered candidates using the complete stream CRC; when only member CRCs are stored, these are combined using their lengths without decrypting content. Other streams can have different passwords.

Current limits: single-volume standard archives, no AES salt (as required by Hashcat mode 11600), KDF exponent at most 24, encrypted data at most 8 MiB minus 16 bytes, and CRC verification output at most 9,999,999 bytes. Compression dictionaries used for recovery are limited to 64 MiB. Metadata is bounded to 8 MiB, with LZMA metadata dictionaries at most 8 MiB and at most two nested encoded-header layers. Stream/substream counts are bounded to 100,000, and a folder may contain at most four simple coders. Multi-input graphs, BCJ/Delta/other preprocessing filters, unsupported codecs, additional/external metadata streams, self-extracting containers and split archives require another workflow. An external extractor cannot override Hashcat's own codec/format limitations; some archives require a different recovery backend.

## Built-in BitLocker extraction

This experimental branch also accepts a connected volume through **BitLocker Drive**. Its administrator helper supplies bounded, read-only partition access to the same metadata parser. See [the test workflow and limits](bitlocker-drive-test.md). The raw-image workflow remains available.

Select a raw partition image starting at its boot sector in Target Inspector. The reader supports Windows 7+ `-FVE-FS-` and BitLocker To Go `MSWIN4.1` layouts with the standard or used-space-only identifier. It reads metadata block version 2, metadata header version 1, and version-1 VMKs protected by a user password (`0x2000`). Stretch-key methods `0x1000` and `0x1001`, a 16-byte salt, a 12-byte nonce, and 60 bytes of authentication tag plus encrypted VMK are supported.

The reader emits one distinct `$bitlocker$1$` record per supported password protector. Type 1 verifies the full AES-CCM authentication tag, so the weaker type-0 duplicate is not emitted. The extractor only reads metadata; Hashcat performs candidate testing. It never mounts, writes to, unlocks or decrypts the source partition.

Metadata reads are bounded to 1 MiB, 8,192 entries per table and 128 distinct password protectors. Signatures, versions, offsets, sizes, entry boundaries and required properties are validated. If a copy is truncated or structurally invalid, the reader tries the next of the three boot-sector metadata pointers and reports backup use. A structurally valid copy without a supported password protector is authoritative; it never merges older backup protectors into that snapshot. Backup metadata can still describe an older configuration. Extraction alone does not authenticate the entire partition or its metadata; candidate verification checks the extracted key's tag.

Windows Vista, other metadata versions, TPM-only and TPM+PIN protectors, startup keys, recovery passwords, whole-disk images and VHD/VHDX containers are unsupported. Export the relevant partition as a raw image using a suitable tool first. BitLocker does not need Python, the Hashcat script, or a configurable extractor path.

## Configuration and validation

PDF, ZIP, RAR, 7-Zip and BitLocker are built in and have no configuration cards. Select them in Target Inspector. The Extractors navigation item is hidden while all registered formats are built in. Legacy tool/interpreter paths are preserved in settings but ignored by the application registry, so hidden settings cannot unexpectedly launch an external tool. No downloads or process launches occur during native extraction.

The library retains explicit external adapters for custom composition and tests. An external adapter may use a native executable, Python 3 script or Perl script and an optional interpreter path. Paths are separate arguments, never shell command lines. Availability means the dependency was found; validation checks usage output without a target. Neither proves compatibility with every input. This developer extension is separate from the application's built-in registry.

## File inspection and limits

The registry reads at most 4 KiB per adapter and checks file signatures. Extensions help order matching adapters, but extension-only matches are not accepted. A ZIP renamed to `.pdf` is still treated as ZIP. Detection of a file family does not imply that the file is encrypted. Self-extracting archives, multipart archives without a signature in the selected volume, and arbitrary embedded-container offsets are not supported in this first version.

BitLocker input must start at the BitLocker partition's boot sector. Whole-disk images with a partition offset, dynamic virtual disks, TPM-only protectors, and recovery-key protectors are unsupported by this adapter. Export the relevant partition to a separate raw image using appropriate tools before selecting it. The original disk or container is not modified.

Python scripts require Python 3; legacy Python 2-only extractors are unavailable. Perl scripts may require modules installed separately by the user. A successful usage check cannot guarantee that all format-specific modules are installed.

Extraction runs asynchronously and supports cancellation. For external tools, cancellation terminates the child process tree. Standard output and error are captured concurrently with limits of 16,777,216 and 65,536 characters. Truncated output and nonzero tool exits fail the extraction, preventing partial hashes from being accepted. Large archive hashes may exceed this limit and will need a different extraction workflow. Raw tool output is excluded from application diagnostics because it can contain sensitive input data. Text analysis retains up to 1 MiB per ordinary line, with a bounded 16 MiB + 256 character allowance for tagged WinZip AES and 7-Zip records carrying inline ciphertext.

## Adding an adapter

1. Implement `IHashExtractor` in `HashLynx.Extractors` with a stable unique `Id`, display metadata, extension hints, availability/validation methods, signature matching, and asynchronous extraction.
2. Return `ExtractionResult` containing normalized hashes, suggested modes, source type, extractor name, metadata, and concise diagnostics. Propagate cancellation. Do not put hashes or recovered passwords in diagnostic text.
3. Register the implementation in `ExtractorRegistry.CreateDefault`, or provide it to the registry constructor. The UI consumes the interface and does not need format-specific branches.
4. For external tools, reuse `ExternalHashExtractor` and `IExtractorProcessRunner` where suitable, add parser signatures and mode suggestions, and keep tool acquisition external. Set `IsBuiltIn` for implementations that need no settings so they stay off the configuration page.
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

The opt-in BitLocker check uses synthetic partition metadata around Hashcat's public mode-22100 self-test vector. It compares native output against the referenced Python script, then checks Hashcat identification, CPU/device recovery and session plaintext. It is not a validation against a full Windows-created volume. Normal parser tests also cover To Go/used-space layouts, backups, unsupported protectors and malformed metadata.

Set `HASHLYNX_TEST_HASHCAT` to a disposable backend, `HASHLYNX_TEST_DEVICE` to a working device ID, `HASHLYNX_TEST_PYTHON` to Windows `py.exe`, and `HASHLYNX_TEST_BITLOCKER_REFERENCE` to a separately obtained copy of [Hashcat's reference script](https://github.com/hashcat/hashcat/blob/master/tools/bitlocker2hashcat.py). Run:

```powershell
dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~NativeBitLocker
```

No test downloads dependencies, uses a live disk, or changes BitLocker settings.

## PDF validation

Normal tests include seven original one-page PDFs generated by pikepdf 10.13.0.post1, with revision 2/3/4 RC4, revision 4 AES with unencrypted metadata, revision 5/6 AES, compressed cross-reference streams and a linearized revision-6 document. The expected hashes were obtained independently from the referenced `pdf2john.py` with pyHanko 0.37.0. The known open password is `HashLynx-pdf-test`; a different owner password ensures recovery targets the open password. These are synthetic test data, never user documents or application assets.

`scripts/generate-pdf-fixtures.py OUTPUT_DIRECTORY` regenerates samples using a separately installed test-only pikepdf. Encryption salts/IDs are random; regenerated files need fresh `.expected` outputs from the reference script. Ordinary tests use the checked-in fixtures and need no Python. Malformed-file tests additionally cover cyclic references, stale/freed incremental entries, hybrid precedence, all PNG row filters, bounded expansion, syntax escapes, truncations, mutations and cancellation.

For actual Hashcat identification and known-password recovery across all seven fixtures, set `HASHLYNX_TEST_HASHCAT` to a disposable installation and `HASHLYNX_TEST_DEVICE` to a working device ID, then run:

```powershell
dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~NativePdfFixtures
```

This also verifies session plaintext and that source PDFs remain unchanged. It requires no Python runtime. Python reference/generation tools are development tools only and are not distributed with the app.
