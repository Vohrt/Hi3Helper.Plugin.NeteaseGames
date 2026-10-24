import base64, zlib, json, sys, struct

KEY = b'netease_joker_h74_encrypt_key'

def dehex(data: bytes) -> bytes:
    if data[:4] != b'HEX_':
        return data
    raw = base64.b64decode(data[4:])
    x = bytes(c ^ KEY[i % len(KEY)] for i, c in enumerate(raw))
    return zlib.decompress(x)

def parse_patchmd5(text: str):
    i = text.index('{')
    header = [l.strip() for l in text[:i].split('\n') if l.strip()]
    body = json.loads(text[i:])
    meta = body.get('@metadata@', {})
    return {
        'content_version': header[0] if len(header) > 0 else None,
        'patchlist_hash': header[1] if len(header) > 1 else None,
        'patch_version': header[3] if len(header) > 3 else None,
        'normal': body.get('normal', {}),
        'metadata': meta,
    }

if __name__ == '__main__':
    for p in sys.argv[1:]:
        data = open(p, 'rb').read()
        plain = dehex(data)
        try:
            txt = plain.decode('utf-8')
        except UnicodeDecodeError:
            print(p, 'binary', len(plain))
            continue
        if txt.lstrip().startswith('{') or ('\n{' in txt and txt[:40].strip().isdigit()):
            print(json.dumps(parse_patchmd5(txt), indent=2, ensure_ascii=False)[:4000])
        else:
            print(p, 'text', len(txt), txt[:200])
