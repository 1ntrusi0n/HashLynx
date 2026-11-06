# Job progress and timing

Jobs displays the physical line count of the configured wordlist files and the percentage of password-check work reported by Hashcat. Blank and duplicate lines contribute to the file line count. Rules, multiple hashes, rejected guesses, and other attack settings can make the work counter differ from that count. The interface does not present a checkpoint position as a literal number of file lines tested.

Line counts run asynchronously with bounded memory. Existing wordlist-library counts are reused only when file size and modification time still match. New sessions compare source metadata against their recorded inputs. Counts for older sessions without recorded metadata describe the current files, with an explanation in Jobs. Counting never changes the source or blocks a recovery process.

The local time estimate combines the recent checking rate with earlier progress in the same pass. Hashcat's own estimate can supply an initial value while enough observations accumulate. Estimated active duration is the observed active time plus the estimated time remaining. Setup, pauses, and work before a restored checkpoint are excluded. The elapsed field includes wall-clock time since the current launch. Finding the password can finish the attempt early.

Estimates stop during pauses, stale updates, or completed/interrupted sessions. A changed source, pattern, work total, or restored process starts a fresh measurement. Saved summaries are not resumed as live countdowns. Multi-pass estimates cover the current pass; when sequential dictionary source indices match the configured files, overall percentage is an approximation weighted by file line counts. Combined-source feeds and other ambiguous cases retain current-pass scope.

Only counts, timestamps, durations, and existing source-file metadata are stored locally. No candidates, hashes, or recovered passwords are added to timing history or diagnostics.

## Upstream semantics

- [Hashcat 7.1.2 JSON status](https://github.com/hashcat/hashcat/blob/v7.1.2/src/terminal.c#L2670-L2785)
- [Hashcat 8 JSON status](https://github.com/hashcat/hashcat/blob/v8.0.0/src/terminal.c#L3273-L3376)
- [Progress and restore counters](https://github.com/hashcat/hashcat/blob/v8.0.0/src/status.c#L1464-L1604)
- [Source queues and combined-source feeds](https://github.com/hashcat/hashcat/blob/v8.0.0/src/straight.c#L213-L278)

The JSON source counts and indices describe the source queue, not the number of wordlist lines. Hashcat's progress includes completed, rejected, and restored work. Its raw source-description strings are deliberately not retained by the parser because other attack families can place password-related input there.
