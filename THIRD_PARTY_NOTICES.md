# Third-party notices

HashLynx is an independent application. Hashcat is a separate third-party project, is not developed by HashLynx, and does not sponsor or endorse this frontend.

## Hashcat

[Hashcat](https://hashcat.net/hashcat/) is an MIT-licensed backend. Users separately obtain an official release and configure it. The local `hashcat/` folder is ignored by Git and is not included in this repository or the application publish output. Hashcat's own `docs/license.txt` and `docs/license_libs/` contain its copyright and dependency notices; do not remove or replace them. The BitLocker adapter invokes the user's existing Hashcat-supplied script without copying or modifying its implementation.

## External extractors

John the Ripper and utilities such as pdf2john, zip2john, rar2john, and 7z2john are optional external programs. Some are GPL-licensed and may have additional component licenses. Obtain and review the license for the exact tool/version you use. HashLynx does not redistribute them or their source. Python and Perl runtimes also remain separately installed tools with their own licenses.

HashLynx's MIT license does not relicense any external software. Process adapters and output parsers in this repository are HashLynx code; the extraction implementations remain external.

## Development dependencies

The .NET SDK/runtime and WPF are Microsoft/.NET Foundation projects with their own notices. Test projects use xUnit.net (Apache-2.0), the xUnit Visual Studio runner (Apache-2.0), and Microsoft.NET.Test.Sdk (MIT). These test packages are not part of the application runtime. GitHub Actions used for CI retain their respective upstream licenses.

## Branding

The supplied HashLynx PNG logos are retained as project branding. `assets/branding/logo.ico` is a size-converted version of `logo.png`, containing 16, 24, 32, 48, 64, 128, and 256 pixel images. Pillow was used as a local development conversion tool; it is not a runtime dependency or redistributed with HashLynx.
