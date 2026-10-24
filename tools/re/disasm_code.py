"""Disassemble a Python 3.11+ code object that was found in a live process memory dump.

Usage: python disasm_code.py <dump.bin> <co_code_start_offset> [length]

The dump is a raw memory region; only the bytes are used, so the offsets are the ones printed by the
memory scanner. Python 3.11+ uses a 2-byte-per-instruction wordcode layout (opcode, oparg) with
inline caches, so the listing below is a best-effort raw view, not a full decompilation.
"""

import sys

# Opcodes that matter for reasoning about these small helpers (CPython 3.11/3.12).
OPCODES = {
    0x01: "POP_TOP",
    0x02: "PUSH_NULL",
    0x03: "NOP",
    0x04: "NOP",
    0x05: "NOP",
    0x06: "NOP",
    0x09: "NOP",
    0x0B: "NOP",
    0x19: "BINARY_SUBSCR",
    0x1A: "BINARY_SLICE",
    0x1B: "STORE_SLICE",
    0x20: "NOP",
    0x21: "NOP",
    0x22: "NOP",
    0x23: "NOP",
    0x25: "NOP",
    0x26: "NOP",
    0x28: "NOP",
    0x2A: "NOP",
    0x2B: "NOP",
    0x2D: "NOP",
    0x2E: "NOP",
    0x32: "NOP",
    0x33: "NOP",
    0x35: "NOP",
    0x3B: "NOP",
    0x46: "NOP",
    0x4A: "NOP",
    0x4D: "NOP",
    0x4E: "NOP",
    0x4F: "NOP",
    0x50: "NOP",
    0x51: "NOP",
    0x52: "NOP",
    0x53: "NOP",
    0x54: "NOP",
    0x55: "NOP",
    0x56: "NOP",
    0x57: "NOP",
    0x58: "NOP",
    0x59: "NOP",
    0x5A: "NOP",
    0x60: "NOP",
    0x64: "LOAD_CONST",
    0x67: "BUILD_TUPLE",
    0x69: "BUILD_STRING",
    0x6A: "BUILD_LIST",
    0x6B: "LIST_TO_TUPLE",
    0x6C: "LIST_EXTEND",
    0x6D: "SET_UPDATE",
    0x6E: "DICT_UPDATE",
    0x6F: "DICT_MERGE",
    0x70: "LOAD_FAST_CHECK",
    0x71: "NOP",
    0x7C: "LOAD_FAST",
    0x7D: "STORE_FAST",
    0x82: "NOP",
    0x83: "NOP",
    0x85: "NOP",
    0x86: "NOP",
    0x95: "RETURN_VALUE",
    0x96: "NOP",
    0x97: "NOP",
    0x98: "NOP",
    0x99: "NOP",
    0x9A: "NOP",
    0x9B: "NOP",
    0x9C: "NOP",
    0x9D: "NOP",
    0x9E: "NOP",
    0xA6: "NOP",
    0xA7: "BINARY_OP",
    0xAC: "CALL_INTRINSIC_1",
    0xB2: "CALL",
    0xB3: "CALL_KW",
    0xB4: "NOP",
    0xB5: "NOP",
    0xB6: "NOP",
    0xB7: "NOP",
    0xB8: "NOP",
    0xB9: "NOP",
    0xBA: "NOP",
    0xBB: "NOP",
    0xBC: "NOP",
    0xBD: "NOP",
    0xBE: "NOP",
    0xBF: "KW_NAMES",
    0xC0: "NOP",
    0xC1: "NOP",
    0xC2: "NOP",
    0xC3: "NOP",
    0xC4: "NOP",
    0xC5: "NOP",
    0xC6: "NOP",
    0xC7: "NOP",
    0xC8: "NOP",
    0xC9: "NOP",
    0xCA: "NOP",
    0xCB: "NOP",
    0xCC: "NOP",
    0xCD: "NOP",
    0xCE: "NOP",
    0xCF: "NOP",
    0xD0: "NOP",
    0xD1: "NOP",
    0xD2: "NOP",
    0xD3: "NOP",
    0xD4: "NOP",
    0xDA: "NOP",
    0xDB: "NOP",
    0xDC: "NOP",
    0xDD: "NOP",
    0xDE: "NOP",
    0xDF: "NOP",
    0xE0: "NOP",
    0xE1: "NOP",
    0xE2: "NOP",
    0xE3: "NOP",
    0xE4: "NOP",
    0xE5: "NOP",
    0xE6: "NOP",
    0xE7: "NOP",
    0xE8: "NOP",
    0xE9: "NOP",
    0xEA: "NOP",
    0xEB: "NOP",
    0xEC: "NOP",
    0xED: "NOP",
    0xEE: "NOP",
    0xEF: "NOP",
    0xF0: "NOP",
    0xF1: "NOP",
    0xF2: "NOP",
    0xF3: "NOP",
    0xF4: "NOP",
    0xF5: "NOP",
    0xF6: "NOP",
    0xF7: "NOP",
    0xF8: "NOP",
    0xF9: "NOP",
    0xFA: "NOP",
    0xFB: "NOP",
    0xFC: "NOP",
    0xFD: "RESUME",
}

BINARY_OPS = {
    0: "+", 1: "&", 2: "//", 3: "<<", 4: "@", 5: "*",
    6: "%", 7: "|", 8: "**", 9: ">>", 10: "-", 11: "/",
    12: "^", 13: "+=", 14: "&=", 15: "//=", 16: "<<=", 17: "@=",
    18: "*=", 19: "%=", 20: "|=", 21: "**=", 22: ">>=", 23: "-=",
    24: "/=", 25: "^=",
}


def main() -> None:
    path = sys.argv[1]
    start = int(sys.argv[2], 0)
    length = int(sys.argv[3], 0) if len(sys.argv) > 3 else 64

    data = open(path, "rb").read()
    code = data[start:start + length]

    print(f"raw   : {code.hex()}")
    offset = 0
    while offset + 1 < len(code):
        op = code[offset]
        arg = code[offset + 1]
        name = OPCODES.get(op, f"0x{op:02x}")
        extra = ""
        if op == 0xA7:
            extra = f"   -> {BINARY_OPS.get(arg, '?')}"
        print(f"  {offset:03d}  {name:<14} {arg}{extra}")
        offset += 2


if __name__ == "__main__":
    main()
