"""Replay a capture's World->zone (WZ) stream to a zone server and record what it sends back.

Acts as World on a port: waits for the zone's ZWJoin, sends the captured WZ frames that preceded the
native ZoneLoaded (ZW 0x001F), waits for the zone's own ZoneLoaded, then sends the remaining WZ frames
(timing compressed by --speed, default instant). Records the zone's ZW frames in the wzproxy format.

  python -I tools/capture/wzreplay.py <capture.bin> <listen-port> <out.bin> [--speed N] [--linger S]
"""
import argparse
import socket
import struct
import time


def frames(path):
    buf = {0: b'', 1: b''}
    data = open(path, 'rb').read()
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
            yield d, t, struct.unpack_from('<H', b, 2)[0], b[:ln + 2]
            b = b[ln + 2:]
        buf[d] = b


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('capture')
    ap.add_argument('port', type=int)
    ap.add_argument('out')
    ap.add_argument('--speed', type=float, default=0, help='0 = send as fast as possible')
    ap.add_argument('--linger', type=float, default=30, help='seconds to keep recording after the last frame')
    a = ap.parse_args()

    all_frames = list(frames(a.capture))
    loaded_at = next(t for d, t, op, _ in all_frames if d == 0 and op == 0x001F)
    wz = [(t, op, raw) for d, t, op, raw in all_frames if d == 1]
    before = [f for f in wz if f[0] <= loaded_at]
    after = [f for f in wz if f[0] > loaded_at]
    print(f'{len(before)} WZ frames before ZoneLoaded, {len(after)} after', flush=True)

    out = open(a.out, 'wb')
    t0 = time.time()
    rx = b''

    def record(data):
        out.write(struct.pack('<BdI', 0, time.time() - t0, len(data)) + data)
        out.flush()

    def pump(sock, until_opcode=None, timeout=0.0):
        """Read ZW frames; return True once until_opcode is seen."""
        nonlocal rx
        sock.settimeout(timeout if timeout else 0.001)
        deadline = time.time() + timeout
        while True:
            try:
                data = sock.recv(65536)
                if not data:
                    raise ConnectionError('zone closed the connection')
                record(data)
                rx += data
            except socket.timeout:
                pass
            seen = False
            while len(rx) >= 4 and len(rx) >= struct.unpack_from('<H', rx)[0] + 2:
                ln, op = struct.unpack_from('<HH', rx)
                rx = rx[ln + 2:]
                if op == until_opcode:
                    seen = True
            if seen or time.time() >= deadline:
                return seen

    def send(sock, batch):
        start, base = time.time(), batch[0][0] if batch else 0
        for t, op, raw in batch:
            if a.speed:
                delay = (t - base) / a.speed - (time.time() - start)
                if delay > 0:
                    time.sleep(delay)
            sock.sendall(raw)
            pump(sock)

    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(('127.0.0.1', a.port))
    server.listen(1)
    print(f'replay World listening on 127.0.0.1:{a.port}', flush=True)
    zone, _ = server.accept()
    print('zone connected', flush=True)
    if not pump(zone, 0x0000, 60):
        raise SystemExit('no ZWJoin within 60 s')
    print('ZWJoin received; sending bring-online frames', flush=True)
    send(zone, before)
    if not pump(zone, 0x001F, 900):
        raise SystemExit('zone did not report ZoneLoaded within 900 s')
    print(f'ZoneLoaded after {time.time() - t0:.1f}s; replaying {len(after)} frames', flush=True)
    send(zone, after)
    pump(zone, None, a.linger)
    # Signal World EOF while continuing to read the zone's pending notifications. Closing a
    # socket with unread debug/movement reports sends RST and hides orderly retirement failures.
    zone.shutdown(socket.SHUT_WR)
    zone.settimeout(60)
    while data := zone.recv(65536):
        record(data)
    zone.close()
    server.close()
    out.close()
    print(f'done after {time.time() - t0:.1f}s (zone retired)', flush=True)


main()
