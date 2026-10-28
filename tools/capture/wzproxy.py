"""Logging TCP proxy: listen_port -> target. Writes binary records: dir(1) t_ms(8) len(4) data."""
import socket
import struct
import sys
import threading
import time

listen_port, target_port, out_path = int(sys.argv[1]), int(sys.argv[2]), sys.argv[3]
lock = threading.Lock()
out = open(out_path, 'ab')
t0 = time.time()


def pump(src, dst, direction):
    try:
        while True:
            data = src.recv(65536)
            if not data:
                break
            with lock:
                out.write(struct.pack('<BdI', direction, time.time() - t0, len(data)) + data)
                out.flush()
            dst.sendall(data)
    except OSError:
        pass
    finally:
        for s in (src, dst):
            try:
                s.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass


server = socket.socket()
server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
server.bind(('127.0.0.1', listen_port))
server.listen()
print(f'proxy 127.0.0.1:{listen_port} -> 127.0.0.1:{target_port}, log {out_path}', flush=True)
while True:
    zone, _ = server.accept()
    world = socket.create_connection(('127.0.0.1', target_port))
    print(f'{time.time() - t0:.1f}s connection', flush=True)
    # direction 0 = zone->World (ZW), 1 = World->zone (WZ)
    threading.Thread(target=pump, args=(zone, world, 0), daemon=True).start()
    threading.Thread(target=pump, args=(world, zone, 1), daemon=True).start()
