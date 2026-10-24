"""Test candidate packing orders against the shard sizes that exist on disk.

Usage: python pack_test.py <bigpatchlist.json> <patch_dir> [install_epoch]

The base tier is stored as Patch1..127.mpk on disk and the manifest announces the chunks it is made of.
This tool tries plausible packing rules and reports how closely each reproduces the real shard sizes.

Note: the on-disk tier is the result of the initial install plus every later update, so it is expected
to disagree with a fresh pack of the current manifest whenever a round rewrote individual shards. A rule
is only confirmed when it reproduces the shards that were NOT rewritten (install_epoch filters those).
"""

import json
import os
import re
import sys

CAPACITY = 524288000  # 500 MiB, the value the on-disk shards cluster around


def disk_shards(patch_dir):
    shards = []
    for entry in os.listdir(patch_dir):
        match = re.match(r"^Patch(\d+)\.mpk$", entry)
        if match and int(match.group(1)) <= 127:
            path = os.path.join(patch_dir, entry)
            shards.append((int(match.group(1)), os.path.getsize(path), os.path.getmtime(path)))
    shards.sort()
    return shards


def pack_greedy(order, chunks):
    """Keep adding chunks until the next one would push the shard past the capacity."""
    shards = []
    current = 0
    for name in order:
        size = chunks[name][0]
        if current > 0 and current + size > CAPACITY:
            shards.append(current)
            current = 0
        current += size
    if current:
        shards.append(current)
    return shards


def main():
    manifest_path, patch_dir = sys.argv[1], sys.argv[2]
    install_epoch = int(sys.argv[3]) if len(sys.argv) > 3 else 0

    with open(manifest_path, "rb") as handle:
        document = json.load(handle)

    chunks = dict(document["big_common"])
    chunks.update(document.get("big_windows64_common", {}))

    shards = disk_shards(patch_dir)
    untouched = [size for _, size, mtime in shards if mtime <= install_epoch]
    print("disk: {} shards, {:,} bytes".format(len(shards), sum(s for _, s, _ in shards)))
    print("      {} shards never rewritten since the initial install".format(len(untouched)))

    orders = {
        "sorted": sorted(chunks),
        "size-asc": sorted(chunks, key=lambda k: chunks[k][0]),
        "size-desc": sorted(chunks, key=lambda k: -chunks[k][0]),
        "hash-asc": sorted(chunks, key=lambda k: chunks[k][2]),
        "hash-desc": sorted(chunks, key=lambda k: -chunks[k][2]),
    }

    for label, order in orders.items():
        candidate = pack_greedy(order, chunks)
        matched = sum(1 for a, b in zip(candidate, untouched) if a == b)
        print("  {:<12} shards={:>4} matched-untouched={:>4}/{} first5={}".format(
            label, len(candidate), matched, len(untouched), candidate[:5]))


if __name__ == "__main__":
    main()
