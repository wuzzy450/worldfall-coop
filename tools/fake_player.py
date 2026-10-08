#!/usr/bin/env python3
"""
fake_player.py - a scripted second player for testing Coopfall without a second PC.

It connects to the WorldfallRooms relay, joins a world, and (when it has the world)
walks a "possessed" avatar in a circle, waves its god cursor around, uses a god power
now and then and chats. It also checks that world snapshots it receives are real
WorldBox saves (zlib-compressed JSON).

Examples
  python fake_player.py --room home-alice                     # visit Alice's own world
  python fake_player.py --room shared --host-with world.wbox  # host the shared world from a file
  python fake_player.py --room shared --save-snapshot got.wbox --seconds 60
"""

import argparse
import base64
import hashlib
import json
import math
import socket
import time
import zlib

CHUNK = 60 * 1024


class Bot:
    def __init__(self, args):
        self.a = args
        self.sock = socket.create_connection((args.server, args.port), timeout=10)
        self.sock.settimeout(0.05)
        self.buf = b""
        self.room = None
        self.host = False
        self.have_world = False
        self.snapshot = None
        self.dl = None
        self.center = (args.x, args.y)
        self.stats = {}

    def pick_center(self, data):
        """Circle around the first unit in the world (or the map center)."""
        if self.a.x >= 0:
            return
        try:
            js = json.loads(zlib.decompress(data))
            actors = js.get("actors_data") or []
            if actors:
                self.center = (actors[0].get("x", 50), actors[0].get("y", 50))
            else:
                self.center = (js.get("width", 2) * 32, js.get("height", 2) * 32)
        except Exception:  # noqa: BLE001
            self.center = (50, 50)
        print(f"avatar will walk around {self.center}")

    def send(self, obj):
        self.sock.sendall((json.dumps(obj) + "\n").encode())

    def poll(self):
        out = []
        try:
            while True:
                data = self.sock.recv(1 << 20)
                if not data:
                    raise ConnectionError("server closed")
                self.buf += data
        except (socket.timeout, BlockingIOError):
            pass
        while b"\n" in self.buf:
            line, _, self.buf = self.buf.partition(b"\n")
            if line.strip():
                out.append(json.loads(line))
        return out

    def upload(self, data):
        parts = [base64.b64encode(data[i:i + CHUNK]).decode() for i in range(0, len(data), CHUNK)]
        self.send({"t": "snap-begin", "room": self.room, "size": len(data),
                   "sha": hashlib.sha256(data).hexdigest(), "total": len(parts)})
        for i, p in enumerate(parts):
            self.send({"t": "snap-chunk", "room": self.room, "seq": i, "data": p})
        self.send({"t": "snap-end", "room": self.room})

    def handle(self, m):
        t = m.get("t")
        self.stats[t] = self.stats.get(t, 0) + 1
        if t == "welcome":
            print(f"welcome: I am {m['name']}; worlds: {[r['id'] for r in (m.get('rooms') or [])]}")
            join = {"t": "join", "room": self.a.room, "name": self.a.world_name, "seed": bool(self.a.host_with),
                    "preferLocal": bool(self.a.host_with)}
            self.send(join)
        elif t == "joined":
            self.room, self.host = m["room"], m["role"] == "host"
            print(f"joined {self.room} as {m['role']} (load={m['load']})")
            if not m["load"]:
                self.have_world = True
                if self.a.host_with:
                    self.pick_center(open(self.a.host_with, "rb").read())
        elif t == "snap-request":
            src = self.snapshot or (open(self.a.host_with, "rb").read() if self.a.host_with else None)
            if src:
                print(f"host duty: uploading {len(src) // 1024} KB snapshot ({m.get('reason')})")
                self.upload(src)
        elif t == "snap-begin":
            self.dl = {"total": m["total"], "sha": m.get("sha"), "parts": []}
        elif t == "snap-chunk" and self.dl is not None:
            self.dl["parts"].append(base64.b64decode(m["data"]))
        elif t == "snap-end" and self.dl is not None:
            data = b"".join(self.dl["parts"])
            ok_sha = hashlib.sha256(data).hexdigest() == self.dl["sha"]
            try:
                js = json.loads(zlib.decompress(data))
                info = f"valid WorldBox save: v{js.get('saveVersion')} {js.get('width')}x{js.get('height')} zones, " \
                       f"{len(js.get('actors_data') or [])} units, {len(js.get('cities') or [])} cities"
            except Exception as e:  # noqa: BLE001
                info = f"NOT a WorldBox save ({e})"
            print(f"received world: {len(data) // 1024} KB, sha ok={ok_sha}; {info}")
            self.pick_center(data)
            self.snapshot = data
            if self.a.save_snapshot:
                open(self.a.save_snapshot, "wb").write(data)
                print(f"saved snapshot to {self.a.save_snapshot}")
            self.dl = None
            self.have_world = True
        elif t in ("chat", "notice", "error", "role", "power", "speed", "act"):
            print(f"<- {t}: " + json.dumps({k: v for k, v in m.items() if k not in ('t', 'room', 'color')})[:160])
        elif t == "avatar" and self.stats[t] % 30 == 1:
            print(f"<- avatar from {m.get('name')}: on={m.get('on')} at ({m.get('x')},{m.get('y')}) unit #{m.get('aid')}")
        elif t == "cursor" and self.stats[t] % 50 == 1:
            print(f"<- cursor from {m.get('name')}: ({m.get('x')},{m.get('y')}) power={m.get('p')}")

    def run(self):
        self.send({"t": "hello", "name": self.a.name, "version": 3, "color": self.a.color, "game": "bot"})
        start = time.time()
        next_av = next_cur = 0.0
        next_power = start + 6
        next_chat = start + 3
        said = 0
        while time.time() - start < self.a.seconds:
            for m in self.poll():
                self.handle(m)
            now = time.time()
            if self.have_world:
                ang = (now - start) * 0.6
                cx, cy = self.center
                if self.a.avatar and now >= next_av:
                    next_av = now + 1 / 15
                    self.send({"t": "avatar", "on": True, "aid": "0", "asset": self.a.asset,
                               "x": round(cx + 6 * math.cos(ang), 3), "y": round(cy + 6 * math.sin(ang), 3),
                               "flip": math.sin(ang) < 0, "hp": 75, "mhp": 100})
                if not self.a.avatar and now >= next_cur:
                    next_cur = now + 0.1
                    self.send({"t": "cursor", "x": round(cx + 10 * math.cos(ang), 2),
                               "y": round(cy + 10 * math.sin(ang), 2), "p": "lightning"})
                if self.a.powers and now >= next_power:
                    next_power = now + 8
                    self.send({"t": "power", "p": "lightning", "x": int(cx + 12), "y": int(cy), "brush": "circ_3"})
                    print("-> used lightning")
                if now >= next_chat and said < 3:
                    next_chat = now + 10
                    said += 1
                    self.send({"t": "chat", "text": ["hello from the fake player!", "nice world :)", "watch this lightning"][said - 1]})
            time.sleep(0.01)
        if self.a.avatar:
            self.send({"t": "avatar", "on": False})
        self.send({"t": "bye"})
        print("stats:", json.dumps(self.stats))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--server", default="127.0.0.1")
    ap.add_argument("--port", type=int, default=25598)
    ap.add_argument("--name", default="FakeFriend")
    ap.add_argument("--color", default="#ff6fd0")
    ap.add_argument("--room", default="shared")
    ap.add_argument("--world-name", default="Fake Friend's World")
    ap.add_argument("--host-with", help="a .wbox snapshot file: host/seed the room with it")
    ap.add_argument("--save-snapshot", help="write the received world snapshot to this file")
    ap.add_argument("--seconds", type=float, default=45)
    ap.add_argument("--asset", default="human")
    ap.add_argument("--x", type=float, default=-1, help="avatar circle center (default: first unit in the world)")
    ap.add_argument("--y", type=float, default=-1)
    ap.add_argument("--no-avatar", dest="avatar", action="store_false", help="show a god cursor instead of a unit")
    ap.add_argument("--no-powers", dest="powers", action="store_false")
    Bot(ap.parse_args()).run()


if __name__ == "__main__":
    main()
