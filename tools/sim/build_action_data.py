"""Build the simulator's action data from the public game data (xivapi/ffxiv-datamining csv/en),
for every action named in Combat Pilot's job files. Run once per patch, commit the output.

Usage: python tools/sim/build_action_data.py <ffxiv-datamining checkout> [out=CombatPilot.Sim/actions.json]
"""
import csv, json, sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
dm = Path(sys.argv[1]) / "csv" / "en"
out = Path(sys.argv[2]) if len(sys.argv) > 2 else ROOT / "CombatPilot.Sim" / "actions.json"

def read(name):
    with open(dm / name, encoding="utf-8-sig") as f:
        rows = list(csv.reader(f))
    # Some exports have a type row and an offset row before data; the header is the row starting with '#'.
    head = next(i for i, r in enumerate(rows) if r and r[0] == "#")
    cols = rows[head]
    return [dict(zip(cols, r)) for r in rows[head + 1:] if r and r[0].isdigit()]

cats = {int(r["#"]): r for r in read("ClassJobCategory.csv")}
actions = read("Action.csv")
T = lambda v: v == "True"

out_data = {}
for job_file in sorted((ROOT / "CombatPilot" / "Jobs").glob("*.json")):
    job = json.loads(job_file.read_text(encoding="utf-8"))
    abbrs = job["classJobs"]
    names = {a["name"] for a in job["actions"]}
    found = {}
    for r in actions:
        n = r["Name"]
        if n not in names or T(r["IsPvP"]) or int(r["ClassJobLevel"] or 0) == 0: continue
        cat = cats.get(int(r["ClassJobCategory"] or 0), {})
        if not any(T(cat.get(a, "False")) for a in abbrs): continue
        # Prefer the hotbar version when a name exists twice (as the plugin does).
        if n in found and not (not found[n]["_player"] and T(r["IsPlayerAction"])): continue
        found[n] = {
            "_player": T(r["IsPlayerAction"]),
            "id": int(r["#"]), "level": int(r["ClassJobLevel"]),
            "cdGroup": int(r["CooldownGroup"]), "cdGroup2": int(r["AdditionalCooldownGroup"]),
            "recast": int(r["Recast100ms"]) / 10, "cast": int(r["Cast100ms"]) / 10,
            "maxCharges": int(r["MaxCharges"]), "range": int(r["Range"]),
            "effectRange": int(r["EffectRange"]), "castType": int(r["CastType"]), "xAxis": int(r["XAxisModifier"]),
            "mpCost": int(r["PrimaryCostValue"]) * 100 if r["PrimaryCostType"] == "3" else 0,
            "canTargetParty": T(r["CanTargetParty"]), "comboFrom": int(r["ActionCombo"] or 0),
            "affectsPosition": T(r["AffectsPosition"]),
        }
    for v in found.values(): v.pop("_player")
    missing = sorted(names - set(found) - {"Potion"})
    out_data[job["job"]] = {"actions": found, "missing": missing}
    print(f"{job['job']}: {len(found)} actions" + (f"; not in game data: {missing}" if missing else ""))

out.parent.mkdir(parents=True, exist_ok=True)
out.write_text(json.dumps(out_data, indent=1) + "\n", encoding="utf-8")
print("wrote", out)
