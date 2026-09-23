# Third-party notices

HashLynx is an independent application. Hashcat is a separate third-party project, is not developed by HashLynx, and does not sponsor or endorse this frontend.

## Hashcat

[Hashcat](https://hashcat.net/hashcat/) is an MIT-licensed backend. Users separately obtain an official release and configure it. The local `hashcat/` folder is ignored by Git and is not included in this repository or the application publish output. Hashcat's own `docs/license.txt` and `docs/license_libs/` contain its copyright and dependency notices; do not remove or replace them. The BitLocker adapter invokes the user's existing Hashcat-supplied script without copying or modifying its implementation.

## External extractors

John the Ripper and utilities such as pdf2john, zip2john, rar2john, and 7z2john are optional external programs. Some are GPL-licensed and may have additional component licenses. Obtain and review the license for the exact tool/version you use. HashLynx does not redistribute them or their source. Python and Perl runtimes also remain separately installed tools with their own licenses.

HashLynx's MIT license does not relicense any external software. Process adapters and output parsers in this repository are HashLynx code. PDF and BitLocker extraction implementations remain external; ZIP, RAR and 7-Zip have native implementations described below.

## Native ZIP extractor references

The C# ZIP reader is a HashLynx implementation of the [PKWARE ZIP specification](https://pkware.cachefly.net/webdocs/casestudies/APPNOTE.TXT) and [WinZip AE-1/AE-2 specification](https://www.winzip.com/en/support/aes-encryption/). Its hash serialization is informed by the formats and checksum selection documented in Openwall's [zip2john.c](https://github.com/openwall/john/blob/bleeding-jumbo/src/zip2john.c), consulted September 23, 2026. That individual source file grants permissive redistribution rights separately from John's main program license. Its notice is retained here:

```text
This software is
Copyright (c) 2011-2018 Dhiru Kholia <dhiru.kholia at gmail.com>,
Copyright (c) 2011-2018 JimF, Copyright (c) 2020 Simon Rettberg,
Copyright (c) 2013-2021 magnum,
and it is hereby released to the general public under the following terms:
Redistribution and use in source and binary forms, with or without
modification, are permitted.
```

No John C source files, support libraries, or executable are included in the application. Hashcat's archive module parsers were consulted for interoperability and format limits; no backend module code is bundled. The 7-Zip executable is used only when explicitly selected for development integration tests and is not a runtime dependency or redistributed component.

## Native RAR and 7-Zip references

The native RAR reader implements RAR3/RAR5 metadata parsing using [RARLAB's format documentation](https://www.rarlab.com/technote.htm) and the format/checksum descriptions in Openwall's [rar2john.c](https://github.com/openwall/john/blob/bleeding-jumbo/src/rar2john.c) and [rar2john.h](https://github.com/openwall/john/blob/bleeding-jumbo/src/rar2john.h). The permissive `rar2john.c` notice is retained here:

```text
This software is Copyright (c) 2011, Dhiru Kholia <dhiru.kholia at gmail.com>
and (c) 2012, magnum and (c) 2014, JimF
and it is hereby released to the general public under the following terms:
Redistribution and use in source and binary forms, with or without
modification, are permitted.
```

The native 7z reader is an original implementation of the [7z format specification](https://github.com/ip7z/7zip/blob/main/DOC/7zFormat.txt). The hash field layout documented by philsmd and magnum in [7z2john.pl](https://github.com/openwall/john/blob/bleeding-jumbo/run/7z2john.pl) was consulted for interoperability. No Perl implementation or GPL John support code has been copied or translated into the app.

A small C# LZMA decoder subset from **LZMA SDK 26.03**, by **Igor Pavlov**, is included for compressed archive metadata. **LZMA SDK is written and placed in the public domain by Igor Pavlov.** The SDK is separate from the LGPL 7-Zip application. See [source provenance and scope](src/HashLynx.Extractors/ThirdParty/LzmaSdk/README.md) and the [official SDK license](https://www.7-zip.org/sdk.html). HashLynx adds a bounded, cancellable wrapper; it does not bundle the 7-Zip executable or unRAR library.

Optional RAR integration tests use public known-password fixtures from the `rarfile` and `libarchive` test suites; those downloaded archives remain outside the repository and publish output. The stored RAR3 test uses Hashcat's public mode-23700 self-test data. Normal 7z fixtures are locally generated synthetic archives with a documented test password.

## Rule presets and starter wordlist

The embedded HashLynx rule presets are generated from original recipes in `RulePresetRecipes.cs`, and `assets/wordlists/hashlynx-starter.txt` is an original educational starter list. These assets are covered by the project's MIT license. Local third-party rule and combined wordlist collections were inspected to inform coverage choices, but their files are not redistributed. Custom rule files and wordlists supplied by users retain any applicable upstream terms.

## Development dependencies

The .NET SDK/runtime and WPF are Microsoft/.NET Foundation projects with their own notices. Test projects use xUnit.net (Apache-2.0), the xUnit Visual Studio runner (Apache-2.0), and Microsoft.NET.Test.Sdk (MIT). These test packages are not part of the application runtime. GitHub Actions used for CI retain their respective upstream licenses.

## Branding

The supplied HashLynx PNG logos are retained as project branding. `assets/branding/logo.ico` is a size-converted version of `logo.png`, containing 16, 24, 32, 48, 64, 128, and 256 pixel images. Pillow was used as a local development conversion tool; it is not a runtime dependency or redistributed with HashLynx.
