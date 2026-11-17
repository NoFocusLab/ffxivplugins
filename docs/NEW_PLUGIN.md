# Adding a new plugin

1. Create a folder `<PluginName>/` at the repo root with `<PluginName>.csproj`
   (copy TankStanceManager's and edit Name, Punchline, Description, Tags, Version).
2. Add it to `ffxiv-plugins.slnx`.
3. Add a row to the table in the root README.
4. Push to main and confirm the GitHub Actions build passes.
5. The first green build on main publishes it automatically (see root README).

## Reusable prompt for Claude
Paste and fill in:

> New Dalamud plugin for my plugins repo.
> Name: ___
> What it should do: ___
> When it should be active (combat / duties / always): ___
> Settings I want to control in the UI: ___
> Does it press actions for me, or only display info? ___
> Follow the repo conventions in docs/NEW_PLUGIN.md, and get the
> GitHub Actions build passing (a push to main publishes the release).

## Icon
Every plugin gets an icon (a white symbol on a rounded tile, coloured by group):
1. Add an entry for the plugin to `GLYPHS` in `tools/make_icons.py` (pick a group and draw a symbol in SVG),
   then run `python tools/make_icons.py`. It writes `<PluginName>/images/icon.png` (+ `icon.svg`).
2. Add the icon to Plugin Hub: one `<EmbeddedResource>` line in `PluginHub/PluginHub.csproj`
   (`LogicalName="PluginHub.Icons.<PluginName>.png"`).
The release workflow points the plugin's `IconUrl` at the PNG automatically (in repo.json and in the zip's manifest).
Stand-in icons for other people's plugins go in `HUB_ICONS` (named after the plugin's InternalName) and land in
`PluginHub/Icons/`, which the hub embeds automatically.
