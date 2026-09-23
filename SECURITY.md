# Security policy

HashLynx is for password recovery, authorized auditing, research, lab work, and CTFs. Only work with data and systems you own or are authorized to test.

Report security vulnerabilities privately through the repository's [GitHub vulnerability reporting page](https://github.com/1ntrusi0n/HashLynx/security/advisories/new) when private reporting is enabled. If unavailable, open a minimal issue requesting a private contact channel; do not publish exploit details before coordinating with the maintainer.

**Do not include real hashes, passwords, encrypted private documents, potfiles, session files, credentials, or secrets in public issues.** Provide a minimal synthetic reproduction, HashLynx/Hashcat versions, and a sanitized description. Review any diagnostic attachment before sharing it.

The 0.1.x line is an early development release. Updates are provided on a best-effort basis; there is no supported long-term maintenance branch yet.

HashLynx operates locally and does not upload recovery data or telemetry. Settings and job metadata are stored under the current user's LocalApplicationData folder. Target copies, restore files, output files, and Hashcat potfiles may contain sensitive data and are not encrypted by HashLynx. Protect your Windows account and disk accordingly. External extractors execute with your user permissions; configure only trusted tools obtained separately from their publishers.
