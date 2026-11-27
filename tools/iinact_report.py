#!/usr/bin/env python3
"""
Quick Warrior review of an IINACT / ACT network log (Network_*.log), per pull:
GCD rolling, damage per second, Inner Release stacks used/expired (from the status lines),
Primal Rend -> Ruination delay, and charge waste for Infuriate and Onslaught.

    python tools/iinact_report.py Network_30305_20261127.log [--player "Name"] [--split HH:MM]

--split labels pulls before/after a clock time (e.g. Combat Pilot Live vs RSR runs).
Estimates: charges are assumed full when a pull starts; Infuriate's 5 s reduction is applied
per Fell Cleave / Inner Chaos / Decimate / Chaotic Cyclone.
"""
import argparse
import datetime as dt

GCD = {"Heavy Swing", "Maim", "Storm's Path", "Storm's Eye", "Fell Cleave", "Inner Chaos", "Primal Rend",
       "Primal Ruination", "Decimate", "Tomahawk", "Overpower", "Mythril Tempest", "Chaotic Cyclone"}
SPENDERS = {"Fell Cleave", "Inner Chaos", "Decimate", "Chaotic Cyclone"}


def ts(s):
    return dt.datetime.fromisoformat(s[:26] + s[-6:])


def damage(h):
    h = h.zfill(8)
    a, b, c, d = h[0:2], h[2:4], h[4:6], h[6:8]
    return int(d + a + b, 16) if int(c, 16) & 0x40 else int(a + b, 16)


def charge_waste(uses, start, end, max_charges, recast, reductions=()):
    """Seconds spent at full charges (cooldown lost), simulating charges from full."""
    events = sorted([(t, "use") for t in uses] + [(t, "cut") for t in reductions])
    charges, next_at, t_prev, waste = max_charges, None, start, 0.0
    for t, kind in events + [(end, "end")]:
        # advance time
        while next_at is not None and next_at <= t:
            charges += 1
            next_at = next_at + dt.timedelta(seconds=recast) if charges < max_charges else None
            if charges == max_charges: t_prev = max(t_prev, next_at or t_prev)
        if charges == max_charges:
            waste += max(0.0, (t - t_prev).total_seconds())
        t_prev = t
        if kind == "use":
            if charges == max_charges: next_at = t + dt.timedelta(seconds=recast)
            charges = max(0, charges - 1)
        elif kind == "cut" and next_at is not None:
            next_at -= dt.timedelta(seconds=5)
    return waste


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("log")
    ap.add_argument("--player", default=None, help="character name (default: the logging player)")
    ap.add_argument("--split", default=None, help="HH:MM: label pulls before/after this time")
    a = ap.parse_args()

    me_id, me_name = None, a.player
    acts, hits, ir = [], [], []
    ir_start = None
    for line in open(a.log, encoding="utf-8", errors="replace"):
        c = line.split("|")
        if c[0] == "02" and me_id is None and (me_name is None or c[3] == me_name):
            me_id, me_name = c[2], c[3]
        if me_id is None:
            continue
        if c[0] in ("21", "22") and c[2] == me_id and len(c) > 10:
            t = ts(c[1])
            try:
                if int(c[8], 16) & 0xFF == 3:
                    hits.append((t, damage(c[9])))
            except ValueError:
                pass
            if c[5] not in ("attack", "") and not (acts and acts[-1][1] == c[5] and (t - acts[-1][0]).total_seconds() < 0.3):
                acts.append((t, c[5]))
        if c[0] == "26" and c[3] == "Inner Release" and c[8] == me_name and ir_start is None:
            ir_start = ts(c[1])
        if c[0] == "30" and c[3] == "Inner Release" and c[8] == me_name and ir_start:
            ir.append((ir_start, ts(c[1])))
            ir_start = None

    gcds = [x for x in acts if x[1] in GCD]
    if not gcds:
        print("No weaponskills found for", me_name)
        return
    pulls = [[gcds[0]]]
    for x in gcds[1:]:
        (pulls.append([x]) if (x[0] - pulls[-1][-1][0]).total_seconds() > 15 else pulls[-1].append(x))

    print(f"Player: {me_name}")
    for p in pulls:
        s, e = p[0][0], p[-1][0] + dt.timedelta(seconds=2.5)
        secs = (e - s).total_seconds()
        if secs < 30:
            continue
        label = ""
        if a.split:
            hh, mm = map(int, a.split.split(":"))
            label = " [after split]" if (s.hour, s.minute) >= (hh, mm) else " [before split]"
        gaps = [round((p[i + 1][0] - p[i][0]).total_seconds(), 2) for i in range(len(p) - 1)]
        dmg = sum(d for t, d in hits if s <= t <= e)
        A = [x for x in acts if s <= x[0] <= e]
        n = lambda name: sum(1 for x in A if x[1] == name)
        expired = sum(max(0, 3 - sum(1 for t, m in acts if a0 <= t <= b0 and m in ("Fell Cleave", "Decimate")))
                      for a0, b0 in ir if s <= a0 <= e)
        rend = [t for t, m in A if m == "Primal Rend"]
        ruin = [t for t, m in A if m == "Primal Ruination"]
        delays = [round(min([(r - q).total_seconds() for r in ruin if r > q] or [99]), 1) for q in rend]
        inf = [t for t, m in A if m == "Infuriate"]
        cuts = [t for t, m in A if m in SPENDERS]
        ons = [t for t, m in A if m == "Onslaught"]
        print(f"\nPull {s:%H:%M:%S}{label}: {secs:.0f}s, {len(p)} GCDs, avg GCD {sum(gaps) / max(1, len(gaps)):.3f}s, "
              f"gaps > 2.7s: {[g for g in gaps if g > 2.7]}")
        print(f"  DPS {dmg / secs:,.0f} (total {dmg:,})")
        print(f"  Inner Release {n('Inner Release')}, stacks expired {expired}; Primal Rend {n('Primal Rend')}, "
              f"Ruination {n('Primal Ruination')} (delays {delays}); Primal Wrath {n('Primal Wrath')}")
        print(f"  Infuriate {len(inf)} (Inner Chaos {n('Inner Chaos')}), ~{charge_waste(inf, s, e, 2, 60, cuts):.0f}s at full charges; "
              f"Onslaught {len(ons)}, ~{charge_waste(ons, s, e, 3, 30):.0f}s at full charges; Upheaval {n('Upheaval')}")


if __name__ == "__main__":
    main()
