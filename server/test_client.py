#!/usr/bin/env python3
"""
test_client.py - end-to-end test for the WorldfallRooms v2 relay (Cuberite plugin).

Plays several fake Coopfall clients against 127.0.0.1:25598 and checks the real flows:

  * hello / welcome / players / rooms, version mismatch, unique names
  * "own world" mode: alice hosts home-alice from her open world (seed + preferLocal),
    answers the server's snapshot request, publishes a preview
  * bob travels to alice's world: server asks the host for a FRESH snapshot and streams it
    to bob; reassembled bytes match
  * relays inside a room: avatar, cursor, power, speed, act; chat is server-wide
  * live world sync: wu/wb/wdata relayed raw from the host only, wneed from guests
  * resync request, host migration when the host leaves, dormant worlds served from the
    stored snapshot, shared world, rename/delete permissions

Usage:  python test_client.py [port]         (standard library only)
"""

import base64
import hashlib
import json
import secrets
import socket
import sys
import time

HOST = "127.0.0.1"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 25598
CHUNK = 60 * 1024
RESULTS = []
RUN = secrets.token_hex(3)  # unique suffix so repeated runs don't collide with stored rooms


def check(name, ok, detail=""):
    RESULTS.append((name, bool(ok)))
    print(f"[{'PASS' if ok else 'FAIL'}] {name}" + (f"  ({detail})" if detail and not ok else ""))
    return ok


class Client:
    def __init__(self, name):
        self.name = name
        self.sock = socket.create_connection((HOST, PORT), timeout=10)
        self.buf = b""
        self.id = None
        self.inbox = []

    def send(self, obj):
        self.sock.sendall((json.dumps(obj) + "\n").encode())

    def _read_line(self, timeout):
        self.sock.settimeout(timeout)
        while b"\n" not in self.buf:
            chunk = self.sock.recv(1 << 20)
            if not chunk:
                raise ConnectionError(f"{self.name}: connection closed")
            self.buf += chunk
        line, _, self.buf = self.buf.partition(b"\n")
        return json.loads(line)

    def expect(self, *types, timeout=15.0, where=None):
        """Next message of one of `types` (optionally matching where(msg)); others are kept."""
        for i, m in enumerate(self.inbox):
            if m.get("t") in types and (where is None or where(m)):
                return self.inbox.pop(i)
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                m = self._read_line(max(0.2, deadline - time.time()))
            except socket.timeout:
                continue
            if m.get("t") in types and (where is None or where(m)):
                return m
            self.inbox.append(m)
        raise TimeoutError(f"{self.name}: no {types} within {timeout}s; inbox={[m.get('t') for m in self.inbox][-12:]}")

    def send_raw(self, line):
        """Send a pre-built line exactly as given (live sync lines must start with {"t":"...")."""
        self.sock.sendall((line + "\n").encode())

    def drop(self, t):
        """Forget already-received messages of type t (so the next expect() waits for a fresh one)."""
        self.inbox = [m for m in self.inbox if m.get("t") != t]

    def none_of(self, t, wait=0.7):
        try:
            self.expect(t, timeout=wait)
            return False
        except TimeoutError:
            return True

    def hello(self, color="#33aaff"):
        self.send({"t": "hello", "name": self.name, "version": 2, "color": color, "game": "test"})
        w = self.expect("welcome")
        self.id = w["yourId"]
        self.name = w["name"]
        return w

    def upload(self, room, payload):
        b64 = [base64.b64encode(payload[i:i + CHUNK]).decode() for i in range(0, len(payload), CHUNK)]
        self.send({"t": "snap-begin", "room": room, "size": len(payload),
                   "sha": hashlib.sha256(payload).hexdigest(), "total": len(b64)})
        for i, c in enumerate(b64):
            self.send({"t": "snap-chunk", "room": room, "seq": i, "data": c})
        self.send({"t": "snap-end", "room": room})
        return self.expect("snap-stored", where=lambda m: m.get("room") == room)

    def download(self, room, timeout=30):
        begin = self.expect("snap-begin", timeout=timeout, where=lambda m: m.get("room") == room)
        parts = []
        for _ in range(begin["total"]):
            c = self.expect("snap-chunk", where=lambda m: m.get("room") == room)
            parts.append(base64.b64decode(c["data"]))
        self.expect("snap-end", where=lambda m: m.get("room") == room)
        data = b"".join(parts)
        return data, begin

    def close(self):
        try:
            self.send({"t": "bye"})
        except OSError:
            pass
        self.sock.close()


def rooms_by_id(msg):
    return {r["id"]: r for r in (msg.get("rooms") or [])}


def main():
    print(f"WorldfallRooms v2 end-to-end test -> {HOST}:{PORT}\n")
    try:
        socket.create_connection((HOST, PORT), timeout=3).close()
    except OSError:
        check("server reachable", False, f"nothing listening on {HOST}:{PORT}")
        return
    check("server reachable", True)

    # --- version check -------------------------------------------------------------------
    v = Client("old")
    v.send({"t": "hello", "name": "old", "version": 1})
    err = v.expect("error")
    check("old protocol version rejected", "version" in err["msg"])
    v.sock.close()

    alice_name, bob_name, carol_name = "alice" + RUN, "bob" + RUN, "carol" + RUN
    home = "home-" + alice_name
    world_a = secrets.token_bytes(300 * 1024)   # "alice's world" v1

    # --- own-world mode: alice hosts her currently open world ----------------------------
    alice = Client(alice_name)
    w = alice.hello("#ff5555")
    check("welcome has id, name, rooms, players", alice.id and w["name"] == alice_name and "rooms" in w)
    alice.send({"t": "join", "room": home, "name": "Alice's Island", "seed": True, "preferLocal": True})
    j = alice.expect("joined")
    check("alice hosts her own world without loading", j["role"] == "host" and j["load"] is False, str(j))
    req = alice.expect("snap-request")
    check("server asks the new host for a snapshot", req["room"] == home and req["reason"] == "seed")
    st = alice.upload(home, world_a)
    check("snapshot stored (v1)", st["version"] == 1)

    bob = Client(bob_name)
    wb = bob.hello("#55ff55")
    check("duplicate-free names", wb["name"] == bob_name)
    rooms = rooms_by_id(wb)
    check("bob sees alice's world in the world list", home in rooms and rooms[home]["host"] == alice_name
          and rooms[home]["name"] == "Alice's Island", str(rooms.get(home)))

    png = base64.b64encode(b"\x89PNG fake preview " + secrets.token_bytes(500)).decode()
    alice.send({"t": "preview", "room": home, "png": png, "stats": {"year": 42, "pop": 1234, "w": 192, "h": 192}})
    pv = bob.expect("preview", where=lambda m: m.get("room") == home)
    check("preview thumbnail broadcast", pv["png"] == png)
    rl = bob.expect("rooms", where=lambda m: rooms_by_id(m).get(home, {}).get("year") == 42)
    check("room stats (year/pop) in world list", rooms_by_id(rl)[home]["pop"] == 1234)

    # --- bob travels to alice's world: fresh snapshot from the live host -----------------
    world_a2 = secrets.token_bytes(250 * 1024)  # alice's world has changed since v1
    bob.send({"t": "join", "room": home})
    jb = bob.expect("joined")
    check("bob joins as guest and must load", jb["role"] == "guest" and jb["load"] is True and jb["host"] == alice_name)
    req = alice.expect("snap-request")
    check("host asked for a fresh snapshot for the joiner", req["reason"] == "join")
    alice.upload(home, world_a2)
    data, begin = bob.download(home)
    check("bob receives the FRESH world, byte-exact", data == world_a2 and begin["sha"] == hashlib.sha256(world_a2).hexdigest(),
          f"{len(data)} bytes")

    # --- relays ---------------------------------------------------------------------------
    alice.send({"t": "avatar", "on": True, "aid": "1234", "asset": "human", "x": 10.5, "y": 20.25, "hp": 80, "mhp": 100})
    a = bob.expect("avatar")
    check("avatar relayed with server-assigned id/name", a["id"] == alice.id and a["name"] == alice_name and a["x"] == 10.5)
    alice.send({"t": "avatar", "id": "SPOOF", "on": True, "x": 1, "y": 1})
    check("avatar id cannot be spoofed", bob.expect("avatar")["id"] == alice.id)
    bob.send({"t": "power", "p": "fire", "x": 33, "y": 44, "brush": "circ_3"})
    p = alice.expect("power")
    check("god power relayed to the host", p["p"] == "fire" and p["x"] == 33 and p["brush"] == "circ_3")
    bob.send({"t": "cursor", "x": 5, "y": 6, "p": "rain"})
    check("cursor relayed", alice.expect("cursor")["p"] == "rain")
    alice.send({"t": "speed", "s": "x5", "paused": False})
    check("game speed relayed", bob.expect("speed")["s"] == "x5")
    alice.send({"t": "act", "a": "attack", "x": 11, "y": 21})
    check("unit action relayed", bob.expect("act")["a"] == "attack")

    carol = Client(carol_name)
    carol.hello("#5555ff")
    alice.send({"t": "avatar", "on": True, "x": 2, "y": 2})
    bob.expect("avatar")
    check("avatar not leaked to other rooms", carol.none_of("avatar"))

    # --- live world sync (relayed raw, host-only for wu/wb/wdata) -------------------------------
    wu = json.dumps({"t": "wu", "room": home, "seq": 1, "part": 0, "parts": 1, "full": True, "idu": 900,
                     "ck": [2, 1], "a": ["human", "sheep"], "u": [101, 0, 105, 205, 80, 3, 1, 300, 310, 20]},
                    separators=(",", ":"))
    alice.send_raw(wu)
    got = bob.expect("wu")
    check("live sync: host's creature list reaches the guest unchanged", got == json.loads(wu), str(got)[:200])
    check("live sync not leaked to other rooms", carol.none_of("wu"))
    alice.send_raw(json.dumps({"t": "wdata", "room": home, "u": [{"id": 104, "asset_id": "sheep", "x": 30, "y": 31}], "b": []},
                              separators=(",", ":")))
    check("live sync: newborn data relayed", bob.expect("wdata")["u"][0]["id"] == 104)
    bob.send_raw(json.dumps({"t": "wu", "room": home, "seq": 9, "u": []}, separators=(",", ":")))
    check("live sync: guests cannot stream the world", alice.none_of("wu"))
    bob.send_raw(json.dumps({"t": "wneed", "room": home, "u": [104], "b": [7]}, separators=(",", ":")))
    need = alice.expect("wneed")
    check("live sync: guest's request reaches the host", need["u"] == [104] and need["b"] == [7])

    # --- everything else live (meta objects, creature details, terrain, world) -------------------
    for t, body in (("wm", {"k": "city", "d": [{"id": 3, "name": "Testburg"}], "s": [12345]}),
                    ("wa", {"r": [[101, "Bob", 3, "1", -1, -1, -1, -1, -1, -1, -1, -1, -1, 1, 1, 0, 0, 0, "", ""]]}),
                    ("wt", {"k": ["soil_low|grass_low|0|0"], "z": [[5] + [0] * 64]}),
                    ("ww", {"time": 1234.5, "age": "age_hope"})):
        alice.send_raw(json.dumps(dict({"t": t, "room": home}, **body), separators=(",", ":")))
        check(f"live sync: host's '{t}' reaches the guest unchanged", bob.expect(t) == dict({"t": t, "room": home}, **body))
    bob.send_raw(json.dumps({"t": "wm", "room": home, "k": "city", "d": []}, separators=(",", ":")))
    check("live sync: guests cannot stream meta objects", alice.none_of("wm"))
    bob.send_raw(json.dumps({"t": "wask", "room": home, "m": {"city": [3]}, "z": [5]}, separators=(",", ":")))
    ask = alice.expect("wask")
    check("live sync: guest's 'wask' reaches the host", ask["m"] == {"city": [3]} and ask["z"] == [5])

    # --- hits between games ----------------------------------------------------------------------
    bob.send({"t": "whit", "vid": "104", "hp": 0, "at": 1, "by": "77", "id": "spoofed"})
    wh = alice.expect("whit")
    check("guest's hit reaches the host, sender id set by the server", wh["vid"] == "104" and wh["id"] == bob.id, str(wh))
    alice.send({"t": "hit", "to": bob.id, "aid": "77", "dmg": 12, "at": 1, "by": "5"})
    check("hit on a possessed creature reaches its player", bob.expect("hit")["dmg"] == 12)
    check("hits not leaked to other rooms", carol.none_of("hit"))
    carol.send({"t": "chat", "text": "hi all!"})
    ca, cb = alice.expect("chat"), bob.expect("chat")
    check("chat is server-wide", ca["text"] == "hi all!" and cb["name"] == carol_name)

    # --- resync ------------------------------------------------------------------------------
    bob.send({"t": "resync"})
    alice.expect("snap-request", where=lambda m: m.get("reason") == "resync")
    world_a3 = secrets.token_bytes(100 * 1024)
    alice.upload(home, world_a3)
    data, _ = bob.download(home)
    check("resync delivers the host's current world", data == world_a3)

    # --- permissions ------------------------------------------------------------------------
    bob.send({"t": "rename-room", "room": home, "name": "Bob was here"})
    check("non-owner cannot rename", "owner" in bob.expect("error")["msg"])
    alice.send({"t": "rename-room", "room": home, "name": "Alice's Archipelago"})
    carol.expect("rooms", where=lambda m: rooms_by_id(m).get(home, {}).get("name") == "Alice's Archipelago")
    check("owner can rename", True)
    bob.send({"t": "snap-begin", "room": home, "size": 10, "total": 1})
    check("guests cannot upload over the host", "host" in bob.expect("error")["msg"])

    # --- shared world + host migration --------------------------------------------------------
    shared_payload = secrets.token_bytes(120 * 1024)
    shared = "world-t" + RUN  # throwaway world (never touch the real "shared" world)
    alice.send({"t": "join", "room": shared, "name": "Test world", "seed": True, "preferLocal": False})
    ja = alice.expect("joined", where=lambda m: m.get("room") == shared)
    role = bob.expect("role", timeout=5)
    check("host leaves -> guest becomes host", role["room"] == home and role["role"] == "host")
    check("alice seeds a new world as host", ja["role"] == "host" and ja["load"] is False)
    alice.expect("snap-request", where=lambda m: m.get("room") == shared)
    alice.upload(shared, shared_payload)

    carol.send({"t": "join", "room": shared, "seed": True})
    jc = carol.expect("joined", where=lambda m: m.get("room") == shared)
    check("second player joins the shared world as guest", jc["role"] == "guest" and jc["load"])
    alice.expect("snap-request", where=lambda m: m.get("room") == shared)
    alice.upload(shared, shared_payload)
    data, _ = carol.download(shared)
    check("shared world delivered", data == shared_payload)

    # --- dormant world served from storage -----------------------------------------------------
    bob.close()  # bob hosted home-alice; it becomes dormant with the last snapshot (world_a3)
    time.sleep(0.5)
    dave = Client("dave" + RUN)
    dave.hello()
    dave.send({"t": "join", "room": home})
    jd = dave.expect("joined")
    check("empty world: joiner becomes host and loads stored copy", jd["role"] == "host" and jd["load"] is True)
    data, _ = dave.download(home)
    check("stored copy is the latest snapshot", data == world_a3)
    dave.send({"t": "join", "room": "nope-" + RUN})
    check("joining an unknown world fails cleanly", "exist" in dave.expect("error")["msg"])

    # --- owner deletes their world ---------------------------------------------------------------
    alice.send({"t": "delete-room", "room": home})
    check("cannot delete a world someone is in", "still" in alice.expect("error")["msg"])
    dave.send({"t": "leave"})
    time.sleep(0.3)
    alice.drop("rooms")
    alice.send({"t": "delete-room", "room": home})
    alice.expect("rooms", where=lambda m: home not in rooms_by_id(m))
    check("owner deletes their empty world", True)

    # --- connection drop: the last host resumes the world from its open copy ----------------------
    erin_name = "erin" + RUN
    erin = Client(erin_name)
    erin.hello()
    extra = "world-" + RUN
    erin.send({"t": "join", "room": extra, "name": "Erin's extra", "seed": True, "preferLocal": True})
    erin.expect("joined", where=lambda m: m.get("room") == extra)
    erin.expect("snap-request", where=lambda m: m.get("room") == extra)
    erin.upload(extra, b"extra-v1")
    erin.sock.close()                       # dropped, no "bye"
    time.sleep(1.0)
    frank = Client("frank" + RUN)
    frank.hello()
    frank.send({"t": "join", "room": extra, "seed": True, "preferLocal": True, "resume": True})
    jf = frank.expect("joined", where=lambda m: m.get("room") == extra)
    check("resume is refused to someone who wasn't the last host", jf["role"] == "host" and jf["load"] is True, str(jf))
    frank.download(extra)
    frank.send({"t": "leave"})
    time.sleep(0.5)
    erin = Client(erin_name)
    erin.hello()
    erin.send({"t": "join", "room": extra, "seed": True, "preferLocal": True, "resume": True})
    je = erin.expect("joined", where=lambda m: m.get("room") == extra)
    check("last host resumes the world from its open copy", je["role"] == "host" and je["load"] is False, str(je))
    erin.expect("snap-request", where=lambda m: m.get("room") == extra)
    erin.upload(extra, b"extra-v2")
    erin.send({"t": "leave"})
    time.sleep(0.3)
    erin.drop("rooms")
    erin.send({"t": "delete-room", "room": extra})
    erin.expect("rooms", where=lambda m: extra not in rooms_by_id(m))
    erin.close()
    frank.close()

    # --- ping, players, bye ---------------------------------------------------------------------
    alice.send({"t": "ping", "ts": 123})
    check("ping/pong", alice.expect("pong")["ts"] == 123)
    carol.close()
    alice.expect("players", where=lambda m: all(p["name"] != carol_name for p in (m.get("players") or [])))
    check("players list updated on disconnect", True)
    alice.send({"t": "leave"})
    time.sleep(0.3)
    alice.drop("rooms")
    alice.send({"t": "delete-room", "room": shared})
    alice.expect("rooms", where=lambda m: shared not in rooms_by_id(m))
    alice.send({"t": "ping", "ts": 7})
    alice.expect("pong")  # make sure the server processed everything before we hang up
    check("test world cleaned up", True)
    alice.close()
    dave.close()


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:  # noqa: BLE001
        check(f"unexpected exception: {exc!r}", False)
    passed = sum(1 for _, ok in RESULTS if ok)
    print(f"\n{passed}/{len(RESULTS)} checks passed")
    sys.exit(0 if passed == len(RESULTS) else 1)
