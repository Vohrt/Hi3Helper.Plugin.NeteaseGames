"""Dump the bytes around a captured symbol name and try to expose the code object that follows it.

Usage: python inspect_region.py <dump.bin> <marker> [window]

Python 3.11+ stores strings inline in the object graph; a function's name is immediately followed by
its code object and constant tables. This tool prints a hex + ASCII window and every ASCII run found
inside it, which is usually enough to recover the surrounding identifiers and literals.
"""

import re
import sys


def main() -> None:
    path = sys.argv[1]
    marker = sys.argv[2].encode()
    window = int(sys.argv[3]) if len(sys.argv) > 3 else 512

    data = open(path, "rb").read()
    index = data.find(marker)
    if index < 0:
        print(f"{path}: marker {marker!r} not present")
        return

    print(f"{path} @ {index:#x}")
    segment = data[index:index + window]
    for offset in range(0, len(segment), 16):
        chunk = segment[offset:offset + 16]
        hex_part = " ".join(f"{b:02x}" for b in chunk)
        text_part = "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)
        print(f"  +{offset:04x}  {hex_part:<48} {text_part}")

    print("--- ascii runs ---")
    for match in re.finditer(rb"[ -~]{3,}", segment):
        print(f"  +{match.start():04x} {match.group().decode()!r}")


if __name__ == "__main__":
    main()
