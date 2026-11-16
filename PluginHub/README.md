# Plugin Hub

Bars of plugin buttons, laid out in rows and columns like your hotbars, so you don't
have to dig through `/xlplugins` or remember each plugin's command.

**Tiles**
- Click: opens the plugin's main window (its settings if it has no main window).
- Right-click: the plugin's settings.
- Shift+click: turns the plugin on or off (Dalamud's `/xltoggleplugin`; can be switched off in Options).
- Hover: the plugin's name and one-line description, whether it's off, and what each click does.
- A tile can have its own **command** (run on click instead of opening the window) and
  **label**. Commands must be plugin or Dalamud commands; game commands like `/gearset` aren't supported.
- Built-in tiles: Plugin installer, Plugin updates, Dalamud settings, Plugin Hub settings.

**Icons.** Our own plugins' icons are built into the hub. Other plugins' icons are downloaded
once from the icon link in their listing and kept in the hub's config folder. A plugin without
an icon (or with one Dalamud can't read) shows its name, or its initials on small tiles.
Turned-off plugins are dimmed; uninstalled ones get a red outline (click to search the installer).

**Bars.** As many as you like, each with its own name, columns, tile size, title bar,
background, position and lock. Drag a tile onto another to move it, also between bars
(unlocked bars only). Right-click an empty spot on a bar: lock, hide, settings.

The first run makes a "Plugins" bar with every installed NoFocusLab plugin, the installer and the hub settings.

Commands: `/hub` (settings), `/hub show`, `/hub hide`, `/hub lock`, `/hub unlock` (all bars).

## Things to know
- Dalamud lets one plugin open another's windows but not close them, so clicking an open
  plugin's tile again doesn't close it. Give the tile the plugin's own command if that toggles.
- Shift+click toggling fails (Dalamud says so in chat) for a plugin that's in more than one collection.
- Dalamud hides plugin windows, these bars included, in cutscenes and gpose unless that's turned off in
  Dalamud settings. Cooldown Grid's floating "Plugins" button still works in cutscenes.
