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

`wzreplay.py` stands in for World: it replays a capture's WZ stream to a zone and records the zone's ZW frames, so a zone can be compared with the native one without a live World or client. It sends the frames that preceded the native ZoneLoaded, waits for the zone's own ZoneLoaded (ZW 0x001F), then sends the rest:

```
python -I tools/capture/wzreplay.py captures/native.bin 1260 captures/ours.bin --linger 60
AAEmu.ZoneServer --config zone.json +zone w_gweonid_forest_1 +instance 0 +world_port 1260
python -I tools/capture/wzdecode.py captures/ours.bin
```

The replay does not react to the zone, so World-assigned ids in later frames only line up while the zone issues the same requests in the same order as the native zone did.

Captures contain your World's live data. Keep them out of the repository.
