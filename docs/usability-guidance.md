# Readiness and outcomes

The Attack page keeps the normal workflow: select a target and inputs, then Start recovery. Password hints and queue-building controls remain Expert-only, while the Queue page and saved wordlist library remain accessible in Basic mode.

## Ready to recover

The checklist checks whether the target is supplied, whether its type has been selected, and whether the selected wordlist/rule/mask inputs are available. A selected encrypted file still needs extraction and analysis. Green text indicates readiness, red indicates missing inputs or required corrections, and amber indicates pending analysis or device checks. The colors remain readable in Light, Dark and System themes, and each item also explains its state in text.

The checklist never treats device discovery as proof that a recovery kernel works: an earlier successful sample is described as such; automatic selection otherwise explains that its sample runs before recovery. Start performs the final validation.

Review target and Choose inputs scroll to their controls. Check hardware and Set up Hashcat navigate to the appropriate page. Refresh checks handles files moved, removed or reconnected outside HashLynx. Checks run away from the WPF dispatcher, support cancellation and discard results when the draft changes.

## Wordlist selection and saved data

Use starter selects the bundled educational wordlist. Add wordlists accepts local files and remembers their locations in the saved wordlist library. Selecting a saved list avoids browsing for it again. Library management, missing-file repair and new-file transformations remain available.

Earlier saved attack profile files remain untouched for compatibility, although Attack no longer exposes profile save/load/delete controls. Local source rule and wordlist collections remain unmodified. The application keeps existing session histories, queues and results in their current workspaces.

## Outcomes and next actions

Saved backend states remain unchanged for compatibility. Jobs, completion banners and queue feedback distinguish a completed search with no new saved password, partial recovery, backend failure, interruption and an available checkpoint using plain language. Failure remedies navigate to the relevant page without restarting recovery. Recorded backend diagnostics remain expandable under Technical details.

Only a complete, valid record in the session's own output proves a session recovery. Ambiguous shared legacy output paths and unreadable output remain unknown. A backend Cracked state can mean matches were already cached; Check earlier results explicitly opens the all-sessions view, without importing potfile matches or assigning earlier passwords to the selected session.

## Validation

Normal unit tests cover readiness states, outcome mapping and result ownership without requiring a backend or GPU. The WPF smoke harness checks the checklist, missing inputs, draft changes, navigation, Basic/Expert visibility, narrow layouts and theme bindings. Opt-in connected checks use synthetic known-answer targets, a disposable backend and isolated application data.

Validated on 2026-09-25 with `scripts/build.ps1`: restore succeeded, the Release build had no warnings or errors, 521 tests passed, and 13 opt-in integration tests were skipped. The offline WPF smoke check passed with zero binding errors, including readiness colors in Light/Dark themes and the simplified attack controls.
