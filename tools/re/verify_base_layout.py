"""Verify the base tier layout recorded in Patch.mpkinfo against the shard files on disk.

Usage: python verify_base_layout.py <Patch.mpkinfo> <patch_dir>

`Patch.mpkinfo` is a SQLite database (`info0(filepath, value)`). The `value` blob carries, among other
fields, the shard id at offset 8 (u16) and the cumulative offset inside that shard at offset 20 (u32).
Grouping by shard id reproduces the base tier: every group starts at 0 and ends just below 500 MiB, and
the largest cumulative offset of a group is a lower bound for the size of the matching PatchN.mpk file.
"""

import collections
import os
import re
import shutil
import sqlite3
import struct
import sys
import tempfile

SHARD_ID_OFFSET = 8
CUMULATIVE_OFFSET = 20


def main() -> None:
    source, patch_dir = sys.argv[1], sys.argv[2]

    copy = os.path.join(tempfile.gettempdir(), "verify_base_layout.db")
    shutil.copy2(source, copy)
    cursor = sqlite3.connect(copy).cursor()

    ends = collections.defaultdict(int)
    counts = collections.defaultdict(int)
    rows = 0
    for (value,) in cursor.execute("SELECT value FROM info0"):
        rows += 1
        shard = struct.unpack_from("<H", value, SHARD_ID_OFFSET)[0]
        cumulative = struct.unpack_from("<I", value, CUMULATIVE_OFFSET)[0]
        counts[shard] += 1
        if cumulative > ends[shard]:
            ends[shard] = cumulative

    print(f"index rows: {rows:,}   shards in index: {len(ends)}")

    disk = {}
    for entry in os.listdir(patch_dir):
        match = re.match(r"^Patch(\d+)\.mpk$", entry)
        if match:
            disk[int(match.group(1))] = os.path.getsize(os.path.join(patch_dir, entry))

    matched = 0
    padding = []
    print(f"{'shard':<12}{'entries':>9}{'index end':>15}{'disk size':>15}{'padding':>12}")
    for shard in sorted(ends):
        if shard not in disk:
            continue
        matched += 1
        pad = disk[shard] - ends[shard]
        padding.append(pad)
        if matched <= 8 or shard >= max(ends) - 2:
            print(f"Patch{shard:<7}{counts[shard]:>9}{ends[shard]:>15,}{disk[shard]:>15,}{pad:>12,}")

    print()
    print(f"shards compared: {matched}")
    if padding:
        print(f"padding: min={min(padding):,} max={max(padding):,} mean={sum(padding) / len(padding):,.0f}")
        print(f"all non-negative (index never claims more than the file): {all(p >= 0 for p in padding)}")
    print(f"indexed shard ids: {min(ends)}..{max(ends)}")


if __name__ == "__main__":
    main()
