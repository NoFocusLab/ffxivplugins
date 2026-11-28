# Fight timelines for the simulator

One JSON file per fight (`FightTimeline` in Model.cs): duration, downtime windows, out-of-melee
windows, adds, party buff windows; all in seconds from the pull. The dummy needs no file.

Sources, in the order they're planned: Mit Planner's Cactbot timelines (downtime and mechanics),
learned burst windows (Combat Pilot's bursts.json), then parses (FFLogs) for party buffs and movement.
The optimiser tunes a per-fight profile against these and saves it keyed by territory.
