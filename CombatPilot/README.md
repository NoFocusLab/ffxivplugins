# Combat Pilot

Your own damage rotation. Instead of a fixed priority list, Combat Pilot looks a few GCDs ahead from
what's happening right now, scores the possible sequences, and presses the first move of the best one.
It re-plans every GCD, so a missed GCD, walking out for a mechanic, or being out of range simply becomes
the new starting point.

`/pilot` opens the window. `/pilot live | listen | off` sets the mode for your current job,
`/pilot pause | resume` (or the kill-switch key, Ctrl+Shift+End by default) stops and restarts it,
`/pilot report` opens the reports.

## Modes (per job)
- **Live:** Combat Pilot presses the damage buttons. RotationSolver is turned off for that job
  (`/rotation off`) and turned back on (`/rotation auto`) when you switch to a job that isn't Live.
- **Listen:** RotationSolver flies; Combat Pilot plans alongside and logs where it would have differed.
- **Off.**

Mitigation stays with **Mit Planner**: Combat Pilot leaves a weave slot when Mit Planner has a planned
press due (`MitPlanner.PressDueIn`), and never presses over the game's animation lock.

## How it decides
- **Job files** (`Jobs/*.json`) describe each job as data: actions, combos, gauge, buffs, what each
  action grants or uses up. Action and status names are matched to the game's data at load, so files
  carry no ids. Potencies are read from the game's tooltips when they parse cleanly; the Job data tab
  shows where each number came from and flags any tooltip that disagrees with the file (a patch changed it).
- **Planner:** beam search over the next GCDs (6 by default) and the weave slots between them. A
  sequence scores the damage it does (buffs, guaranteed crit/direct hits, AoE targets and falloff)
  plus what it leaves in hand (gauge, buff time, ready procs, combo progress), minus cooldown time
  wasted sitting at full charges.
- **Range:** out of melee range, ranged options (Tomahawk, Primal Rend, Onslaught) compete with
  holding the GCD; Tomahawk and gauge spenders don't break the combo (7.0+), so this works itself out.

## Party buffs
Combat Pilot watches the party's 2-minute buffs (Battle Litany, Brotherhood, Arcane Circle, Embolden,
Technical Finish, Searing Light, Starry Muse, Radiant Finale, Battle Voice, Divination, Chain
Stratagem, Dokumori). Each one seen is expected again one cooldown later; ones not seen yet are
expected in the opener (~7 s in) and dropped if they haven't shown up 30 s into the fight. The
planner scores damage inside those windows higher, so bursts line up when the buffs are close
(within its look-ahead). [Estimate] durations, cooldowns and average multipliers. Solo/dummy: none.

**Per fight** (any fight Mit Planner runs a timeline for, e.g. the current tier: M9S-M12S, Hell on
Rails (Extreme), The Unmaking (Extreme)):
- **Learned schedule:** every pull records when your party's buffs went out, by Mit Planner's synced
  timeline clock (so phase pushes and branches line up). Once two pulls agree within 8 s, that's the
  expected time (last 8 pulls kept, `bursts.json`).
- **Downtime rule:** a buff expected while the boss is untargetable is expected when it comes back
  ([Assumption] parties hold 2-minute buffs through downtime).
- The Pilot tab shows each expected buff and its basis (learned / cycle / opener, after downtime).

## AoE vs single target
Worked out per decision, for every job, from the enemies each AoE would actually hit:
- **Area from game data:** shape (circle around you or the target, cone, line) and size come from the
  action's own data; falloff ("50% less for all remaining enemies") from its tooltip. A job file can
  override any of it. Hitboxes count.
- **Which enemies count:** fighting enemies (and your target). Enemies that can't be damaged
  ([Assumption] statuses named Invincibility/Invulnerability) are left out. An AoE that would also hit
  an enemy that isn't fighting yet is not used (setting, on by default: no accidental pulls).
- **Time to live:** each enemy's HP drop rate gives how long it will last; a hit only counts on
  enemies still alive when it lands (no AoE into a pack that dies next GCD).
- The planner then compares the sequences (combos, gauge, buffs included) and picks what does more.
- **AoE toggle (bar):** auto / **off** (no AoE that hits anything but your target: focus mechanics,
  things that shouldn't be hit) / force (treat AoEs as hitting at least 3).
- The Pilot tab shows the enemies fighting (with time to live) and how many each AoE would hit.

**Warrior break-even points** (7.55 potencies, per target, before time to live):

| Single target | AoE | AoE wins at |
|---|---|---|
| Heavy Swing > Maim > Storm's Path: 1080 / 3 GCDs = 360 per GCD | Overpower > Mythril Tempest: 250 / 2 GCDs = 125 per GCD per target | 3+ |
| Fell Cleave 580 | Decimate 180 per target | 4+ |
| Inner Chaos 700 | Chaotic Cyclone 200 per target (both crit+DH) | 4+ |
| Upheaval 420 | Orogeny 150 per target | 3+ |
| Primal Rend / Ruination / Wrath | already AoE (50% less on others) | always |

Both combos give the same gauge per GCD (10), and Mythril Tempest keeps Surging Tempest up too.

## Your level
Everything follows the level you're at (level sync included): actions you don't have yet aren't
considered, trait upgrades swap in (Berserk -> Inner Release), effects a trait adds only count from
that level (job file `minLevel`: e.g. Primal Rend Ready from 90, Burgeoning Fury from 96, Nascent
Chaos from 72), potencies come from the tooltip as the game shows it at your level
([Assumption] Dalamud's string evaluator resolves the tooltips' level checks; the Job data tab marks
any action still on the job file's level-100 number), crit/direct-hit math uses your level's
constants, and openers written for level 100 aren't used below it (the planner opens instead).

## Dashes and danger zones
Actions that move you to the target (Warrior: Onslaught, Primal Rend) are checked with BossMod
(`BossMod.Hints.IsDashSafe`, the same data Rescue Helper uses) before they're used: if the landing
spot or the path is in a danger zone BossMod knows (boss AoEs, puddles, the arena edge), the planner
won't use it, and an opener step that would is skipped. Clicking it on the bar still uses it (your
call). Without BossMod loaded, dashes are unchecked (the Pilot tab says so).

## Your stats
GCD length (and so opener step timing) comes from your actual recast: skill/spell speed and haste
included. The value of a guaranteed critical direct hit is worked out from your Critical Hit and
Direct Hit stats (as the game shows them, so level sync counts), using the Allagan Studies formulas;
the Pilot tab shows your rates and the result. A fixed value can be used instead (Settings).

## Boss downtime (from Mit Planner)
When Mit Planner is running a fight timeline (bundled cactbot or one it learned), Combat Pilot asks it
for the upcoming untargetable windows (`MitPlanner.Downtime`). Damage planned inside a window counts
as zero, so it won't start a burst just before the boss leaves, and it spends or holds procs around
it. As Mit Planner's timelines get better (learned timelines now record downtime too), this improves
with no change here. The Pilot tab shows the timeline clock and the downtime it sees.

## Safety
- Something usable but nothing pressed for 2 GCDs (or a planner error): **safe mode**, a fixed
  priority from the job file. Still stalled after 2 more GCDs: **pauses** and says so in chat.
- Kill switch: hotkey or `/pilot pause`.

## Job bar
A bar for your current job (`/pilot bar` shows/hides it):
- **Toggles:** mode (Live/Listen/Off), pause, Engage (below), Burst auto/HOLD (holds the job file's
  burst list, Warrior: Inner Release and Infuriate), AoE auto/off/force, travel reserve (Warrior: keep
  1 Onslaught charge for getting back in), potions on/off.
- **Action buttons** with cooldowns and charges. Clicking one **queues** it: in Live it goes into the
  next slot it fits (next weave for an oGCD, next GCD otherwise), ahead of the plan, and the plan
  continues around it; it waits up to 6 s (Settings). Not Live: used right away. Other plugins can
  queue an action too (IPC `CombatPilot.Queue(actionId)`).
- **Potions** (off by default, per job): the highest potion for your main stat in your bags, or a
  specific one (Settings). [Estimate] valued at +6% damage for 30 s; the planner places it, lining
  it up with party buffs it can see coming.

## Openers (countdown)
Each job file carries its openers (Warrior: The Balance's All-Around opener, 11 steps). With the bar's
**Opener: countdown** toggle on (default) and the job on Live, starting an in-game countdown with
an enemy targeted fires the first action at the opener's prepull time (Warrior: Tomahawk at -0.7 s)
and then plays the steps: each GCD with its oGCDs. It overrides the travel reserve (the opener uses
all three Onslaughts) and skips "Potion" when potions are off. It hands back to the planner when
done, or if it goes off script (a step can't be pressed for 2 GCDs, or different GCDs went out
twice). If the countdown is **cancelled**, nothing is pulled: the opener doesn't fire, and Combat
Pilot won't start a fight on its own (whatever the Engage setting) until combat starts some other way
or a new countdown begins. Right-click the toggle to run the opener now (from the next GCD). The toggle's tooltip
lists the steps.

## Starting a fight
Once you're in combat it always fights. Before that, the **Engage** toggle decides (job bar, or
`/pilot engage wait|fighting|any`):
- **Wait** until you're in combat.
- **Fighting** (default): attack your target only if it's already fighting someone.
- **Any**: attack any attackable target you select, starting the fight (pulls, striking dummies).
"Attackable" is the game's own check, so striking dummies count. The status line says why it's
idle (no target, target can't be attacked, waiting for combat...). Each decision and press is also
written to the Dalamud log (`/xllog`, Debug level) as `[Pilot]` lines.

## Reports
Every fight is logged (Live and Listen): each decision, what was wanted, what was pressed, and the
estimated potency difference. One file per fight in the plugin's config folder (`fights/`), the last
100 kept; the Reports tab lists them with the biggest differences first.

## Reviewing logs
`tools/iinact_report.py <Network_*.log> [--split HH:MM]` reviews IINACT/ACT logs per pull: GCD
rolling, DPS, Inner Release stacks expired, Primal Rend to Ruination delay, Infuriate/Onslaught
time at full charges. In Listen reports, an oGCD left unpressed counts once per stretch.

## Jobs
- **Warrior** (and Marauder): patch 7.55 data from the official job guide.

## Not done yet / unverified
- Unverified in game: tooltip potency parsing, RSR's `/rotation off|auto` commands, the oGCD-use
  detection used for reports, the 0.65 s weave timing.
- [Estimate] values: party buff multipliers, the
  leftover-value numbers in the job file (gauge point, procs, buff time).
- No targeting (uses your current target), no prediction of downtime yet, no
  time-to-kill (it may start a burst on a dying pack).
