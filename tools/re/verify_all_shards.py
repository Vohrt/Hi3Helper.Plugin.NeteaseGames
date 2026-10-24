import sqlite3, shutil, os, struct, collections, json

src = r'D:\ywzh\ywzh_extreme\LocalData\Patch\Patch.mpkinfo'
dst = os.path.join(os.environ['TEMP'], 'pm_verify.db')
shutil.copy2(src, dst)
c = sqlite3.connect(dst).cursor()

g = collections.defaultdict(list)
for val, in c.execute('SELECT value FROM info0'):
    mk = struct.unpack_from('<H', val, 8)[0]
    cum = struct.unpack_from('<I', val, 20)[0]
    g[mk].append(cum)

d = r'D:\ywzh\ywzh_extreme\LocalData\Patch'
import re
diffs = []
for e in os.listdir(d):
    m = re.match(r'^Patch(\d+)\.mpk$', e)
    if not m:
        continue
    n = int(m.group(1))
    if n > 127:
        continue
    sz = os.path.getsize(os.path.join(d, e))
    if n in g:
        diffs.append((n, max(g[n]), sz, sz - max(g[n])))

print(f'shards compared: {len(diffs)}')
for row in diffs:
    print(f'  Patch{row[0]:<4} index={row[1]:>13,} disk={row[2]:>13,} pad={row[3]:>12,}')
