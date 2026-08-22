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
| `/pass` | Set a pass position with your blade — a puck shooter appears there and feeds you. Run again to fire one. `fast` / `slow` / `lob` modes. |
| `/unpass` | Clear the saved pass position and remove the shooter. |
| `/pop` | Pop the last puck you touched upward. |
| `/cones` | 5 frozen cones in a line for stickhandling. |
| `/minefield` | 10 frozen cones scattered around you. |
| `/clearcones` / `/clearminefield` | Remove your cones. |
| `/mininet` (`/net`) | Drop a mini net ahead of you for shooting practice. One per player — it eats any puck it catches. |
| `/clearmininet` (`/clearnet`) | Remove your mini net. |

### Cone asset

`/cones` and `/minefield` both spawn actual cones instead of pucks. The cone is
generated procedurally at runtime (`src/ConeAsset.cs`) — a stubby 36 cm training
cone on a 32 cm round base with a white band, in two submeshes so the band takes
its own material. No AssetBundle, so the mod stays a single code-only DLL. Resize
it by editing the dimension constants at the top of `ConeAsset.cs`; the colliders
are generated from the same numbers, so the two can't drift apart.

Cones can't be placed within `MinConeSeparation` (1.25 m) of one another — checked
against every player's cones, not just yours. `/minefield` re-rolls a blocked spot
and `/cones` pushes the slot further down the line, up to `ConeSpotAttempts` tries
each; a cone with nowhere to go is skipped and the chat message says how many. On
open ice both commands place a full set every time, but the 3–6 m scatter ring is
only so big — stacking several minefields on the same spot will start reporting
skips, which means the ice really is full.

Cones have their own collision, not the puck's — the puck's disc collider is
switched off and two convex `MeshCollider`s take over: one for the base disc, one
for the tapered body. Two hulls rather than one over the whole cone, because the
base is wider than the body sitting on it and hulling them together fills that step
with an invisible slope. The base is deliberately taller than a puck is thick, so a
puck sliding in hits it instead of slipping under the body.

**Cones stop pucks, not players or sticks.** The cone's colliders are paired off
against every player body and stick with `Physics.IgnoreCollision`, refreshed every
2 s — sticks get destroyed and rebuilt whenever someone changes position, and a
stick catching on a cone would wreck the drill the cones are there for.

Two layers are in play, and mixing them up is the trap:

| | Layer | Why |
|---|---|---|
| Cone visual | `Default` | Puck rendering has a see-through silhouette pass filtered on the game's **Puck** layer. A cone on it shows through the boards and the net. |
| Cone colliders | the puck's | That's what makes pucks collide with them. They're on their own child objects, so they can sit on a different layer from the visual. |

Under the hood each cone is still a frozen handle puck. The puck remains the
network anchor with its renderers and colliders disabled, and the cone is parented
to it. Spawning, tracking, limits, and `/clearcones` are unchanged, and removing
the cone restores the puck exactly as it was.

Because neither mesh nor collider assignments are replicated by Netcode, the server
names its prop pucks to clients over a `MaxPractice.Props` named message
(`src/PropNetwork.cs`) and each client builds the visual locally. Physics is
server-authoritative, so colliders are only ever built on the server — a client
never builds a second set to fight the replicated puck position. A client without
MaxPractice loaded sees plain pucks; everything else still works.

Set `ConeVisuals: false` in the config to go back to plain pucks.

### Puck shooter and mini net

`/pass` and `/mininet` spawn props built the same way as the cone — a procedural
mesh on a frozen anchor puck (`src/PropAssets.cs`), announced to clients by
`src/PropNetwork.cs`. Riding a real NetworkObject means position and heading
replicate for free, and destroying the puck cleans the whole thing up.

**Puck shooter.** Setting a pass position stands a passing machine on it, aimed at
you, and it swings round to face each new pass. It's a rollers-on-the-ice design
rather than a tall hopper for a reason: `/pass` spawns its puck at your blade
height, so the muzzle sits at 9 cm and the puck genuinely leaves it.

The shooter has **no colliders at all** — not the mesh, and not the anchor puck
underneath, whose own collider is switched off. It stands exactly where passes
spawn, so anything solid there would knock every pass off course the moment it
appeared. `/unpass` removes it.

**Mini net.** 1.2 m × 0.8 m, dropped 7 m ahead of you, one per player — running
`/mininet` again moves it rather than adding a second. Posts and crossbar are solid
so pucks ring off them, and pucks stay where they land (no trigger).

It first tries to build itself from a scaled copy of the rink's own net, and falls
back to a procedural frame when it can't. **On the stock rink it can't**, and the
reason is worth recording so nobody retries it:

- The frame (`Goal Blue/goal/Goal Frame`) is a **statically batched** renderer. Its
  geometry is baked into a combined mesh at its original world position, so a copy
  keeps drawing over at the real net however you move or scale it. You get a mini
  net with no frame. Nothing a plugin can do fixes that, so `IsStaticBatched` checks
  for it up front and hands over to the procedural net.

Two other traps found the hard way, kept here because both produce results that look
like something else entirely:

- Under `goal` sit four **deliberately inactive** proxy meshes (`Goal Net`,
  `Goal Net Collider`, `Goal Player Collider`, `Goal Trigger`) — editor
  visualisations with no usable material. Anything that switches them on paints four
  magenta slabs straight over the frame.
- The goal's colliders reach far past the net (the player zone and crease), so
  folding them into a bounds measurement gives something like 9.8 × 2.0 × **35.0 m**
  and an alignment shift to match — the net ends up across the rink and hovering.
  Measure from active `MeshRenderer`s only, never colliders, and never the netting's
  `SkinnedMeshRenderer` (its `Cloth` is destroyed, so its bounds are junk).

Like the cones, nets stop pucks but not players or sticks.

### The menu

**F3** opens the practice menu. Esc, F3 again, or a click outside closes it.

It's styled to match Ponce Arena's panel — same centred modal geometry, palette,
section/row/tab construction and game-font enforcement — so a server running both
doesn't feel like two different mods.

Three tabs:

- **Actions** — the default. Every command as a button you can just click, grouped
  by what it's for, with its bound key shown on the tile. Nothing needs to be bound
  and nothing needs typing in chat. The panel stays open on a click, so you can lay
  a whole drill out — cones, then traffic, then a goalie — in one visit.
- **Binds** — the same commands as detail rows: description, bind chips, a **BIND**
  button, and a **RUN** button. A command can hold more than one bind; the `×` on a
  chip removes it.
- **Server** — what this install has enabled, plus the limits and behaviour flags.

Both tabs are built from one catalogue, so anything bindable is also clickable.
Commands the config has switched off are marked `OFF`, dimmed, and their buttons
disabled — a disabled command no-ops server-side, so the menu doesn't pretend
otherwise.

Binds are saved to `config/ModHub/MaxPractice/MaxPractice.Keybinds.json` and fire
without the menu open (never while chat is focused). A command can hold more than
one bind; the `×` on a chip removes it.

While the menu is up, MaxPractice sets the game's `isMouseRequired` UI state so
skating input stops — previously you could steer your player through the menu.

MaxPractice no longer registers with `PonceMods.Shared.ModMenuHub`. On first run
after updating it unregisters itself once, so it doesn't leave a dead **MAX
PRACTICE** button in other Ponce mods' hubs.

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

### Practice sheets

Extra copies of the arena's rink, so a group can run drills without three other people skating through them. Nothing is built at startup — the first player to ask for a sheet builds it, and it comes down again once nobody is standing on it.

| Command | Description |
|---|---|
| `/rink` / `/rinks` | List the rinks, who's on each, and which one you're on. |
| `/rink <n>` | Move to that rink. Rink 1 is the arena's own; anything higher is a clone. |
| `/rink new` | Move to the lowest-numbered empty rink, building it if it isn't up. |
| `/mainrink` | Go back to the arena rink. |

You keep your spot on the sheet when you move — step across at the blue line and you arrive at the blue line. Cones, mini nets, pucks and traffic stay behind on the rink you left; `/saveprac` and `/tipprac` shoot at the net on whichever sheet you're standing on.

#### The minimap follows you

The minimap is a top-down XZ view normalised against the arena rink's box, and a vertical
stack never changes X or Z — so by default four players spread across four sheets all plot
on the same spot, and the map claims everyone is standing on top of you. A horizontal
layout fails the other way, plotting those dots off the map entirely.

Every dot the map draws — players, pucks and sticks — is placed through one method, so
`src/MinimapSheetView.cs` hides the dots that aren't on your sheet and rebases the ones
that are into rink-local coordinates. Your sheet reads exactly like the arena rink does,
and changing sheets changes the map with you.

The full position is rebased rather than just X and Z, because the vanilla code also
scales a dot by its height — left alone, everyone on a sheet 40 m down would be drawn at
the wrong size as well as the wrong place.

#### Lighting on a sheet

Clone geometry splits into two halves, lit differently.

The parts that clone cleanly — glass, nets — keep their baked lighting. Lightmaps are sampled through per-vertex UV2, so a translated clone reads the same texels as the original and looks like the real rink for nothing.

The bulk of the rink is static-batched and has to be redrawn through `Graphics.DrawMesh`, which cannot sample a lightmap at all (the shader variant is chosen per renderer, not per draw). So the sheet gets the arena's **actual light fixtures and reflection probes cloned onto it** at the same offset — the probes reusing the original's baked cubemap, which is what keeps the ice glossy instead of matte — plus a flat ambient floor so the proxy geometry isn't lit by nothing. Baked fixtures emit nothing at runtime but still describe where the arena's light comes from, so they're revived as realtime at reduced intensity.

`RinkSheetFillLightGain` scales the fill rig. This arena turns out to have **no runtime light fixtures at all** — its entire look is baked — so the sheet falls back to a small synthetic rig, and says so in the log.

If the boards come out blotched with black patches, that's the lightmap tile/offset being applied to the batched geometry wrongly. `RinkSheetProxyLightmaps` picks how: `identity` (assume static batching already folded the transform into the mesh UVs), `renderer` (apply each renderer's own on top), or `off` (flat ambient — uniform and dull, but never blotchy).

Sheets also copy the arena's **reverb zone** — the "echo dampener" — to their own centre. A reverb zone is a pure scene component that no game code references, so it doesn't show up when reading the game's audio system at all; the arena's one is centred on the main rink and reaches a stacked sheet's centre but not its ends.

Sheets are a warmup feature. When the phase leaves warmup everyone is pulled back to the arena rink and the clones are torn down.

A sheet's nets run the **same** `WarmupGoalTriggerHandler` the arena's nets do, not a copy of it — same delay before the puck is eaten, same replacement puck, same phase checks — with the replacement dropped at that sheet's centre ice and the puck-count check scoped to that sheet. Only the arena rink reports goals to the AI goalies, since that's where they stand.

An empty sheet comes down on its own after `RinkSheetIdleTimeoutSeconds` (30 s). The clock starts the moment the last player leaves — occupancy is re-checked immediately on a rink change or a disconnect rather than on the next poll — and rebuilding takes about ten milliseconds, so the timeout only has to be long enough to survive a respawn. Anyone still detected on a sheet when it comes down is moved back to the arena rink first and told why.

#### Nothing bleeds between sheets

Sheet *geometry* is scoped to the one you're standing on, but players, sticks and pucks
are ordinary networked objects sitting 40 m above or below you, and two things the
renderer does reach across that gap. Both are handled in `src/SheetBleed.cs`.

**Shadows.** A sheet stacked above yours is lit, and its players and pucks drop shadows
straight down onto your ice — a game of shadow hockey played by nobody. Anything not on
your sheet has its shadow casting switched off, and gets back the setting it had before
rather than a blanket "on": the local body is legitimately shadows-only or off depending
on the camera.

**Puck elevation indicators.** `PuckElevationIndicator` raycasts straight down with an
*infinite* distance, which is correct on one rink — the first thing under a puck is always
that rink's floor. On a stack, a puck one sheet up finds your ice and plants its marker
plane and line there. Off-sheet pucks skip the method entirely, which also saves the
raycast. Your own "show puck elevation" setting still wins; when it's off, nothing here
turns it back on.

#### Custom arenas follow you

If [Dem's Scenery Loader](https://steamcommunity.com/sharedfiles/filedetails/?id=3566470321)
(`SceneryChanger`) is installed, it builds its arena once around the level's own rink —
so stepping onto a sheet used to leave the scenery behind and the sheet stood in nothing.

`src/SceneryFollow.cs` moves that scenery onto whichever sheet you're on. It moves the one
local copy rather than cloning it per sheet, which is possible because the scenery is
decoration: client-side, not networked, and nothing on the server reads it. Each client
only draws its own sheet anyway, so nobody else's view is affected by your copy moving.
The cost is a single transform write per sheet change — no extra draw calls, no extra
memory — and the bundle's own lights and ambient audio travel with it.

The offset is always measured from where the container was first seen, so repeated sheet
changes can't accumulate drift, and the scenery goes back to where SceneryChanger put it
when the sheets come down. The integration is by object name (`SceneryLoaderContent`), so
there's no assembly reference and nothing to break if that mod reorganises what sits under
it. Does nothing when the mod isn't installed; `RinkSheetSceneryFollows: false` turns it off.

#### Why the layout matters

Puck sends every networked position as a 16-bit value scaled by 655, so a coordinate past **±50 m on any axis** wraps around and the object desyncs. The arena rink is ~45 × 91.5 m, which only just fits that box — there is no room beside it for a second sheet. Hence two layouts:

- **`vertical`** (default) — clones are stacked alternately *below and above* the arena rink, 40 m apart. X and Z never change, so every coordinate stays inside the vanilla envelope and the position-sync code is left completely untouched. Costs nothing and works against unmodded clients. The ±50 m ceiling is also the sheet limit: alternating uses the range on both sides, so 40 m spacing fits two clones and 20 m fits four. Each client draws only the sheet it's standing on, so a sheet overhead is never visible from the rink below it.
- **`horizontal`** — clones sit on a grid beside the arena rink, like a real multi-sheet facility. Positions out there overflow the encoding, so the mod re-encodes them relative to a 32 m chunk grid and announces each object's chunk out of band. Sheet origins are snapped to that grid so the arena rink always lands in chunk (0,0) and encodes **byte-identically to vanilla** — an unmodded client still sees the main rink correctly, and only misreads players out on a clone. This is the more capable option and the riskier one: it patches the position-sync hot path.

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

### AI stays off the scoreboard

AI goalies, traffic dummies and passer AIs are real `Player` objects — that's what lets
them skate, hold a position and appear in replays — so the Tab scoreboard listed them
alongside the humans. Six traffic dummies read as six players who just joined.

`src/ScoreboardFilter.cs` blocks the row and takes them back out of the header count, so
`3/12` matches the three rows above it. Set `HideAIFromScoreboard: false` to list them
again, which is worth doing on a server running `/votegoalies` through real games — those
goalies play the whole match and people want to see who's in net.

The existing `PlayerManager.GetPlayers` filter doesn't cover this and never did. It matches
against a set the **server** fills in when it spawns a bot, so on a connected client the set
is empty and the filter does nothing; only the host ever had a clean count. It couldn't be
widened to match on client id either, because `GetPlayerByClientId` is built on top of
`GetPlayers` — filtering there would make the AI unresolvable by id on both sides. And the
rows were never fixed on any machine, host included, because the scoreboard takes the
`Player` straight out of the event payload rather than from `GetPlayers`. So the count and
the rows are both corrected where they're actually drawn.

This one is read per machine rather than from the server, since the scoreboard is drawn
client-side and nothing carries settings to a client today.

---

## Configuration

A config file is auto-generated at `<puck-install>/config/maxpractice.json` on first run.

```jsonc
{
  "ConfigVersion": 9,

  // Practice sheets (extra rinks, built on demand)
  "EnableRinkSheets": true,
  "MaxRinkSheets": 3,                  // total INCLUDING the arena rink
  "RinkSheetLayout": "vertical",       // "vertical" or "horizontal" — see above
  "RinkSheetVerticalSpacing": 40,      // metres between stacked sheets (alternating ±)
  "RinkSheetProxyLightmaps": "identity", // "identity" | "renderer" | "off" — see below
  "RinkSheetSceneryFollows": true,     // move Dem's Scenery Loader's arena onto the
                                       // sheet you're on (no-op without that mod)
  "RinkSheetGridSpacingX": 64,         // horizontal layout only, snapped to 32 m
  "RinkSheetGridSpacingZ": 128,        // horizontal layout only, snapped to 32 m
  "RinkSheetIdleTimeoutSeconds": 30,   // empty sheet teardown (0 = keep until phase end)
  "RinkSheetNetsEatPucks": true,       // clone nets swallow pucks; nothing ever scores
  "RinkSheetFillLightGain": 1.0,       // sheet brightness — raise if dark, lower if washed out

  // Limits
  "ConesPerPlayer": 1,
  "MinefieldPerPlayer": 1,
  "TrafficPerPlayer": 1,
  "SavePracDurationSeconds": 120,
  "MaxPucksBeforeCleanup": 30,

  // Goalie AI
  "GoalieAIPersistDuringGame": false,  // always-on AI through every phase
  "EnableGoalieVoting": false,         // allow /votegoalies (/vg)
  "HideAIFromScoreboard": true,        // keep AI goalies, traffic and passers off the
                                       // Tab scoreboard and out of its player count
                                       // (read per machine, not from the server)

  // Practice-server quality of life
  "PauseWarmupTimer": false,           // freeze warmup countdown
  "DisableVoting": false,              // block /vs and /vw (and /vg)
  "ConeVisuals": true,                 // /cones spawns cone meshes, not pucks
  "EnableMiniNet": true,               // allow /mininet
  "MiniNetsPerPlayer": 1,              // 0 disables
  "MiniNetScale": 0.55,                // fraction of the game net's size

  // Anti-grief
  "ClearCommandCooldownSeconds": 30,   // per-player cooldown shared across all
                                       // /clear* commands (0 disables)

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

`MaxRinkSheets` is clamped to whatever the layout can actually carry, and the clamp is logged. Spacing trades against sheet count, because every sheet has to sit inside the ±50 m box: at the default 40 m you get two clones, at 20 m you get four.

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

The practice-sheet approach — cloning the level's rink templates, redrawing static-batched geometry through `Graphics.DrawMesh` submesh slices, and re-encoding positions against a chunk grid to survive the 16-bit position sync — was worked out first in [Puck-MultiSheet](https://github.com/Dalfan4Puck/Puck-MultiSheet) and [PuckLargeLevel](https://github.com/Jake-Porter/PuckLargeLevel). MaxPractice's implementation is its own, but the problems and their shapes came from reading those. Neither repo carries a licence file, so nothing was copied verbatim.

The goalie AI was adapted from the [ToastersRinkSuite](https://github.com/) reference implementation. Behavior structure (butterfly/standing decision tree, idle fidgets, intermission behaviors, dash overshoot braking, look-RPC replication via `NetworkingUtils.CompressFloatToShort`) is ported 1:1 with b323+/b897 API adaptations and MaxPractice integration.

## Support

If you find this useful: https://buymeacoffee.com/amikiir
