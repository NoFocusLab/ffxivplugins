# Settings Sync

Keeps your plugin settings the same on every PC. Your `pluginConfigs` folder (every plugin, minus an
exclude list) is backed up to a **private GitHub repository you own** and pulled on your other PCs.

- **Automatic:** pulls when the game starts (before your other plugins load, so they start with the
  newest settings) and pushes when you log out or close the game. Optional push every N minutes while playing.
- **Manual:** `Sync now`, `Push only`, `Pull only` buttons, or `/ssync now | push | pull`. Turn
  *Automatic sync* off when you have two PCs open at once for testing.
- **Conflicts:** if both PCs changed the same file since they last synced, it asks which copy to keep
  (all, or ticked files). Nothing is deleted automatically on either side.
- **History:** the last 5 snapshots, each with *Restore everything* or *Restore one plugin*. A restore is
  a new snapshot, so it can be undone too.
- Every push and pull is stamped with the time and this PC's name.

Pulled files are written immediately at game start. Pulled while the game is running, they wait and are
applied at the next start (running plugins would overwrite them otherwise).

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
