"""Spot duty runs that aren't valid dungeon data, so the analysis tools leave them out.

Rule (owner, 2026-11-16): ignore Trust / Duty Support runs, solo runs and unsynced runs in trash,
dungeon-route, healing and party analysis. Trust boss fights can still teach boss mitigation tactics
(build_timeline.py keeps them); solo/unsynced boss fights can't (overleveled), so it skips those.

How a run is recognised:
  - Combat Recorder 0.4.3+: an "excluded" event (why=trust|solo|unsynced) and "_<why>" in the file name.
  - Mit Planner 0.11.1+ trash logs: an EXCLUDED line (logging stops there).
  - Older files: a duty run with no other players in the party list -> "trust or solo".
    [Assumption] older unsynced runs WITH a party can't be told apart from normal ones (no level sync recorded).
"""
import gzip
import json
import re
from pathlib import Path

KINDS = ("trust", "solo", "unsynced")


def _events(path):
    op = gzip.open if str(path).endswith(".gz") else open
    out = []
    with op(path, "rt", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    return out


def raw_reason(path, events=None):
    """Combat Recorder .jsonl(.gz): 'trust' | 'solo' | 'unsynced' | 'trust or solo' (older file) | None."""
    m = re.search(r"_(trust|solo|unsynced)\.jsonl", Path(path).name)
    if m:
        return m.group(1)
    if events is None:
        events = _events(path)
    for e in events:
        if e.get("e") == "excluded":
            return e.get("why", "excluded")
        if e.get("e") == "npc_party":
            return "trust"
    head = next((e for e in events if e.get("e") == "head"), {})
    others = [p for p in head.get("party", []) if p.get("slot") != "me"]
    if head.get("cfc") and not others:
        return "trust or solo"
    return None


def trash_reason(path):
    """Mit Planner trash log (.log): same answers as raw_reason, or None."""
    text = Path(path).read_text(encoding="utf-8", errors="replace")
    m = re.search(r"\tEXCLUDED\twhy=(\w+)", text)
    if m:
        return m.group(1)
    if "\tNPCPARTY\t" in text:
        return "trust"
    for line in text.splitlines():
        if "\tZONE\t" in line:
            if re.search(r"party=(solo/trust\?)?(\t|$)", line):
                return "trust or solo"
            break
    return None
