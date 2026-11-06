# Hashcat 8 in HashLynx

HashLynx 0.2.0 supports the existing Hashcat 7.1.2 workflows and exposes selected Hashcat 8 features when the installed backend advertises them. Obtain Hashcat separately and keep its complete release together. HashLynx never downloads or changes that release.

## What changed upstream

The [official v8 release notes](https://github.com/hashcat/hashcat/blob/v8.0.0/docs/releases_notes_v8.0.0.md) describe four new attack modes, 146 additional hash modes, new feed generators, broader rules, and many performance improvements. The installed catalog automatically supplies supported hash modes; UI code does not maintain its own list. FPGA support is Linux-only and is not exposed by this Windows application.

The features selected for this release are:

- **Likely passwords (PCFG):** guesses learned password structures in probability order using the backend's bundled `default-passwords` grammar. No wordlist is required. This is a prioritized search, not a guarantee of recovery or coverage of every password.
- **Expert PCFG hints:** uses the bundled `hints` grammar with remembered words. Put the strongest clues first. Hints stay in a local UTF-8 file, and every generated guess uses a clue. Rejected counts can be high because repeated clues are excluded. Length limits or a shape can narrow the search; counts are not estimated before execution.
- **Expert words inside patterns:** place one wordlist marker `?w` inside an ordinary character pattern, such as `?d?d?w!`. An optional `?q` refers to a second wordlist and must follow `?w`. Each marker appears at most once; `?w` is required. This first implementation accepts one inline pattern and does not use increment mode. Ordinary masks do not accept these wordlist markers.
- **Rules on more attacks:** v8 supports additional attack families. The optional separate-list mode adds rule counts; default chaining multiplies transformations. Existing saved jobs retain their chosen behavior.

PCFG controls expose the bundled models and focused length/shape options, not arbitrary feed settings. Tables, account-aware association, custom hash recipes and bridge/plugin configuration remain deferred.

## Upgrade behavior

Hashcat 8 no longer runs a session when invoked with `--restore`; it prints a command. HashLynx never executes that text. It rebuilds managed arguments from the saved job and requests the checkpoint position after validating provenance, inputs and runtime location. Changed inputs or incompatible checkpoint data require a new attempt. Hashcat 7 uses restore format 611, while v8 requires format 720; older files remain untouched. Select the matching backend or start a fresh session. Stopping a process cannot guarantee a new checkpoint.

Hashcat 8 ignores workload profiles and tunes automatically. The associated controls are disabled without erasing older preferences. Explicit device selection excludes the removed accelerator type. Removed options and ZIP-compressed wordlist/hash/rule inputs receive validation guidance. Mode aliases from older catalogs remain recognized when their replacement is installed.

Managed runtime workspaces now include `feeds`, `pcfg`, and `tables`, with versioned staging and support-asset identity. Original caches are excluded. Older workspaces remain available for saved sessions; do not delete them while checkpoints depend on them.

Relevant upstream details: [PCFG](https://github.com/hashcat/hashcat/blob/v8.0.0/docs/hashcat-pcfg.md), [command help](https://github.com/hashcat/hashcat/blob/v8.0.0/docs/hashcat-help.md), [restore code](https://github.com/hashcat/hashcat/blob/v8.0.0/src/restore.c), and [exit codes](https://github.com/hashcat/hashcat/blob/v8.0.0/docs/exit_status_code.txt).
