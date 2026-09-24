# Recovery workflows

Recovery workflows are part of the normal application on `main`. Launch `artifacts/publish/win-x64/HashLynx.exe`; it uses `%LOCALAPPDATA%\HashLynx` and resumes the normal settings, library, profiles and session history. Earlier recovery-workflow test data under `%LOCALAPPDATA%\HashLynx\Experiments\RecoveryWorkflows` is preserved separately, including its queues and results, without automatic import. Older BitLocker-experiment data is also preserved.

## Password hints

On Attack, enable **Expert mode**, select a target and expand **What do you remember?**. Enter remembered words/phrases, one per line. They are tried unchanged by default; optional Quick and Normal steps add variations. A separate pattern describes a known beginning, unknown middle, known ending and inclusive total length range. Known text is literal (including `?`); generated masks preserve both ends for every length instead of shortening the suffix with increment mode.

For example, beginning `Summer`, ending `!`, total length 11 and digits in the middle produces 10,000 candidates from `Summer0000!` through `Summer9999!`. An optional ending can be expressed as a second sequence without that ending. These are explicit patterns, not natural-language guesses. Pattern text currently supports printable ASCII; word/phrase files preserve UTF-8 and spaces. There are at most 200 distinct words, 128 UTF-8 bytes per word, total pattern lengths 1-32, and at most 16 lengths per preview.

Preview shows attempts, example candidates and a candidate-application estimate. Rule tiers overlap, so counts are an effort ceiling, not distinct guesses or a recovery probability. Very large estimates are called out. Changing any hint, including invalid numeric text, invalidates the preview. Queueing rechecks the preview after asynchronous target validation. Generated candidate files stay in the local `hints/` directory for replay; they are not written to diagnostic logs. Expert options that reinterpret, truncate or skip the previewed candidates are rejected. Autohex decoding is disabled for generated word files so a literal `$HEX[...]` is still tried literally.

## Queue behavior

Enable **Expert mode** on Attack to show **Run several attempts**. Its **Add current attack to queue** action snapshots the selected target and attack configuration. To append to a sequence, select it on Queue first and enable **Append current attack** on Attack. Appending requires the same target bytes and mode and an unstarted sequence. **Queue No Rules, Quick, Normal** creates a three-step dictionary sequence from the selected wordlist(s), without custom rules or loopback. The hints wizard builds its own sequence. The Queue page and saved sequences remain available in Basic mode.

Review the Queue page and choose **Start queue**. Only one compute operation runs at a time. Steps have independent sessions, checkpoints and result files; a sequence shares its own potfile to skip targets recovered by earlier steps. It does not import the shared recovery cache used by standalone attacks. Exhaustion advances to the next step, complete recovery skips the rest of that sequence, and errors/stops pause the queue. Other sequences retain their own targets and caches.

**Pause after current step** lets the active attempt finish. To stop it too, use **Stop** in Jobs; this pauses the queue before terminating the process. Restart always loads the queue paused. Interrupted steps stay blocked for review. Restore their sessions in Jobs when a checkpoint exists, then start the queue to reconcile the result; or choose **Retry step** to create a new attempt from its beginning, keeping prior results. **Skip step**, **Move step up/down**, and **Remove sequence** are available while idle. Reordering is limited to pending steps within a sequence. Removal keeps its sessions and local files.

The target is copied at enqueue time. Wordlists, custom-rule files and mask files remain references and must remain available; changing their contents changes a later attempt. Built-in mask lists retain a versioned preset ID and are materialized automatically. Queue persistence is atomic and versioned, and malformed or future-schema data is preserved and reported. A failed save stops advancement. A pause or app close during pre-launch saving is rechecked before any process starts. When no device is selected, automatic hardware verification happens before recovery; pausing during that check cancels it and leaves an interrupted step that can be retried.

## Other additions

- **Hardware > Test recovery on this device** performs a bounded known-answer check, separate from real jobs and results. See [hardware checks](hardware-check.md).
- **Mask > Built-in common structures (1,000 masks)** works without a mask file. The picker shows candidate count and a short mask explanation. See [selection, sources and limits](built-in-masks.md).
- Completion banners offer result/session navigation and explain partial recovery, exhaustion and failure. **Settings > Show Windows completion notifications** enables transient Windows notification-area balloons; Windows notification settings can suppress their display. Notifications contain generic status only, with no passwords, candidate text or targets.
- **Manage wordlist library** adds names, counts, location repair and new-file combine/filter/deduplicate tools. See [wordlist tools](wordlist-tools.md).
- Encrypted Office and KeePass files use built-in readers through Encrypted File. See [selected formats and limitations](extractor-expansion.md).

## Suggested manual checks

1. Open the normal executable and confirm Settings shows `%LOCALAPPDATA%\HashLynx`. Existing normal history should be available; earlier experiment histories should remain separate.
2. Confirm **What do you remember?** and **Run several attempts** are hidden in Basic mode and shown in Expert mode. In Expert mode, choose a test target with a known password, enter matching hints, preview, add to Queue and start it. Confirm the recovered password in its session results.
3. Queue a list that misses the password followed by one that contains it. Check that the first exhausts, the second recovers, and later steps in that sequence are skipped.
4. Pause between attempts and restart the app. Confirm the saved queue waits for you to start it.
5. Test a selected hardware device, rename a saved wordlist, and create a combined list at a new path.
6. Try your own supported Office/KeePass test files; retain their originals and compare recovered passwords with what you set.

Use `scripts/build.ps1 -Publish` for restore/build/unit tests/UI checks and publication to `artifacts/publish/win-x64`. Backend-enabled tests must use a disposable Hashcat release; existing recovery tests use an explicitly selected device. The additional `HASHLYNX_TEST_AUTO_DEVICE=1` WPF check exercises discovery and verified automatic selection with empty device preferences. Automated checks use synthetic/public fixture passwords, never the user's recovery targets.
