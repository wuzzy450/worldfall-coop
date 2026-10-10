"""Compare the "TEST looks:" lines (and pictures) of two profile folders: python compare-looks.py <host dir> <guest dir>.
The parts line must match exactly. Pictures: the share of pixels that differ (the pose follows each PC's clock,
so a little is expected); with --save, a red diff image goes next to each pair that differs."""
import os, re, sys

def lines(d):
    out = {}
    for l in open(os.path.join(d, "log.txt"), encoding="utf-8", errors="replace"):
        m = re.search(r"TEST looks: (e\d+ t\d+ #\d+) (.*)$", l)
        if m:
            out[m.group(1)] = m.group(2).strip()
    return out

def strip_hash(v):
    # the picture hash, and the sprite's animation frame (gen_walk_1 vs gen_walk_0: where each PC is in the
    # walk cycle, drawn from its own clock), are not the look
    v = re.sub(r" \| picture \w+$", "", v)
    return re.sub(r"(sprite \S*?)_\d+\b", r"\1", v)

host, guest = sys.argv[1], sys.argv[2]
save = "--save" in sys.argv
h, g = lines(host), lines(guest)
try:
    from PIL import Image, ImageChops
except ImportError:
    Image = None
same = differ = undrawn = 0
worst = []
for k in sorted(set(h) & set(g)):
    if h[k].endswith("not drawn") or g[k].endswith("not drawn"):
        undrawn += 1   # that PC hadn't loaded the creature's sprite yet: nothing to compare
        continue
    if strip_hash(h[k]) != strip_hash(g[k]):
        differ += 1
        print("DIFFERS", k, "\n  host ", h[k], "\n  guest", g[k])
        continue
    same += 1
    if Image is None:
        continue
    e, t, i = k.split()
    name = "%s-%s-%s.png" % (e, t, i[1:])
    a, b = os.path.join(host, "looks", name), os.path.join(guest, "looks", name)
    if not (os.path.exists(a) and os.path.exists(b)):
        continue
    ia, ib = Image.open(a).convert("RGB"), Image.open(b).convert("RGB")
    diff = ImageChops.difference(ia, ib).convert("L").point(lambda v: 255 if v > 24 else 0)
    px = diff.get_flattened_data() if hasattr(diff, "get_flattened_data") else diff.getdata()
    share = sum(1 for v in px if v) / float(ia.width * ia.height)
    worst.append((share, k))
    if save and share > 0:
        diff.save(os.path.join(guest, "looks", "diff-" + name))
print("looks: %d match, %d differ, %d not drawn on a PC, host only %d, guest only %d" % (same, differ, undrawn, len(set(h) - set(g)), len(set(g) - set(h))))
if worst:
    worst.sort(reverse=True)
    print("pictures: most different %.1f%% of pixels (%s); median %.1f%%" % (worst[0][0] * 100, worst[0][1], worst[len(worst) // 2][0] * 100))
