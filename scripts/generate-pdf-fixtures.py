"""Test-only fixture writer. Requires pikepdf; never used by the application.

Usage: python scripts/generate-pdf-fixtures.py OUTPUT_DIRECTORY
Creates original one-page PDFs containing no user data. The known open password
is HashLynx-pdf-test; the distinct owner password tests open-password extraction.
"""
import pathlib
import sys
import pikepdf

out = pathlib.Path(sys.argv[1])
out.mkdir(parents=True, exist_ok=True)
for name, revision, aes, metadata, streams, linearize in [
    ("r2", 2, False, False, False, False),
    ("r3", 3, False, False, False, False),
    ("r4-rc4", 4, False, False, False, False),
    ("r4-aes", 4, True, False, True, False),
    ("r5", 5, True, True, True, False),
    ("r6", 6, True, True, True, False),
    ("r6-linearized", 6, True, True, False, True),
]:
    with pikepdf.Pdf.new() as pdf:
        pdf.add_blank_page(page_size=(72, 72))
        pdf.save(out / (name + ".pdf"),
                 encryption=pikepdf.Encryption(user="HashLynx-pdf-test",
                    owner="Different-owner-test", R=revision, aes=aes, metadata=metadata),
                 object_stream_mode=pikepdf.ObjectStreamMode.generate if streams else pikepdf.ObjectStreamMode.disable,
                 linearize=linearize)
    print("Created " + name)
