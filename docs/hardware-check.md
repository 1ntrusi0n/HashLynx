# Hardware recovery check

Open **Hardware**, refresh devices, and choose **Test recovery on this device** on the CPU or GPU you want to verify. The check can be cancelled and cannot start while another recovery or hardware check is running. A successful check does not change your selected default device; use **Use this device by default** separately.

The check asks the configured Hashcat installation to recover a newly generated sample on exactly the chosen device. A pass requires both a successful process exit and the expected recovered bytes in the check's own output. Merely listing a device or returning a successful exit code does not pass. A sample pass verifies basic MD5 recovery, not performance, stability over long runs, or support for every encrypted format.

Each check uses a private directory beneath the application cache for its synthetic hash, two-entry wordlist, and result. It disables potfiles, restore files and Hashcat logs, and uses a unique session name. These files are removed when the check ends. They never appear in job history or recovered user results. No user target, wordlist or potfile is used. The configured Hashcat release is preserved; support files and mutable backend caches use HashLynx's existing runtime workspace.

The device runs at Hashcat workload profile 1. The sample has a 30-second backend runtime limit and a 90-second overall deadline, including runtime preparation and device initialization. Cancellation or timeout terminates the child process. A first-time kernel compilation may exceed that deadline; retry once before investigating drivers. Failure messages summarize recognized runtime errors without displaying raw backend output, target data or candidates. HashLynx does not bypass driver checks with `--force`, install drivers or change device settings.

Normal service tests inject process results and do not require Hashcat or a GPU. The optional integration test uses `HASHLYNX_TEST_HASHCAT` pointing at a disposable backend copy and `HASHLYNX_TEST_DEVICE` identifying the device explicitly selected for testing:

```powershell
dotnet test tests/HashLynx.Hashcat.Tests -c Release --filter FullyQualifiedName~HardwareCheckIntegrationTests
```
