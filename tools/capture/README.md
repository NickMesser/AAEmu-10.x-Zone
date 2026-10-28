# WZ/ZW capture tools

Record the traffic between a zone server and World, then summarize or dump it. Python 3, no packages.

`wzproxy.py` listens on one port and forwards to World. Start the zone with `+world_port` set to the proxy port:

```
python -I tools/capture/wzproxy.py 1250 1240 captures/native.bin
AAEmu.ZoneHost.exe -dedicated +world_ip 127.0.0.1 +world_port 1250 +world_serverport 1250 +zone w_gweonid_forest_1 ...
```

Each record is `direction u8` (0 = zone to World, ZW; 1 = World to zone, WZ), `seconds f64`, `length u32`, then the raw TCP bytes.

`wzdecode.py` reassembles frames (`length u16` including the opcode, `opcode u16`, body):

```
python -I tools/capture/wzdecode.py captures/native.bin            # first time, count and body sizes per opcode
python -I tools/capture/wzdecode.py captures/native.bin dump 0073 5 WZ   # first 5 WZ 0x0073 bodies (hex)
```

Captures contain your World's live data. Keep them out of the repository.
