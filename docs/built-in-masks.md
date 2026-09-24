# Built-in common password structures

Choose **Mask → Built-in common structures (1,000 masks)** to use HashLynx's bundled mask selection without supplying a file. Custom mask text and custom `.hcmask` files remain available. The list applies to complete-password mask attacks; hybrid attacks still use a user-defined mask. Saved profiles and queue steps retain the immutable ID `common-1000-v1`.

## Scope and effort

The list contains exactly **1,000 unique ASCII structures**, with lengths from **1 to 11 characters**. It tries number-only, lowercase, uppercase, mixed-case and symbol-containing structures. A mask describes character classes, not a particular password: `?l?l?d?d` tries two lowercase letters followed by two digits; `?u?l?l?l?l?d` tries an uppercase letter, four lowercase letters and a digit. Hashcat defines these tokens in its [mask attack documentation](https://hashcat.net/wiki/doku.php?id=mask_attack).

There are **971,288,570,427 possible candidates** across the list. This is a full-combination upper bound, before format restrictions or backend candidate filtering. The catalogue multiplies class sizes using `BigInteger`: `?l` and `?u` each contain 26 ASCII letters, `?d` contains 10 digits, and `?s` contains 33 printable ASCII symbols including space. Different structures here are disjoint, so their products can be summed without counting overlaps. Increment is disabled for this list because its entries already specify lengths; increment would repeat work and change this count.

One thousand masks does **not** mean one thousand guesses. At a hypothetical constant 1,000,000 candidates/second, all combinations would take about 11.2 days; at 1,000/second, about 30.8 years. These are arithmetic illustrations, not measured speeds or time-to-recovery promises. Encrypted archives, documents and drives can be slow. Remembered hints or a focused wordlist often make a much smaller first attempt. During a multi-mask run, Hashcat can report progress for the current structure rather than the entire list.

## Research, selection and limitations

The upstream aggregate source is Hashcat **v7.1.2**, commit [`c75f446c44cd3f0742035a1394416c39bee5ea8f`](https://github.com/hashcat/hashcat/tree/c75f446c44cd3f0742035a1394416c39bee5ea8f), file [`masks/rockyou-2-1800.hcmask`](https://github.com/hashcat/hashcat/blob/c75f446c44cd3f0742035a1394416c39bee5ea8f/masks/rockyou-2-1800.hcmask). This contains 2,968 password structures, not raw passwords, names, accounts or hashes. Only the published aggregate mask file and its license were downloaded for this feature.

HashLynx takes the **first 1,000 distinct structures in upstream order**, retaining that curated priority. This is a reproducible selection from a historical, corpus-derived mask collection, **not a measured global top-1,000 frequency ranking**. The upstream file supplies neither occurrence counts nor a reproducible scoring formula, so HashLynx does not invent frequencies, coverage percentages or a numeric popularity score. We also do not interpret `1800` in the upstream filename as a runtime promise on the user's hardware.

The source was selected because it is a small, established, permissively licensed structure collection distributed by Hashcat itself. [PACK's author](https://github.com/iphelix/pack) explains the tradeoff between frequency-only and frequency-versus-keyspace ordering and warns that a corpus from one population may not reflect another population or password policy. Hashcat's [mask documentation](https://hashcat.net/wiki/doku.php?id=mask_attack#hashcat_mask_files) likewise describes ordering by runtime and likelihood and points to PACK and the shipped examples. These references inform the choice to preserve the curated order; they do not establish per-entry frequencies for this subset.

This historical RockYou-derived selection is limited to its source population and ASCII structure patterns. It is not a current worldwide popularity survey, does not cover every length or password policy, and cannot express word semantics. Some common but expensive long structures are outside the first 1,000 entries. The UI therefore calls it **Common patterns**, and makes the search size visible rather than claiming universal coverage.

## Versioning, reproduction and licensing

The checked-in aggregate source is `scripts/mask-sources/hashcat-v7.1.2-rockyou-2-1800.hcmask`. Its SHA-256 is `622b4ec063f59c24d0fea27fc553ebb7484882cb0ef676f4edddacc616e30494`. The pinned [Hashcat MIT license](https://github.com/hashcat/hashcat/blob/c75f446c44cd3f0742035a1394416c39bee5ea8f/docs/license.txt) is retained in `scripts/mask-sources/HASHCAT-LICENSE.txt` and in full as comments in the generated asset. No PACK implementation is copied.

Regenerate offline with:

```powershell
py -3 scripts/generate-mask-presets.py
```

The script verifies both source checksums, selects first occurrences in order, validates supported tokens, and writes canonical ASCII/LF output. The output `src/HashLynx.Hashcat/MaskPresets/common-1000-v1.hcmask` has SHA-256 `1f69fc0f7ca02497256c0f4426b128c16212914c3191ed1dbf5fcf5262b777b2`. Its byte integrity is checked when embedded data is loaded. Keep this version unchanged when adding future lists; add a new ID and preserve old versions for saved profiles and queues.

The application materializes this licensed embedded asset under its own `mask-presets` data directory. On the recovery-workflows experiment this is `%LOCALAPPDATA%\HashLynx\Experiments\RecoveryWorkflows\mask-presets`. The canonical asset repairs changes to its managed cache; it never rewrites user-selected mask files or the backend installation. There is no runtime download, Python dependency, or transmission of user data.

## Validation

The connected WPF smoke check recovered a synthetic password using the actual built-in mask file with Hashcat v7.1.2. It began in Basic mode with empty device preferences, verified automatic selection of CPU device 3, and checked the session's recovered bytes. A separate dictionary attempt without rules also recovered its known password. These checks validate execution and result isolation, not the list's recovery rate or completion time on real targets.

Normal tests check 1,000 distinct valid structures, exact candidate totals, arbitrary-precision arithmetic, version integrity, cancellation, cache repair, license retention, command argument boundaries, legacy profile behavior, incompatible selections and input/output collisions. An opt-in test passes representative boundary-length and character-class entries to a disposable Hashcat release using `--total-candidates`, checking its reported totals against independently calculated combinations. Hashcat 7.1.2 does not accept mask files with that switch, so these checks use individual masks. This parser check does not require or initialize a compute device and does not attempt password recovery.
