"""Decode wzproxy captures: per-direction frame list (len u16 incl. opcode, opcode u16, body)."""
import struct
import sys
from collections import Counter, OrderedDict


def frames(path):
    buf = {0: b'', 1: b''}
    with open(path, 'rb') as f:
        data = f.read()
    i = 0
    while i + 13 <= len(data):
        d, t, n = struct.unpack_from('<BdI', data, i)
        i += 13
        buf[d] += data[i:i + n]
        i += n
        b = buf[d]
        while len(b) >= 2:
            ln = struct.unpack_from('<H', b)[0]
            if len(b) < ln + 2:
                break
            op = struct.unpack_from('<H', b, 2)[0]
            yield d, t, op, b[4:ln + 2]
            b = b[ln + 2:]
        buf[d] = b


def main():
    path = sys.argv[1]
    mode = sys.argv[2] if len(sys.argv) > 2 else 'summary'
    names = {0: 'ZW', 1: 'WZ'}
    if mode == 'summary':
        counts, first, sizes = Counter(), OrderedDict(), {}
        for d, t, op, body in frames(path):
            k = (names[d], op)
            counts[k] += 1
            first.setdefault(k, t)
            sizes.setdefault(k, set()).add(len(body))
        for k, t in first.items():
            s = sorted(sizes[k])
            print(f'{t:8.2f}s {k[0]} 0x{k[1]:04X} x{counts[k]:<6} sizes {s[:6]}{"..." if len(s) > 6 else ""}')
    elif mode == 'dump':
        want = int(sys.argv[3], 16)
        limit = int(sys.argv[4]) if len(sys.argv) > 4 else 3
        for d, t, op, body in frames(path):
            if op == want and (len(sys.argv) <= 5 or names[d] == sys.argv[5]):
                print(f'{t:8.2f}s {names[d]} 0x{op:04X} len={len(body)} {body[:96].hex(" ")}')
                limit -= 1
                if not limit:
                    break


main()
