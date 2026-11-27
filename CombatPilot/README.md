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

## Safety
- Something usable but nothing pressed for 2 GCDs (or a planner error): **safe mode**, a fixed
  priority from the job file. Still stalled after 2 more GCDs: **pauses** and says so in chat.
- Kill switch: hotkey or `/pilot pause`.

## Starting a fight
Once you're in combat it always fights. Before that, the **Engage** setting decides (Settings tab,
the small panel's Engage button, or `/pilot engage wait|fighting|any`):
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

## Jobs
- **Warrior** (and Marauder): patch 7.55 data from the official job guide.

## Not done yet / unverified
- Unverified in game: tooltip potency parsing, RSR's `/rotation off|auto` commands, the oGCD-use
  detection used for reports, the 0.65 s weave timing.
- [Estimate] values: guaranteed crit+DH worth 1.65x an average hit, party buff multipliers, the
  leftover-value numbers in the job file (gauge point, procs, buff time).
- No targeting (uses your current target), no prediction of party buffs or downtime yet, no
  time-to-kill (it may start a burst on a dying pack).
