# Recovery fails before progress starts

HashLynx checks the target, attack configuration, and executable before starting. These checks do not run a compute kernel. Hashcat may identify hashes and list devices successfully, then reject their driver/runtime when an attack starts.

Read the selected job's diagnostic on **Jobs**, then open **Hardware** and refresh device information:

- `CL_INVALID_VALUE` during `clGetDeviceInfo` means the OpenCL device-information query failed. Check the vendor driver/runtime for that hardware.
- An "outdated or broken" runtime message means Hashcat rejected that runtime. Changing the hash mode, wordlist, or rules cannot repair it.
- A missing platform/runtime message means no compatible compute runtime was found.
- "No usable compute device" is often the final consequence of an earlier, more specific error. Review both messages.

Use a compatible vendor runtime or another supported device. HashLynx does not install drivers or suppress Hashcat's runtime checks. Current requirements are listed on the [Hashcat site](https://hashcat.net/hashcat/).

## Selecting a CPU explicitly

After installing a suitable CPU runtime, restart HashLynx, refresh **Hardware**, and choose **Use this device by default** on the newly reported CPU. This preference is saved for future launches and applies to Basic mode. The Attack page shows the selected device beside Start recovery. You do not need to load an attack profile or enable Expert mode.

To override the saved choice for an individual attack, enter device IDs in **Attack → Expert mode → Run options → Advanced**. Blank Expert IDs use the saved Hardware default. Switching back to Basic mode restores that default. To return to Hashcat's automatic selection, choose **Use automatic selection** in Hardware. Refresh and reselect after runtime or hardware changes because IDs can change.

Hashcat has separate device-ID and OpenCL device-type filters. HashLynx allows all OpenCL types when selected IDs are supplied, then limits execution to exactly those IDs. This prevents a selected CPU from being excluded merely because a GPU is also present. With neither a saved default nor Expert IDs, Hashcat's automatic selection remains in effect.

## Intel HD Graphics 4600 / i5-4460 investigation

On the development machine, the HD Graphics 4600 driver `20.19.15.4624` returned `CL_INVALID_VALUE`. A separate CPU-only test rejected Intel runtime `5.2.0.10094`. Both tests used a generated, known-password fixture through a disposable Hashcat release and exited before recovery; no user target was needed to reproduce the failure.

Installing Intel's standalone CPU runtime 2026.0 resolved CPU recovery on this machine. Known-answer MD5 and NTLM recoveries passed afterward; the NTLM check exercised HashLynx's Start, Jobs, and Results screens with the starter wordlist and Normal preset. The new CPU runtime currently appears as device 3; the old runtime also enumerates the same CPU, so select the entry with the new driver version. The legacy GPU remains unusable here.

Intel's [CPU runtime guide](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-cpu-runtime-for-opencl-applications-guide.html) supports Core processors with SSE4.2 or newer on Windows 10/11; the [i5-4460 specifications](https://www.intel.com/content/www/us/en/products/sku/80817/intel-core-i54460-processor-6m-cache-up-to-3-40-ghz/specifications.html) list SSE4.2 and AVX2. Intel provides the administrator-installed Windows package on its [official CPU runtime download page](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-cpu-runtime-for-opencl-applications-with-sycl-support.html). Other machines still need their own compatibility and recovery checks.

Older advice recommending Intel runtime 18.1 does not apply to Hashcat 7.1.2: its [runtime version check](https://github.com/hashcat/hashcat/blob/v7.1.2/src/backend.c#L7580-L7597) rejects Intel CPU versions below 2020. Updating this CPU runtime is distinct from installing a graphics driver for the legacy GPU.
