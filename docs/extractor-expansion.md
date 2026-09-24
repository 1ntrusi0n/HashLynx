# Office and KeePass extraction experiment

Research and implementation: 2026-09-24. The application reads these formats directly, offline, through `IHashExtractor`; users do not need John, Python, Office or KeePass installed.

## Selection from the 2john inventory

The [John jumbo tree](https://github.com/openwall/john/tree/9a336d800a091bec9650c29282485145f31c9ffc) contains 125 files matching `*2john.{py,pl,c,h,rb}` in `run` and `src` at the reviewed revision. This was an inventory survey followed by detailed format, license and Hashcat-compatibility review of the selected candidates, not an audit or port of all 125 programs.

| Candidate family | Fit for HashLynx | Decision |
| --- | --- | --- |
| `office2john` | Common Word, Excel and PowerPoint documents; fits the existing drop-file workflow; published Microsoft specifications and established backend modes | Implement modern encrypted OOXML Standard and Agile profiles |
| `keepass2john` | Common portable password database; small bounded headers; published specification and backend modes | Implement password-only KDBX 3 AES and supported KDBX 4 profiles |
| `libreoffice2john`, `staroffice2john`, `iwork2john` | Useful document extensions, but lower immediate Windows coverage and separate XML/ZIP/legacy variants | Good next document candidates |
| `pwsafe2john`, `1password2john`, `bitwarden2john`, `dashlane2john`, `enpass2john`, `lastpass2john` | Password-manager recovery; formats span standalone files, exports and application profiles with multiple generations | Password Safe is a good next bounded file format; review vault generations individually |
| `ssh2john`, `pem2john`, `putty2john`, `pfx2john`, `gpg2john`, `keystore2john`, `bks2john` | Useful to expert users; diverse key encodings, KDFs and backend coverage | Later expert-focused key import |
| `androidbackup2john`, `itunes_backup2john`, `applenotes2john`, `signal2john`, `telegram2john` | Personal backups/app data, but often need related files or profile-specific workflows | Defer until multi-file inputs have a clear UI |
| `luks2john`, `dmg2john`, `fvde2john`, `truecrypt2john`, `diskcryptor2john`, `bestcrypt*`, `geli2john`, `ecryptfs2john`, `encfs2john` | Disk and volume recovery; additional storage layouts and device access requirements | Separate future designs; BitLocker remains the live-drive workflow |
| Wallet converters (`bitcoin`, `electrum`, `ethereum`, `blockchain`, `monero`, etc.) | Many product-specific formats, versions and extra factors | Defer until format-specific fixtures and UX are available |
| Network, authentication and application profile converters (`pcap`, `radius`, `krb`, `DPAPImk`, `mozilla`, `keychain`, `keyring`, etc.) | Often require captures, account context or multiple artifacts rather than one encrypted document | Outside this initial end-user file-recovery expansion |
| ZIP, RAR, 7-Zip, PDF and BitLocker converters | Already covered by built-in extractors | Keep existing implementations |

## Sources and licensing

Implementation sources are the format specifications: [MS-CFB compound header](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-cfb/05060311-bfce-4b12-874d-71fd4ce63aea), [MS-OFFCRYPTO Standard](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/2895eba1-acb1-4624-9bde-2cdad3fea015), [MS-OFFCRYPTO Agile](https://learn.microsoft.com/en-us/openspecs/office_file_formats/ms-offcrypto/87020a34-e73f-4139-99bc-bbdf6cf6fa55), and the [KeePass KDBX specification](https://keepass.info/help/kb/kdbx.html). Parsers were written independently in C#; no GPL implementation was copied or translated.

The [office2john license/header and output interface](https://github.com/openwall/john/blob/9a336d800a091bec9650c29282485145f31c9ffc/run/office2john.py) were reviewed; its redistribution-permitted implementation was used only as a reference program in the ignored research directory. [keepass2john](https://github.com/openwall/john/blob/9a336d800a091bec9650c29282485145f31c9ffc/src/keepass2john.c) has mixed terms, including GPL portions, so its implementation was not reused. Hashcat's MIT-licensed module interfaces define the output contracts: [9400](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_09400.c), [9500](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_09500.c), [9600](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_09600.c), [13400](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_13400.c), [34300](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_34300.c) and [34301](https://github.com/hashcat/hashcat/blob/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/src/modules/module_34301.c).

Test fixtures retain their upstream notices:

- `tests/HashLynx.Extractors.Tests/Fixtures/Office`: three encrypted public sample documents from [msoffcrypto-tool](https://github.com/nolze/msoffcrypto-tool/tree/6d9e72c58de2cf7df1ab45ac0d74ebedac8c58e3/tests/inputs), with `LICENSE.txt` (MIT, copyright nolze) and `NOTICE.txt` (included BSD 3-Clause attribution). `.expected` records were produced by upstream office2john with isolated olefile 0.47. These are public test data, not user targets.
- `tests/HashLynx.Extractors.Tests/Fixtures/KeePass`: three public databases and matching `.hash` records from [Hashcat's KeePass conversion tests](https://github.com/hashcat/hashcat/tree/9ac24bfebb404100c4d4bd98bf2f755e720cc9d9/tools/2hashcat_tests/keepass), with its original MIT `LICENSE.txt`. The upstream README documents that the records were generated with keepass2john. Neither converter implementation is shipped.
- Additional malformed containers and KDBX 3 verification metadata are generated by original test code. Research tools and Python dependencies remain under ignored `artifacts/extractor-research`, with no runtime application dependency.

## Supported profiles and limits

| Input | Supported profile | Suggested mode |
| --- | --- | --- |
| Encrypted OOXML (`.docx`, `.xlsx`, `.pptx` and related macro/template extensions) | Standard AES-128 or AES-256, SHA-1, 16-byte salt | 9400 |
| Encrypted OOXML | Agile AES-128/SHA-1, CBC, 100,000 rounds, 16-byte salt | 9500 |
| Encrypted OOXML | Agile AES-256/SHA-512, CBC, 100,000 rounds, 16-byte salt | 9600 |
| KeePass KDBX 3.0/3.1 | AES cipher, AES-KDF, password only | 13400 |
| KeePass KDBX 4.0/4.1 | Argon2d/Argon2id, standard 253-byte authenticated header, 32-byte salt | 34300 |
| KeePass KDBX 4.0/4.1 | AES-KDF, 200–256-byte authenticated header, 32-byte seed | 34301 |

Mode suggestions still pass through the installed backend's identification and supported-mode checks. In particular, the tested local Hashcat 7.1.2 release does **not** include mode 34301; extracting a KDBX 4 AES-KDF verifier does not make that older backend capable of recovering it.

Office extraction recovers the password needed to open the document. Editing restrictions, legacy binary DOC/XLS/PPT RC4/XOR encryption, Access, external/certificate-only providers, multiple password keys, custom Agile spin counts and unsupported ciphers are rejected with guidance. A compound-file signature is only a candidate; the reader requires root `EncryptionInfo` and `EncryptedPackage` streams. Unencrypted OOXML retains its ZIP signature and does not become an Office password target.

KeePass recovery targets the database master password, not individual stored entries. Key files, Windows-account keys, hardware factors and key-provider plugins are unsupported. Their presence cannot always be inferred from the file header; the extractor explicitly states its password-only requirement. KeePass 1 KDB files, custom KDFs/public custom fields, duplicate fields, Argon2 secret/associated-data parameters and nonmatching header checksums are rejected. KDBX 4 Argon2 is restricted to the backend's fixed 253-byte header contract, 1–99,999 iterations, 1–32 lanes and valid memory parameters up to the format's 2 GiB ceiling; no Argon2 work or memory allocation occurs during extraction. Nonstandard/ChaCha20 header lengths fail instead of emitting a misleading record. AES round counts must fit an unsigned 32-bit backend value.

## Parser safeguards

Office reads only allocation/directory/encryption metadata: at most 16 MiB of sector reads from files up to 512 MiB, FAT at most 4 MiB, directory/mini-FAT at most 2 MiB each, mini-stream at most 8 MiB, and encryption metadata at most 1 MiB. Both 512- and 4096-byte sectors, ordinary streams and mini-streams are supported. Invalid offsets, duplicate names, sector overlaps and cyclic chains fail. XML DTDs and external entities are prohibited and XML size is bounded. No embedded object is executed.

KeePass reads at most 64 KiB, bounds each field/dictionary entry, rejects duplicates, checks version/type/length consistency and verifies the KDBX 4 header SHA-256 checksum before producing a token. Neither reader modifies source files, logs tokens, decrypts document/vault content, downloads anything, launches an external program or changes Windows settings. Both use the existing asynchronous/cancellable native extractor lifecycle.

## Validation

`NativeDocumentTests` covers known Office and KeePass records, Standard AES profiles, Agile metadata, both compound-sector sizes and allocation paths, malformed/cyclic metadata, hostile XML, truncation, duplicate fields, KDF limits, KDBX 4 checksums, misleading extensions, cancellation and source immutability. Normal tests require no backend or GPU.

The opt-in `DocumentRecoveryTests` identifies and recovers the three public Office fixtures, a locally generated KDBX 3 verifier, and the two public KDBX 4 Argon2 fixtures using their documented test passwords. It uses explicit device selection, private temporary inputs/output, disabled potfile/logfile/restore, shell-free execution, timeout and cleanup:

```powershell
$env:HASHLYNX_TEST_HASHCAT = 'G:\Coding\HashLynx\artifacts\hashcat-smoke\hashcat.exe'
$env:HASHLYNX_TEST_DEVICE = '3'
dotnet test tests/HashLynx.Extractors.Tests -c Release --filter FullyQualifiedName~DocumentRecoveryTests
```

Use a disposable Hashcat copy. No user encrypted files, passwords or live volumes are needed for these checks.

### Recorded results (2026-09-24)

- Extractor/drive normal suite: **216 passed**, including 33 new document/KeePass cases; Release compilation passed.
- All three real Office samples were identified and their known passwords recovered on the Intel CPU (selected device 3), modes 9400 and 9600. The initial combined six-sample run then reached its overall six-minute limit while starting KDBX 3 after those three successful recoveries. Cold OpenCL compilation consumed most of the time.
- Integration tests were split into independent cases with a five-minute limit each. The targeted KeePass run then passed **all three cases** in 4 minutes 20 seconds: generated KDBX 3 (13400), real Argon2d and Argon2id KDBX 4 databases (34300). The source samples and normal parser tests need no GPU.
- KDBX 4 AES-KDF output exactly matches the published upstream fixture, but its password recovery was **not** run: the installed 7.1.2 backend does not contain mode 34301. Office 2010/9500 has structural and token-contract tests; no independent 2010 sample recovery is claimed.
- No user target, password, volume or original backend directory was used or modified in these checks.
