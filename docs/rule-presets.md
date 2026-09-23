# Built-in dictionary rules

HashLynx defaults Dictionary attacks to **No Rules**, which tries each word unchanged and passes no rule file to Hashcat. Four optional original rule presets are also available. The names describe increasing work per input word; they do not claim a measured recovery rate or guarantee success.

| Preset ID | Display name | Unique rule lines | Coverage |
| --- | --- | ---: | --- |
| `quick-v1` | Quick | 64 | Identity, case, reverse/duplicate, digits, six symbols, short numeric endings, and single substitutions |
| `normal-v1` | Normal | 512 | Quick plus two-digit endings, years 1980–2035, single-digit prefixes, and extra case/substitution combinations |
| `heavy-v1` | Heavy | 4,096 | Normal plus three-digit endings, older years, two-digit prefixes, number/symbol combinations, and selected insert/overwrite/toggle operations |
| `super-v1` | Super | 16,384 | Heavy plus all four-digit endings, three-digit/year prefixes, paired substitutions, and additional numeric case variants |

Each file starts with the smaller tier's rules in the same order. Every tier includes `:`, which preserves the original word. Duplicate rule strings are removed during generation. Different rules may still produce the same candidate for a particular word, and some transformations cannot apply to short words.

## How rules work

A rule is a short program applied to each input word. Multiple operations on one line run in order.

| Rule | Operation | Input | Candidate |
| --- | --- | --- | --- |
| `:` | Leave unchanged | `river` | `river` |
| `c` | Capitalize | `river` | `River` |
| `$1` | Append `1` | `river` | `river1` |
| `c$1$!` | Capitalize, append `1`, append `!` | `river` | `River1!` |
| `sa@` | Replace `a` with `@` | `maple` | `m@ple` |

When a preset is selected, HashLynx passes exactly one built-in rule file to Hashcat. Quick, Normal, Heavy, and Super are cumulative unions, so selecting a larger preset adds coverage without multiplying separate tiers together. In Expert mode, custom rule files replace the preset. Multiple custom `--rules-file` arguments use Hashcat's combination semantics: two files containing 100 and 200 rules can yield 20,000 combined transformations per word. An empty custom list runs the dictionary without rule transformations.

## Choosing an effort level

Start with No Rules to try the original words. If you want variations, choose Normal or Quick, especially for a large list or a slow hash type. Heavy and Super make more sense with a focused list. The 848-word starter list produces up to 54,272, 434,176, 3,473,408, or 13,893,632 candidate applications across the four tiers. These figures do not predict runtime: hash type, hardware, rejected candidates, and duplicate outputs matter.

No representative authorized recovery benchmark was supplied, so the presets have not been ranked by recovery effectiveness. Tests cover deterministic content, counts, command integration, and optional installed-Hashcat checks. Future effectiveness tuning should use a documented benchmark and new preset versions.

## Source collection review

The local review covered 364 rule files/chunks totaling 699,576,236 bytes and 53,176,454 nonblank, non-comment lines, counted before deduplication or syntax validation. Smaller files emphasized identity, case changes, common numeric/symbol endings, and substitutions; large generated collections contributed many position insertions/overwrites and prefix/suffix combinations. The fixed budgets favor compact common transformations first and add broader combinations in later tiers. Large generated collections were not concatenated into the application.

The provided combined wordlist contained 47,427,316 lines and 530,555,519 bytes. The application selects that file in place when present; it does not bundle it. The separate 848-word starter is original project content, intended to make the interface usable immediately and explain the workflow.

Source collections under root `rules/` and `wordlist/` remain unmodified and Git-ignored. Preset recipes were authored independently using documented rule operations; no upstream rule file or source filename is needed at runtime. The generated assets and starter list use HashLynx's MIT license.

## Reproduction and persistence

Run from the repository root:

```powershell
dotnet run --project scripts/HashLynx.RulePresetGenerator -c Release -- src/HashLynx.Hashcat/RulePresets
dotnet test tests/HashLynx.Hashcat.Tests -c Release
dotnet test tests/HashLynx.Persistence.Tests -c Release
```

Optional installed-Hashcat checks:

```powershell
$env:HASHLYNX_TEST_HASHCAT = (Resolve-Path .\hashcat\hashcat.exe).Path
dotnet test tests/HashLynx.Hashcat.Tests -c Release --filter Category=Integration
```

That check loads each preset through Hashcat's `--total-candidates` path and verifies its budget without requiring a usable compute device. A separate, stricter check compares actual candidates using `--stdout --outfile`; enable it by also setting `HASHLYNX_TEST_HASHCAT_CANDIDATES` to the executable path on a machine with a working compute runtime. Set `HASHLYNX_TEST_DEVICE` to a device ID (or comma-separated IDs) when explicit selection is needed. Both checks prepare an isolated backend workspace and leave the original release untouched. All four emitted-candidate checks passed on the Intel CPU after its runtime update; see [validation evidence](validation.md).

`RulePresetRecipes.cs` is the source of truth. Generation is deterministic and tests compare embedded assets with the recipe output. The four files total about 173 KB before assembly packaging. They are embedded in the Hashcat integration assembly; no separate source-rule checkout or asset download is required.

At use time, the app materializes versioned files in its per-user `rule-presets/` directory and restores missing or modified managed files from the embedded originals. The starter is similarly materialized under `wordlists/`. These operations never write to the supplied Hashcat release or original source collections.

Profiles store `RulePresetId`, not a managed absolute rule path. Existing profiles without that field retain their custom `RuleFiles` behavior, including an empty no-rule list. Profiles containing custom rule files open Expert mode; empty no-rule profiles select No Rules without requiring Expert mode. Unknown preset IDs fail with an actionable message instead of silently substituting a different recipe. A saved preset and custom files together are rejected.

Treat published preset IDs as immutable. If coverage changes, add a new version and retain older embedded assets so saved jobs and profiles can resolve their original recipe.
