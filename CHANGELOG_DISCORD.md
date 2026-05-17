# 🥅 MaxPractice 1.5.0 — Goalie AI Overhaul

The AI goalie just got a *huge* upgrade. Full rewrite with smarter play, reactions, and a vote system for mid-game use.

## 🤖 Smarter goalie
- **Intercepts long shots** with a held stick instead of flailing at every puck
- **Sweeps only when dangerous** — opposing player nearby, or always during warmup (save-practice mode stays the same)
- **Outlet passes** to a teammate when the puck is safely on the stick
- **Watches pucks behind the net** with their head, cheats slightly toward that side, and points their stick that way
- Keeps respawning automatically when a human goalie disconnects or leaves the position

## 😢🎉 Reactions to every goal
- **Scored on?** Sad look down (or up at the sky, dramatic 30% chance), butterfly drop, 40% chance to fully flop over
- **Your team scored?** Stick raised + waving side to side **OR** spin in place (50/50) — and bouncing the whole time
- **Won the game?** 10-second extended celebration on `GameOver`. Lost it? Extended sad reaction.
- AI **celebrates intermissions** between periods with 7 different goofy behaviors (skate, spin, flop, dash-spam, wiggles, stick windmill, butterfly spam)

## 🗳️ New: `/votegoalies` (`/vg`)
- Toggle mid-game AI goalies on/off via majority vote
- 45-second window, simple majority of present players
- Solo player on the server? Vote passes instantly
- Gated by new `EnableGoalieVoting` config (default **off** — admins opt in)

## 🔧 New config options
- `EnableGoalieVoting` — allow players to `/vg` (default off)
- `GoalieAIPersistDuringGame` — always-on AI through every phase (default off)
- Both are independent: pick always-on, voting, both, or neither

## 🐛 Bug fixes
- **White-gear goalies** — `FlagID`/`MustacheID`/`BeardID` were getting dropped, leaving the AI in default Unity textures
- **PreGame despawn** — vote-spawned goalies disappeared the moment a match started
- **Idle never fired** — fidget animations were getting reset every tick a puck was in the zone
- **Sad-state stick stuck** — stick stayed pointing at wherever the puck was when the goal happened, plus butterfly crouch was being toggled off mid-sad
- **Goal-scored hook fired on clients** instead of server (no sad reaction ever triggered)
- **Vote not passing instantly** when the initiator alone met the threshold
- **Chat messages not visible** on dedicated servers — switched to the string overload (same path OpenWorldPracticeMod uses) so vote-passed / "Puck spawned." actually shows up

## ⚙️ Under the hood
- Complete rewrite from `SimpleGoalieAI` → `GoalieAI` + `GoalieAIManager`
- Replay recorder now correctly captures AI goalie motion in goal replays
- AI goalies don't eat real player slots anymore (was a permanent leak across the server's lifetime)
- AI goalies filtered out of vote counts, server browser counts, puck collision history (so they can't be credited with goals/assists), and `PlayerManager.GetPlayers()` lookups
- TCP server preview count now subtracts AI goalies — your server browser shows the real player count
- Bot join/leave chat messages suppressed

---

**Steam Workshop:** <https://steamcommunity.com/sharedfiles/filedetails/?id=3646785422>
**Source:** <https://github.com/TakoKylo/MaxPractice>

Support: <https://buymeacoffee.com/amikiir> ☕
