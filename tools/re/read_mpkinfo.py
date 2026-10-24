"""Read the SQLite package index that the launcher keeps for the base shard tier.

Usage: python read_mpkinfo.py <Patch.mpkinfo> [query]

`LocalData\\Patch\\Patch.mpkinfo` is a real SQLite database (`SQLite format 3`), unlike the binary
`*.mpkinfo` files of the delta tier. It indexes every resource of the base tier, which is exactly what
the merge rule for PatchN.mpk has to explain: which resources live in which shard, and at what offset.
"""

import os
import shutil
import sqlite3
import sys
import tempfile


def main() -> None:
    source = sys.argv[1]
    query = sys.argv[2] if len(sys.argv) > 2 else None

    target = os.path.join(tempfile.gettempdir(), "Patch_mpkinfo_copy.db")
    shutil.copy2(source, target)

    connection = sqlite3.connect(target)
    cursor = connection.cursor()

    print("--- schema ---")
    for name, kind in cursor.execute("SELECT name, type FROM sqlite_master ORDER BY type, name"):
        print(f"  {kind:<8} {name}")

    tables = [r[0] for r in cursor.execute(
        "SELECT name FROM sqlite_master WHERE type='table'")]
    for table in tables:
        try:
            count = cursor.execute(f"SELECT COUNT(*) FROM [{table}]").fetchone()[0]
        except sqlite3.Error as error:
            print(f"  {table}: {error}")
            continue
        print(f"  {table}: {count:,} rows")

    if query:
        print("--- query ---")
        for row in cursor.execute(query):
            print(" ", row)

    connection.close()


if __name__ == "__main__":
    main()
