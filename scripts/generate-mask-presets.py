"""Reproduce the immutable common-1000-v1 asset from the pinned aggregate source.

No network, raw password corpus or external dependencies are needed. Run with
`py -3 scripts/generate-mask-presets.py`. Keep existing IDs unchanged when adding
a later recipe. See docs/built-in-masks.md for selection and corpus limitations.
"""

from hashlib import sha256
from pathlib import Path
import re

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "scripts/mask-sources/hashcat-v7.1.2-rockyou-2-1800.hcmask"
SOURCE_SHA256 = "622b4ec063f59c24d0fea27fc553ebb7484882cb0ef676f4edddacc616e30494"
LICENSE = ROOT / "scripts/mask-sources/HASHCAT-LICENSE.txt"
LICENSE_SHA256 = "696fea993e355eb08efe8f0625326eee9f98353cf00064778caf3c01821ec255"
DESTINATION = ROOT / "src/HashLynx.Hashcat/MaskPresets/common-1000-v1.hcmask"


def checked_bytes(path: Path, expected: str) -> bytes:
    data = path.read_bytes()
    if sha256(data).hexdigest() != expected:
        raise ValueError(f"Pinned input changed: {path.name}")
    return data


def main() -> None:
    source = checked_bytes(SOURCE, SOURCE_SHA256).decode("ascii")
    license_text = checked_bytes(LICENSE, LICENSE_SHA256).decode("ascii")
    # Stable first-occurrence selection: retain the source's relative priority.
    # The aggregate supplies no counts, so do not fabricate frequency rankings.
    masks = list(dict.fromkeys(source.splitlines()))[:1000]
    if len(masks) != 1000 or any(not re.fullmatch(r"(?:\?[luds]){1,32}", mask) for mask in masks):
        raise ValueError("The pinned source does not supply 1,000 supported masks.")
    header = [
        "HashLynx Common patterns (1,000), immutable recipe common-1000-v1",
        "Source: hashcat/hashcat v7.1.2, commit c75f446c44cd3f0742035a1394416c39bee5ea8f",
        "Source path: masks/rockyou-2-1800.hcmask",
        f"Source SHA-256: {SOURCE_SHA256}",
        "Selection: first 1,000 distinct structures in upstream order.",
        "Historical corpus-derived priorities, not a global frequency ranking.",
        "See docs/built-in-masks.md. Adapted aggregate data retains its license:",
        "",
        *license_text.rstrip().splitlines(),
        "",
    ]
    data = ("\n".join("# " + line if line else "#" for line in header) + "\n" + "\n".join(masks) + "\n").encode("ascii")
    DESTINATION.parent.mkdir(parents=True, exist_ok=True)
    DESTINATION.write_bytes(data)
    print(f"{DESTINATION.name}: {len(masks)} masks, SHA-256 {sha256(data).hexdigest()}")


if __name__ == "__main__":
    main()
