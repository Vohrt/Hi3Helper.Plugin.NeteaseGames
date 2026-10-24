"""Analyse how the base shard tier ("big_common") maps onto the on-disk PatchN.mpk shards.

Usage: python analyze_biglist.py <bigpatchlist.json> <patch_dir>

The manifest groups chunks by a textual prefix and a numeric suffix (CGs_0, Char_736, r_8921, ...).
This tool prints those groups, the announced totals, and how they line up with the shard files that
exist on disk, which is what the merge rule has to explain.
"""

import collections
import json
import os
import re
import sys


def main() -> None:
    manifest_path = sys.argv[1]
    patch_dir = sys.argv[2] if len(sys.argv) > 2 else None

    with open(manifest_path, "rb") as handle:
        document = json.load(handle)

    big_common = document["big_common"]
    big_windows = document.get("big_windows64_common", {})

    groups = collections.defaultdict(list)
    for name in big_common:
        match = re.match(r"^(.*?)_(\d+)$", name)
        if match:
            groups[match.group(1)].append(int(match.group(2)))
        else:
            groups[name].append(-1)

    print("big_common prefix groups")
    print(f"  {'prefix':<28} {'count':>7} {'index range':>18} {'total size':>16}")
    for prefix, indices in sorted(groups.items()):
        numeric = sorted(i for i in indices if i >= 0)
        total = sum(big_common[f"{prefix}_{i}"][0] for i in numeric) if numeric else 0
        span = f"{numeric[0]}..{numeric[-1]}" if numeric else "-"
        print(f"  {prefix:<28} {len(numeric):>7} {span:>18} {total:>16,}")

    print()
    print("big_windows64_common")
    for name, value in sorted(big_windows.items()):
        print(f"  {name:<28} {value[0]:>16,}")

    announced = sum(v[0] for v in big_common.values())
    print()
    print(f"big_common announced total: {announced:,} bytes ({announced / 1024 ** 3:.2f} GB)")

    if not patch_dir:
        return

    print()
    print("shard files on disk (base tier only)")
    shards = []
    for entry in os.listdir(patch_dir):
        match = re.match(r"^Patch(\d+)\.mpk$", entry)
        if match and int(match.group(1)) <= 127:
            path = os.path.join(patch_dir, entry)
            shards.append((int(match.group(1)), os.path.getsize(path)))
    shards.sort()

    disk_total = sum(size for _, size in shards)
    print(f"  {len(shards)} shards, total {disk_total:,} bytes ({disk_total / 1024 ** 3:.2f} GB)")
    print()
    print("per shard size vs the announced chunk size distribution")
    print(f"  {'shard':<12} {'size':>14} {'size % 524288':>14}")
    for number, size in shards[:10]:
        print(f"  Patch{number:<7} {size:>14,} {size % 524288:>14}")
    print("  ...")


if __name__ == "__main__":
    main()
