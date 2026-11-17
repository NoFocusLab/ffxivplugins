"""Draw the plugin icons: a white symbol on a rounded, colour-coded tile.

Usage: python tools/make_icons.py            (writes <Plugin>/images/icon.png for every plugin below)
Needs rsvg-convert (librsvg) on PATH. Edit GLYPHS / GROUP to change a symbol or colour, then rerun.
The SVG sources are written next to each PNG (images/icon.svg) so they can be tweaked by hand.

Colour groups:
  combat  (crimson) - fights, mitigation, healer/party helpers
  economy (amber)   - retainers, materials, currencies, turn-ins
  travel  (green)   - movement and Gold Saucer / leisure chores
  utility (violet)  - tools that manage other plugins
"""
import math
import subprocess
from pathlib import Path

SIZE = 256
ROOT = Path(__file__).resolve().parent.parent

COLOURS = {
    "combat": ("#C9434A", "#7E1F2E"),
    "economy": ("#DDA334", "#94600F"),
    "travel": ("#47A35A", "#1E5E31"),
    "utility": ("#7B5FD3", "#3D2A86"),
}

W = "#FFFFFF"
SOFT = "rgba(255,255,255,0.55)"

GLYPHS = {
    # Shield with a chevron: stance / holding aggro.
    "TankStanceManager": ("combat", f"""
      <path d="M128 50 L190 72 V128 C190 168 162 194 128 208 C94 194 66 168 66 128 V72 Z" fill="{W}"/>
      <path d="M100 118 L128 100 L156 118 V140 L128 122 L100 140 Z" fill="currentColor"/>
      <path d="M100 152 L128 134 L156 152 V170 L128 152 L100 170 Z" fill="currentColor" opacity="0.7"/>"""),
    # 3x3 grid, one cell with a cooldown sweep.
    "CooldownGrid": ("combat", "".join(
        f'<rect x="{62 + c * 46}" y="{62 + r * 46}" width="38" height="38" rx="7" fill="{W}" '
        f'opacity="{1 if (r, c) != (1, 1) else 0.3}"/>' for r in range(3) for c in range(3)) + f"""
      <path d="M127 127 L127 108 A19 19 0 1 1 108 127 Z" fill="{W}" transform="translate(1 1)"/>"""),
    # Timeline with a shield marker on the next mechanic.
    "MitPlanner": ("combat", f"""
      <rect x="52" y="168" width="152" height="8" rx="4" fill="{W}"/>
      <circle cx="72" cy="172" r="11" fill="{W}"/>
      <circle cx="184" cy="172" r="11" fill="{W}"/>
      <circle cx="128" cy="172" r="15" fill="{W}"/>
      <path d="M128 46 L170 61 V94 C170 120 152 136 128 146 C104 136 86 120 86 94 V61 Z" fill="{W}"/>"""),
    # Waveform bars with a record dot.
    "CombatRecorder": ("combat", "".join(
        f'<rect x="{58 + i * 26}" y="{128 - h / 2}" width="16" height="{h}" rx="8" fill="{W}"/>'
        for i, h in enumerate([40, 92, 130, 70, 108, 50])) + f"""
      <circle cx="196" cy="70" r="18" fill="{W}"/><circle cx="196" cy="70" r="9" fill="currentColor"/>"""),
    # Speech bubble with an upward arrow: "Raising X" in chat.
    "RaiseCallouts": ("combat", f"""
      <path d="M64 70 H192 A16 16 0 0 1 208 86 V156 A16 16 0 0 1 192 172 H118 L84 202 V172 H64 A16 16 0 0 1 48 156 V86 A16 16 0 0 1 64 70 Z" fill="{W}"/>
      <path d="M128 88 L160 122 H140 V156 H116 V122 H96 Z" fill="currentColor"/>"""),
    # Life ring: rescue.
    "RescueHelper": ("combat", f"""
      <circle cx="128" cy="128" r="76" fill="none" stroke="{W}" stroke-width="34"/>
      <circle cx="128" cy="128" r="76" fill="none" stroke="currentColor" stroke-width="34"
              stroke-dasharray="22 97.4" stroke-dashoffset="11" opacity="0.55"/>"""),
    # Map pin.
    "MapClickNav": ("travel", f"""
      <path d="M128 214 C128 214 64 150 64 104 A64 64 0 0 1 192 104 C192 150 128 214 128 214 Z" fill="{W}"/>
      <circle cx="128" cy="104" r="24" fill="currentColor"/>"""),
    # Card with a star: Triple Triad / Gold Saucer.
    "SaucerRun": ("travel", f"""
      <rect x="70" y="56" width="104" height="144" rx="14" fill="{SOFT}" transform="rotate(-12 122 128)"/>
      <rect x="88" y="56" width="104" height="144" rx="14" fill="{W}" transform="rotate(8 140 128)"/>
      <path d="M140 92 L150 116 L176 118 L156 134 L163 160 L140 146 L117 160 L124 134 L104 118 L130 116 Z"
            fill="currentColor" transform="rotate(8 140 128)"/>"""),
    # Crate with an arrow dropping in: stash mats.
    "MaterialStash": ("economy", f"""
      <path d="M56 112 H200 V196 A10 10 0 0 1 190 206 H66 A10 10 0 0 1 56 196 Z" fill="{W}"/>
      <rect x="48" y="96" width="160" height="22" rx="6" fill="{W}"/>
      <rect x="104" y="138" width="48" height="12" rx="6" fill="currentColor"/>
      <path d="M128 90 L160 58 H140 V34 H116 V58 H96 Z" fill="{W}"/>"""),
    # Faceted gem: materia.
    "MateriaManager": ("economy", f"""
      <path d="M88 60 H168 L208 104 L128 208 L48 104 Z" fill="{W}"/>
      <path d="M48 104 H208 M88 60 L108 104 L128 208 L148 104 L168 60 M108 104 L128 60 L148 104"
            fill="none" stroke="currentColor" stroke-width="7" stroke-linejoin="round" opacity="0.6"/>"""),
    # Wax seal with a check: Grand Company seals / turn-ins.
    "TurnInManager": ("economy", "<path d=\"" + " ".join(
        ("M" if i == 0 else "L") + f" {128 + (78 if i % 2 == 0 else 66) * math.cos(i * 3.14159 / 12):.1f}"
        f" {128 + (78 if i % 2 == 0 else 66) * math.sin(i * 3.14159 / 12):.1f}" for i in range(24)) + f' Z" fill="{W}"/>' + f"""
      <circle cx="128" cy="128" r="50" fill="none" stroke="currentColor" stroke-width="6" opacity="0.5"/>
      <path d="M102 130 L120 148 L156 108" fill="none" stroke="currentColor" stroke-width="14"
            stroke-linecap="round" stroke-linejoin="round"/>"""),
    # Sprout on a soil mound: topsoil.
    "PoeticsSpender": ("economy", f"""
      <path d="M44 204 C60 166 196 166 212 204 Z" fill="{W}"/>
      <rect x="123" y="104" width="10" height="76" rx="5" fill="{W}"/>
      <path d="M128 128 C92 128 68 104 66 70 C102 70 126 92 128 128 Z" fill="{W}"/>
      <path d="M128 112 C140 80 164 62 196 62 C194 96 170 116 128 112 Z" fill="{W}"/>"""),
    # Helmet with an up arrow: gear upgrades.
    "RetainerGear": ("economy", f"""
      <path d="M52 180 V130 A70 70 0 0 1 192 130 V180 Z" fill="{W}"/>
      <rect x="72" y="134" width="100" height="16" rx="8" fill="currentColor"/>
      <rect x="116" y="150" width="12" height="30" fill="currentColor"/>
      <path d="M196 54 L222 84 H206 V112 H186 V84 H170 Z" fill="{W}"/>"""),
    # 2x2 tiles: the hub itself.
    "PluginHub": ("utility", f"""
      <rect x="56" y="56" width="66" height="66" rx="14" fill="{W}"/>
      <rect x="134" y="56" width="66" height="66" rx="14" fill="{W}" opacity="0.6"/>
      <rect x="56" y="134" width="66" height="66" rx="14" fill="{W}" opacity="0.6"/>
      <rect x="134" y="134" width="66" height="66" rx="14" fill="{W}"/>"""),
}


# Icons that live only in Plugin Hub (PluginHub/Icons/<name>.png, embedded in the hub): its built-in
# tiles, plus stand-ins for other people's plugins that have no usable icon (key = their InternalName).
HUB_ICONS = {
    # Puzzle piece with a down arrow: the plugin installer.
    "Installer": ("utility", f"""
      <path d="M70 92 H104 A22 22 0 1 1 140 92 H174 V126 A22 22 0 1 1 174 162 V196 H70 V162 A22 22 0 1 0 70 126 Z" fill="{W}"/>
      <path d="M122 114 V160 M104 144 L122 162 L140 144" fill="none" stroke="currentColor" stroke-width="12"
            stroke-linecap="round" stroke-linejoin="round"/>"""),
    # Two chasing arrows: plugin updates.
    "Updates": ("utility", f"""
      <path d="M190 118 A62 62 0 0 0 78 86" fill="none" stroke="{W}" stroke-width="20" stroke-linecap="round"/>
      <path d="M58 70 L106 66 L84 106 Z" fill="{W}"/>
      <path d="M66 138 A62 62 0 0 0 178 170" fill="none" stroke="{W}" stroke-width="20" stroke-linecap="round"/>
      <path d="M198 186 L150 190 L172 150 Z" fill="{W}"/>"""),
    # Gear: Dalamud settings.
    "DalamudSettings": ("utility", "".join(
        f'<rect x="116" y="42" width="24" height="40" rx="6" fill="{W}" transform="rotate({a} 128 128)"/>'
        for a in range(0, 360, 45)) + f"""
      <circle cx="128" cy="128" r="58" fill="{W}"/>
      <circle cx="128" cy="128" r="24" fill="currentColor"/>"""),
    # Coin in front of a coin stack: Dagobert (market-board auto-pricing).
    "Dagobert": ("economy", f"""
      <ellipse cx="166" cy="186" rx="46" ry="16" fill="{SOFT}"/>
      <rect x="120" y="130" width="92" height="56" fill="{SOFT}"/>
      <ellipse cx="166" cy="158" rx="46" ry="16" fill="{SOFT}" opacity="0.6"/>
      <ellipse cx="166" cy="130" rx="46" ry="16" fill="{W}"/>
      <circle cx="104" cy="142" r="58" fill="{W}"/>
      <circle cx="104" cy="142" r="44" fill="none" stroke="currentColor" stroke-width="6" opacity="0.6"/>
      <path d="M120 120 C112 110 88 110 84 124 C80 140 124 140 122 158 C120 174 92 174 84 162 M104 102 V182"
            fill="none" stroke="currentColor" stroke-width="10" stroke-linecap="round"/>"""),
}


def svg(group: str, body: str) -> str:
    top, bottom = COLOURS[group]
    return f"""<svg xmlns="http://www.w3.org/2000/svg" width="{SIZE}" height="{SIZE}" viewBox="0 0 256 256" color="{bottom}">
  <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="{top}"/><stop offset="1" stop-color="{bottom}"/></linearGradient></defs>
  <rect x="4" y="4" width="248" height="248" rx="44" fill="url(#g)"/>
  <rect x="4" y="4" width="248" height="248" rx="44" fill="none" stroke="#000" stroke-opacity="0.25" stroke-width="4"/>
  {body}
</svg>
"""


def render(src: Path, png: Path, group: str, body: str) -> None:
    src.parent.mkdir(parents=True, exist_ok=True)
    src.write_text(svg(group, body), encoding="utf-8")
    subprocess.run(["rsvg-convert", "-w", str(SIZE), "-h", str(SIZE), "-o", str(png), str(src)], check=True)


def main() -> None:
    for plugin, (group, body) in GLYPHS.items():
        folder = ROOT / plugin / "images"
        render(folder / "icon.svg", folder / "icon.png", group, body)
        print(f"{plugin}: {group}")
    for name, (group, body) in HUB_ICONS.items():
        folder = ROOT / "PluginHub" / "Icons"
        render(folder / f"{name}.svg", folder / f"{name}.png", group, body)
        print(f"hub {name}: {group}")


if __name__ == "__main__":
    main()
