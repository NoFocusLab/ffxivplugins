"""Per-pull check of an IINACT log for one player: DPS, GCD roll and gaps, positionals (side from the
logged positions vs the target's facing, True North coverage, how far inside the zone), and oGCD use
per minute. Usage: python tools/pull_check.py <Network_*.log> [--after HH:MM] [--player "Name"]"""
import argparse, math
from collections import Counter
from datetime import datetime

NEED = {'Chaotic Spring': 'rear', 'Wheeling Thrust': 'rear', 'Fang and Claw': 'flank',     # DRG
        'Aeolian Edge': 'rear', 'Armor Crush': 'flank', 'Trick Attack': 'rear'}            # NIN
OGCD_WATCH = ["Stardiver", "Starcross", "Geirskogul", "Nastrond", "High Jump", "Mirage Dive", "Life Surge",
              "Wyrmwind Thrust", "True North", "Lance Charge", "Battle Litany", "Dragonfire Dive"]

def damage(h):
    b = h.zfill(8)
    return int(b[6:8] + b[:2] + b[2:4], 16) if int(b[4:6], 16) & 0x40 else int(b[:4], 16)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log"); ap.add_argument("--after", default="00:00"); ap.add_argument("--player", default=None)
    a = ap.parse_args()
    me = a.player; tn = []; ev = []; dmg = []
    for line in open(a.log, encoding="utf-8", errors="ignore"):
        p = line.split("|")
        if p[0] == "02" and me is None and len(p) > 3: me = p[3]
        if len(p) < 9: continue
        if p[1][11:16] < a.after: continue
        t = datetime.fromisoformat(p[1][:26]).timestamp()
        if p[0] in ("26", "30") and p[3] == "True North" and p[8] == me:
            if p[0] == "26": tn.append([t, t + float(p[4])])
            elif tn: tn[-1][1] = t
        if p[0] in ("21", "22") and len(p) > 44 and p[3] == me:
            try:
                if int(p[8], 16) & 0xFF == 3: dmg.append((t, damage(p[9])))
            except ValueError: pass
            if p[5] == "attack" or (ev and ev[-1][1] == p[5] and t - ev[-1][0] < 0.3): continue
            try:
                tx, ty, th, sx, sy = (float(p[i]) for i in (30, 31, 33, 40, 41))
                rel = abs((math.degrees(math.atan2(sx - tx, sy - ty) - th) + 180) % 360 - 180)
            except ValueError: rel = -1
            ev.append((t, p[5], p[1][11:22], rel, p[29] == "" ))
    pulls = []
    for e in ev:
        if not pulls or e[0] - pulls[-1][-1][0] > 20: pulls.append([])
        pulls[-1].append(e)
    for pl in pulls:
        if len(pl) < 40: continue
        start, end = pl[0][0], pl[-1][0] + 2.5
        secs = end - start
        c = Counter(e[1] for e in pl)
        tot = miss = cov = close = 0; notes = []
        for t, n, ts, ang, _ in pl:
            if n not in NEED or ang < 0: continue
            tot += 1
            if any(x <= t <= y for x, y in tn): cov += 1; continue
            inside = ang - 135 if NEED[n] == "rear" else min(ang - 45, 135 - ang)
            if inside < 0: miss += 1; notes.append(f"MISS {ts} {n} at {ang:.0f} deg")
            elif inside < 20: close += 1; notes.append(f"close {ts} {n} {inside:.0f} deg inside")
        d = sum(v for t, v in dmg if start <= t <= end + 3)
        print(f"Pull {pl[0][2]}: {secs:.0f}s, DPS {d / secs:,.0f}")
        print(f"  positionals {tot}: missed {miss}, under True North {cov}, within 20 deg of the edge {close}")
        for n in notes: print("   ", n)
        print("  per min: " + ", ".join(f"{k} {c[k] / secs * 60:.2f}" for k in OGCD_WATCH if c[k]))

if __name__ == "__main__":
    main()
