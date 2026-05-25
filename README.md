# MaxPractice

A server-side practice mod for [Puck](https://store.steampowered.com/app/2994020/). Adds chat commands for save practice, stickhandling drills, puck spawning, traffic dummies, AI goalies, and more — all server-authored so clients only need the standard public mod.

**Steam Workshop:** https://steamcommunity.com/sharedfiles/filedetails/?id=3646785422
**Build target:** Puck b897
**Type:** Server-sided plugin (clients connecting to a modded server get it automatically via mod sync)

---

## Features

Most commands run during the **Warmup** phase. `/votegoalies` is the exception — works in any phase when enabled.

### Save / tip practice

| Command | Description |
|---|---|
| `/saveprac` | 2-minute auto save practice. Shots spawn at varying angles, speeds, and targets (bar-down, top corners, 5-hole, post-in, etc.). Goalies only. |
| `/tipprac` | Tip practice — pucks fly through your crease area to redirect with your stick. |
| `/dummy` | Spawn an AI goalie for your team. |
| `/dummyred` / `/dummyblue` | Spawn an AI goalie for the specified team. |
| `/votegoalies` (`/vg`) | Majority vote to toggle auto-managed AI goalies that fill empty goalie slots through the whole game. Requires `EnableGoalieVoting` in config. |

### Puck spawning

| Command | Description |
|---|---|
| `/spawnpuck` (`/s`) | Spawn a puck above your stick. |
| `/backpass` (`/bp`) | Spawn a puck behind you that passes onto your stick. |
| `/pass` | Set a pass position with your blade. Run again to spawn a pass from that point. `fast` / `slow` / `lob` modes. |
| `/unpass` | Clear the saved pass position. |
| `/pop` | Pop the last puck you touched upward. |
| `/cones` | 5 frozen pucks in a line for stickhandling. |
| `/minefield` | 10 frozen pucks scattered around you. |
| `/clearcones` / `/clearminefield` | Remove your handle pucks. |

### Stick-tap triggers

Tap your stick on the ice 3 times to trigger an action — useful when you can't reach the keyboard.

| Command | Description |
|---|---|
| `/tappass` | Tap 3x → pass from your saved pass position. |
| `/tapspawn` | Tap 3x → spawn a puck above your stick. |
| `/tapyoyo` | Tap 3x → yoyo-return the last puck you shot. |
| `/tapbackpass` | Tap 3x → backpass to yourself. |

### Traffic / AI

| Command | Description |
|---|---|
| `/recordtraffic` | Start recording your movement. |
| `/stoprecord` | Save the recording. |
| `/traffic` | Spawn an AI traffic skater. Plays back your recording if one exists. |
| `/cleartraffic` | Remove all your traffic. `/cleartraffic1`, `/cleartraffic2`, … remove a specific one. |

### Yoyo / utilities

| Command | Description |
|---|---|
| `/yoyo` | After shooting, yank your stick back to magnetically return the puck. |
| `/infinitestamina` (`/is`) | Toggle infinite stamina for yourself. |
| `/clearpucks` (`/c`) | Clear all loose pucks but keep one near each player. |
| `/clearall` | Nuke everything — pucks, cones, traffic, AI goalies. |
| `/practice` | Print the full command list in chat. |

---

## AI Goalie

`/dummy`-style commands spawn a goalie that:

- **Tracks the puck** with stick and body (butterfly / standing modes depending on distance)
- **Intercepts long shots** with a held stick instead of always sweeping
- **Sweeps when dangerous** — only flails the stick when an opposing player is within range (or always in warmup, where sweeping is the desired save-practice behavior)
- **Attempts outlet passes** to a teammate when the puck is under control and safe
- **Turns its head to watch pucks behind the net** (body keeps facing the rink), cheats slightly toward the puck's side, and points the stick that way too
- **Reacts to goals** — sad reaction for the scored-on goalie (look down/up, butterfly drop, 40% chance to dramatically flop), excited celebration for the scoring goalie (50/50 between stick-wave-and-jump or spin-in-place-and-jump)
- **Longer win/loss reactions** at GameOver based on the final score (10s instead of 4s)
- **Idle fidgets** when there's nothing to do (6 different behaviors that cycle)
- **Intermission celebration behaviors** (7 different — skate around, spin, fall over, dash spam, victory wiggles, stick windmill, butterfly spam)

### Vote-driven AI goalies

When the server has `EnableGoalieVoting = true`, any player can run `/votegoalies` (or `/vg`) to start a 45-second vote. Simple majority of present non-bot players passes. On pass, AI goalies automatically fill any empty goalie slot for the rest of the session — including respawning when a human goalie disconnects or switches positions. Run `/vg` again to vote them off.

Solo player on the server? The initiator's implicit yes already meets `(1/2)+1 = 1` so the vote passes instantly.

---

## Configuration

A config file is auto-generated at `<puck-install>/config/maxpractice.json` on first run.

```jsonc
{
  "ConfigVersion": 6,

  // Limits
  "ConesPerPlayer": 1,
  "MinefieldPerPlayer": 1,
  "TrafficPerPlayer": 1,
  "SavePracDurationSeconds": 120,
  "MaxPucksBeforeCleanup": 30,

  // Goalie AI
  "GoalieAIPersistDuringGame": false,  // always-on AI through every phase
  "EnableGoalieVoting": false,         // allow /votegoalies (/vg)

  // Practice-server quality of life
  "PauseWarmupTimer": false,           // freeze warmup countdown
  "DisableVoting": false,              // block /vs and /vw (and /vg)

  // Feature toggles (all default true)
  "EnableSpawnPuck": true,
  "EnableBackpass": true,
  "EnablePass": true,
  "EnableYoyo": true,
  "EnablePop": true,
  "EnableSavePrac": true,
  "EnableTipPrac": true,
  "EnableCones": true,
  "EnableMinefield": true,
  "EnableTraffic": true,
  "EnableDummy": true,
  "EnableInfiniteStamina": true,
  "EnableTapCommands": true
}
```

When the config version bumps, the existing file is backed up with a timestamp suffix and a fresh defaults file is written. Manual config changes are not destroyed silently.

---

## Building

```
dotnet build MaxPractice.csproj
```

The csproj auto-references every DLL in `<puck-install>/Puck_Data/Managed`, so the build is always against your installed Puck version. After a successful build, the DLL is copied to `<puck-install>/Plugins/MaxPractice/MaxPractice.dll` automatically by the `CopyToPuckPlugins` AfterBuild target.

### Target

- **Framework:** .NET Framework 4.8
- **Plugin interface:** `IPuckPlugin` (b323+, unchanged in b897)

Plain `libs/` only contains a few non-game packages (SocketIO, Unity addons). Do **not** put game DLLs there — they'll shadow the live `Managed/` references and the mod will silently build against stale B202 APIs.

---

## Acknowledgements

The goalie AI was adapted from the [ToastersRinkSuite](https://github.com/) reference implementation. Behavior structure (butterfly/standing decision tree, idle fidgets, intermission behaviors, dash overshoot braking, look-RPC replication via `NetworkingUtils.CompressFloatToShort`) is ported 1:1 with b323+/b897 API adaptations and MaxPractice integration.

## Support

If you find this useful: https://buymeacoffee.com/amikiir
