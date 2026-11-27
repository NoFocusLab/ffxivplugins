# Settings Sync

Keeps your plugin settings the same on every PC. Your `pluginConfigs` folder (every plugin, minus an
exclude list) is backed up to a **private GitHub repository you own** and pulled on your other PCs.

- **Automatic:** pulls when the game starts (before your other plugins load, so they start with the
  newest settings) and pushes when you log out or close the game. Optional push every N minutes while playing.
- **Manual:** `Sync now`, `Push only`, `Pull only` buttons, or `/ssync now | push | pull`. Turn
  *Automatic sync* off when you have two PCs open at once for testing.
- **Conflicts:** if both PCs changed the same file since they last synced, it asks which copy to keep
  (all, or ticked files). Nothing is deleted automatically on either side.
- **History:** the last 5 snapshots, or the last 5 pushes from one PC (*Pushed from* list, e.g. to put your
  desktop's settings on your laptop), each with *Restore everything* or *Restore one plugin*. A restore is
  a new snapshot, so it can be undone too.
- Every push and pull is stamped with the time and this PC's name.

Pulled files are written immediately at game start. Pulled while the game is running, they wait and are
applied at the next start (running plugins would overwrite them otherwise).

## Device types (handhelds)

A handheld like the Legion Go wants some settings of its own (HUD layouts, bars, overlays, window
sizes, keybinds) while sharing everything else. On that PC, Settings tab > *Device type*: enter a name
such as `Handheld`, then tick the plugins whose settings should be kept separate. Those sync only
between PCs with the same device type (stored on GitHub under `devices/<type>/pluginConfigs/`); the
first time, each starts from your normal PCs' copy. Normal PCs leave *Device type* empty and are
unaffected. A second PC of the same type picks up the same list automatically.

## Dalamud setup

A second group, with its own *Sync now* button and *automatic* switch (on by default), keeps these
the same across PCs:

- **Custom plugin repos** (and whether each is enabled), **favourite / hidden / pinned** plugins,
  the **Dalamud theme**, **server info bar** order and hidden entries, **auto-update** preferences.
  Dalamud only reads these at start and rewrites them on exit, so pulled changes are applied by a
  small hidden helper right after the game closes (your current dalamudConfig.json is backed up
  first; the last 5 backups are in `pluginConfigs/SettingsSync/backups`). They show on the next start.
- **Plugin on/off:** switched live with Dalamud's `/xlenableplugin` / `/xldisableplugin`. Same rules as
  settings files: whichever PC changed it wins; both changed = you're asked.
- **Plugins missing here:** a plugin can't install other plugins, so they're listed with an
  *Install* button (opens the installer searched to it) and *Ignore*. On a new PC the repos arrive
  after the first restart, then the Install buttons find everything.

Everything else in Dalamud's settings (UI scale, fonts, logs, dev options) stays per PC.

## Logs (troubleshoot another PC remotely)

The **Logs** tab sends Dalamud logs to the settings repository's `logs` branch, so you can look at a
problem from another PC or your phone (github.com > your repository > branch `logs` > `logs/<PC>/`).
Each report is a folder of plain-text files: `README.md` (summary, first crash line, your note),
`crash.log` (Dalamud's crash report: error, call stack, loaded plugins), `dalamud.log` (the session up to
the crash, at most ~2 MB, with giant lines shortened) and `troubleshooting.json` (plugin list and versions).

- **Automatic:** a crashed game can't send anything, so new crash reports (last 7 days) go up the next
  time the game starts. Switch on the Logs tab (on by default).
- **Send logs now:** the current session's log and plugin list, with an optional note.
- Crash dumps (`.dmp`, ~150 MB) are never sent. Reports older than 30 days are removed.
- Logs contain character names and your Windows user folder: keep the repository private.

## Off-screen windows

Window positions saved in plugin settings come along with the sync, so a position from a PC with a
bigger screen can land off this one. Settings Sync checks every open plugin window (any plugin,
Dalamud's too) and moves one whose title bar is off the screen to the middle, so you can drag it where
you want. Settings tab > Screen: **Check windows now**, and **Check automatically, every N seconds**
(on, 15 s by default; off = button only). It also checks once shortly after the game starts. A window
whose plugin keeps putting it back off screen is left alone after 3 tries and named there.

Commands: `/ssync` opens the window.

## One-time setup (about 10 minutes)

1. **Create a private repository** on GitHub, e.g. `ffxiv-settings-sync`, with *Add README* on.
2. **Create a GitHub App** (github.com > Settings > Developer settings > GitHub Apps > New GitHub App):
   - Name: anything unique. Homepage URL: your repository's URL. Callback URL: blank.
   - **Untick** *Expire user authorization tokens* (renewing them needs a secret a plugin can't keep).
   - **Tick** *Enable Device Flow*.
   - Webhook: untick *Active*.
   - Repository permissions: **Contents: Read and write** (nothing else).
   - Where can it be installed: *Only on this account*. Create it.
   - Copy the **Client ID** (starts with `Iv`). Don't create a client secret or private key.
3. **Install the app** (app page > Install App) on **only** the settings repository.
4. In game, `/ssync` > **Settings**: enter the repository (`owner/name`) and the Client ID, then
   **Sign in with GitHub**: open the link, log in, enter the code shown.

## A new PC

A PC that has never synced is **locked for uploads** until you confirm its first sync, so a half-set-up
PC can't overwrite the others: it pulls, asks about anything that differs (pick GitHub's copy), and only
sends files you explicitly chose to keep from it. When everything looks right after a restart, press
*This PC is set up* on the Sync tab. The very first PC (empty repository) isn't locked.

### Setting it up

Install Settings Sync from the custom repo, enter the repository and Client ID, sign in with your
GitHub account. Both values can be looked up any time on github.com (repository list; Developer settings >
GitHub Apps), so there is nothing to remember. The first sync on a PC that already has settings will
list the files that differ and ask which copy to keep.

## What's never synced

Settings Sync's own settings and sign-in (`SettingsSync.json`, `SettingsSync/`), files over the size
limit (5 MB by default), and the exclude list (editable on the Settings tab). Defaults skip backups
(`*.bak`, `*.v10`), logs, caches and recordings: `vnavmesh/meshcache`, `CombatRecorder/logs`,
`MitPlanner/trashlog`, `AutoHook/replays`, plugin `dumps` folders, `InventoryTools/history.csv`.

## Security

The sign-in is stored encrypted with Windows (DPAPI) for your Windows user, and only reaches the one
repository the GitHub App is installed on. Revoke it any time: github.com > Settings > Applications >
Authorized GitHub Apps.

On GitHub the files live under `pluginConfigs/`; `.sync/last.json` records which PC pushed last.
