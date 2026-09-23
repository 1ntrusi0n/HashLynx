# LZMA SDK decoder subset

Source: Igor Pavlov's [LZMA SDK 26.03](https://github.com/ip7z/7zip/releases/download/26.03/lzma2603.7z), downloaded from the [official SDK page](https://www.7-zip.org/sdk.html).

Archive SHA-256: `86c213f752520ab5325c310f50bef63ec344b56dd1c80b0246d06dc6cec953b2`.

Included files are the SDK's `CS/7zip/ICoder.cs`, `Compress/LZ/LzOutWindow.cs`, `Compress/LZMA/LzmaBase.cs`, `Compress/LZMA/LzmaDecoder.cs`, and the three `Compress/RangeCoder/*.cs` files needed to compile that decoder. Only a provenance comment and `#nullable disable` were prepended; source line endings and trailing whitespace are normalized. No encoder implementation, SDK executables or native libraries are included (shared range-coder files contain unused encoder helpers).

The SDK's license statement:

> LZMA SDK is written and placed in the public domain by Igor Pavlov.

HashLynx uses this code only to decode unencrypted 7z metadata. `LzmaHeaderDecoder` bounds the input, dictionary and output, propagates cancellation, rejects premature EOF, and verifies the output size; the 7z reader verifies the metadata CRC. Password derivation, archive payload decompression for recovery and password testing remain in Hashcat. This is not the LGPL 7-Zip application source and does not include the unRAR code.
