# BitLocker drive-reader experiment

This feature lives on `experiment/bitlocker-drive-reader`. `main` remains the stable version; the experiment is not merged automatically. The published test app is under `artifacts/publish/bitlocker-drive-test/`. Its window says **HashLynx TEST - BitLocker drive reader**.

## Trying it

1. Open the test app and choose **Attack > BitLocker Drive**.
2. Click **Refresh drives** and select the volume by drive letter, label and capacity. The list includes local fixed/removable volumes; BitLocker/password support is determined during extraction, not guessed from a filesystem label. Locked volumes may have no readable label or size.
3. Click **Extract and analyze (admin)** and approve Windows' UAC prompt for `HashLynx.DriveReader`. The main app and Hashcat stay unelevated. The helper currently requires elevation as the same Windows user; supplying a different administrator account is unsupported.
4. A supported password record is saved as a normal local target and identified by Hashcat. Select a wordlist, optional rules, and **Start recovery**. Recovered results use the existing session-specific Results page.

For a controlled end-to-end check, create a temporary local wordlist containing the known test-device password, choose **No Rules**, and run recovery. There is no need to share the password with the developer. Extraction and mode identification alone do not verify the password.

All test histories, targets, results and profiles live under `%LOCALAPPDATA%\HashLynx\Experiments\BitLockerDrive`. First launch copies only preferences from the normal workspace, including the saved compute-device choice; subsequent preference changes are independent. The stable app's existing sessions and profiles are not copied or modified. Keep the whole test build folder together, including `DriveReader/`.

## Scope and limits

- Reads ordinary user-password protectors with the existing native BitLocker parser and Hashcat mode 22100. TPM/PIN, startup keys and recovery-password protectors remain unsupported.
- Supports mounted volumes with drive letters and a single contiguous physical-disk extent. Complex storage layouts, volumes without drive letters and virtual-container discovery are outside this experiment.
- Uses physical-disk reads constrained to the selected volume's extent, preserving on-disk encrypted metadata when the filesystem is unlocked. It never unlocks, dismounts, disables encryption, changes protectors, or writes to the volume.
- Reads at most 16 MiB in sector-aligned operations; each parser request is at most 1 MiB. The helper has a 95-second hard lifetime, cancellation/disconnection handling, and no persistent service.
- Repeats extraction and compares results plus the volume extent. Changes during encryption/protector updates are rejected where detected. This is not an atomic volume snapshot; pause testing until conversion or administrative changes have finished.
- On the tested Windows-created removable volume, the legacy BPB sector-size field was zero. The live reader uses Windows' logical-sector geometry for that case and rejects conflicting nonzero sizes. Raw images without device geometry retain the existing validation.

## Privilege boundary

`HashLynx.Drives` contains discovery, the unelevated client and read-only Windows device access. `HashLynx.DriveReader` is a small `requireAdministrator` executable. The client starts only that bundled executable with `ShellExecuteExW`, `runas`, a generated pipe name and numeric parent PID. It does not invoke a command interpreter or use target data as launch arguments. This is the narrowly scoped, user-authorized exception to the normal no-elevation policy.

The pipe has an explicit current-user ACL and bounded length-prefixed JSON (128 KiB maximum). The client checks the launched helper's PID; the helper checks the parent/server PID and connects with identification-only impersonation. Requests accept only canonical local volume GUIDs, with no filenames, output paths, offsets, external commands or device-control operations. Only extracted hash records and sanitized status cross back to the unelevated client. The helper writes no logs or target files.

Windows opens the chosen volume for metadata queries and the physical disk with **GENERIC_READ**. Its disk controls are limited to querying extents, geometry and length. Partition offsets, aligned read sizes, total read budget and physical-disk bounds are checked before reading. Output files are created by the unelevated app under its own experimental workspace.

References: [Windows direct disk/volume access](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilea#physical-disks-and-volumes), [volume disk extents](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-volume_disk_extents), [administrator broker model](https://learn.microsoft.com/en-us/windows/win32/secauthz/administrator-broker-model), and [named-pipe client PID verification](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid).

## Validation

Normal tests do not access devices or request elevation. They cover strict volume/pipe identifiers, framed message bounds/truncation, partition alignment/bounds/budgets, shared parser behavior and zeroed BPB handling. WPF smoke uses a fake drive service to check explicit selection, extracted targets, cached preparation, cancellation, stale-result rejection, unsupported protectors and device removal.

The opt-in live test requires an explicitly chosen test volume and prompts for UAC. It reads metadata, verifies Hashcat identification and deletes its temporary target afterward. It never runs a password attack or prints extracted hashes:

```powershell
$env:HASHLYNX_TEST_BITLOCKER_DRIVE = 'E:' # explicitly confirm the intended test volume
$env:HASHLYNX_TEST_DRIVE_READER = 'G:\Coding\HashLynx\artifacts\publish\bitlocker-drive-test\DriveReader\HashLynx.DriveReader.exe'
$env:HASHLYNX_TEST_HASHCAT = 'G:\Coding\HashLynx\artifacts\hashcat-smoke\hashcat.exe'
dotnet test tests/HashLynx.Integration.Tests -c Release --filter FullyQualifiedName~LiveBitLockerDriveTests
```

On 2026-09-23 the user-designated 16 GB removable test volume yielded one password record after 4,096 physical metadata bytes (including repeated reads); Hashcat 7.1.2 identified mode 22100. This establishes live extraction and format recognition. Recovery using that volume's known password remains a separate user test. No device metadata or credentials are checked into Git.
