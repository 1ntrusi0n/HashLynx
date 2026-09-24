# Wordlist library and tools

The saved wordlist picker remembers friendly names and paths. Expand **Manage wordlist library** to rename an entry, refresh file size and line counts, or **Repair location** when a file has moved. Renaming changes the library label, not the file on disk. Repairing updates the library reference; select that entry again to apply its new path to an attack. **Forget selected** removes only its library entry.

Counts run in the background and can be cancelled. They count physical lines, including blanks and duplicates, with a final unterminated line included. Counts are cached against file size and last-write time. A missing or unreadable file remains in the library and is identified for repair. Metadata is optional in the existing version-one `wordlists.json` format, so older saved libraries load without conversion and malformed libraries remain preserved.

To create a combined or filtered list:

1. Check **Use** beside one or more saved lists.
2. Choose minimum and maximum byte length. ASCII characters each occupy one byte; UTF-8 characters may occupy several.
3. Choose whether to remove exact duplicates. Comparison is case sensitive and byte exact. Duplicate removal sorts the result; without it, source-list and line order are retained.
4. Choose **Save new wordlist** and a new filename. HashLynx refuses to overwrite an existing file, including any source list. The completed file is added to the library.

The transformation keeps candidate bytes unchanged, normalizes CRLF/LF line endings to LF, and removes an initial UTF-8 byte-order mark. UTF-16 and UTF-32 input is rejected with guidance to convert it to UTF-8. A single candidate line is limited to 1 MiB. Empty lines are retained when the minimum length is zero. Hashcat `$HEX[...]` entries are treated as literal file bytes for filtering and duplicate comparison; they are not decoded by these tools.

Large transformations sort in bounded chunks, spill those chunks under the application's local cache, and merge at most 24 runs at a time. Temporary disk space roughly proportional to the processed input is required, in addition to space for the new output. The operation is cancellable. It checks for input size or timestamp changes, publishes the completed output through a non-overwriting rename, and cleans up its own temporary files on handled failure or cancellation. A forced application/process shutdown may leave temporary files in `cache/wordlist-tools`; original lists are never written.
