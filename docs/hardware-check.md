# Hardware recovery check

Open **Hardware**, refresh devices, and choose **Test recovery on this device** on the CPU or GPU you want to verify. The check can be cancelled and cannot start while another recovery or hardware check is running. A successful check does not change your selected default device; use **Use this device by default** separately.

The check asks the configured Hashcat installation to recover a newly generated sample on exactly the chosen device. A pass requires both a successful process exit and the expected recovered bytes in the check's own output. Merely listing a device or returning a successful exit code does not pass. A sample pass verifies basic MD5 recovery, not performance, stability over long runs, or support for every encrypted format.

Each check uses a private directory beneath the application cache for its synthetic hash, two-entry wordlist, and result. It disables potfiles, restore files and Hashcat logs, and uses a unique session name. These files are removed when the check ends. They never appear in job history or recovered user results. No user target, wordlist or potfile is used. The configured Hashcat release is preserved; support files and mutable backend caches use HashLynx's existing runtime workspace.

With no saved or explicit recovery device, new jobs use this same check automatically. HashLynx discovers devices, tests GPUs first, then CPUs and other devices, and chooses the first verified device. Jobs displays **Checking hardware** and enables **Stop** while checking. A queue pause or shutdown cancels pending checks before recovery can launch. If every device fails, the session explains that no attack started and points to Hardware for individual diagnostics. Explicit device selections and saved restore sessions retain their selected devices.

The verified automatic choice is cached in memory for this app session. Every automatic launch refreshes discovery and compares the complete reported device list, including IDs and driver information. Changes, a failed recovery, reconnecting the backend, or refreshing Hardware invalidate verification. Automatic selection does not change saved Hardware preferences. Its selected ID is saved with the job so its session and checkpoint retain that device.

The device runs at Hashcat workload profile 1. The sample has a 30-second backend runtime limit and a 90-second overall deadline, including runtime preparation and device initialization. Cancellation or timeout terminates the child process. A first-time kernel compilation may exceed that deadline; retry once before investigating drivers. Failure messages summarize recognized runtime errors without displaying raw backend output, target data or candidates. HashLynx does not bypass driver checks with `--force`, install drivers or change device settings.

Normal service tests inject process results and do not require Hashcat or a GPU. The optional integration test uses `HASHLYNX_TEST_HASHCAT` pointing at a disposable backend copy and `HASHLYNX_TEST_DEVICE` identifying the device explicitly selected for testing:

```powershell
dotnet test tests/HashLynx.Hashcat.Tests -c Release --filter FullyQualifiedName~HardwareCheckIntegrationTests
```

The WPF smoke harness also exercises failed verification, Stop, queue pause and shutdown using injected checks. Set `HASHLYNX_TEST_AUTO_DEVICE=1` with a disposable `HASHLYNX_TEST_HASHCAT` to test real automatic selection, dictionary recovery without rules, and a short known-password recovery from the built-in mask list. This opt-in path tests available devices rather than requiring a saved device ID, and keeps its sessions and synthetic results in isolated smoke data.
