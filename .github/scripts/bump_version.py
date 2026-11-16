"""Bump the last part of a plugin's <Version> (0.8.0.0 -> 0.8.0.1) and print the new version.

Usage: python bump_version.py <Plugin>/<Plugin>.csproj
"""
import re
import sys
from pathlib import Path

path = Path(sys.argv[1])
text = path.read_text(encoding="utf-8")
m = re.search(r"<Version>(\d+)\.(\d+)\.(\d+)\.(\d+)</Version>", text)
if not m:
    sys.exit(f"No 4-part <Version> in {path}")
new = f"{m[1]}.{m[2]}.{m[3]}.{int(m[4]) + 1}"
path.write_text(text.replace(m[0], f"<Version>{new}</Version>", 1), encoding="utf-8")
print(new)
