"""Put the plugin's icon link into the manifest inside its latest.zip (and the copy next to it).

Dalamud shows an installed plugin's icon from the manifest inside the zip it installed, not from
repo.json, so without this every installed plugin shows the default question mark.
Usage: python add_icon_to_zip.py <PluginFolder>
"""
import json
import os
import shutil
import sys
import zipfile
from pathlib import Path

plugin = sys.argv[1]
owner_repo = os.environ.get("GITHUB_REPOSITORY", "NoFocusLab/ffxivplugins")
if not (Path(plugin) / "images" / "icon.png").exists():
    print(f"{plugin}: no images/icon.png, skipped")
    sys.exit(0)
icon = f"https://raw.githubusercontent.com/{owner_repo}/main/{plugin}/images/icon.png"

zip_path = next((Path(plugin) / "bin").rglob("latest.zip"))
name = f"{plugin}.json"
tmp = zip_path.with_suffix(".tmp")
with zipfile.ZipFile(zip_path) as src, zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED) as dst:
    for item in src.infolist():
        data = src.read(item)
        if item.filename == name:
            manifest = json.loads(data.decode("utf-8-sig"))
            manifest["IconUrl"] = icon
            data = json.dumps(manifest, indent=2).encode("utf-8")
        dst.writestr(item, data)
shutil.move(tmp, zip_path)

side = zip_path.parent / name
if side.exists():
    manifest = json.loads(side.read_text(encoding="utf-8-sig"))
    manifest["IconUrl"] = icon
    side.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(f"{plugin}: IconUrl -> {icon}")
