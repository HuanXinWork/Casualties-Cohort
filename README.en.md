# Casualties: Cohort for Casualties: Unknown

> Online multiplayer co-op mod, a fork of [Casualties: Together](https://github.com/creaturefeaturelarry/casualties-together)
> License: LGPL-2.1, same as upstream

中文说明：见 [README.md](README.md)

## Overview

Casualties: Cohort is a fork of Casualties: Together. It keeps the upstream plugin GUID and assembly name (`CasualtiesMP`), so existing companion mods keep working without changes.

What this fork changes:

- Full modularization, every file under 800 lines, Harmony warnings cleared
- `Together.Api` facade plus `Together.ModHooks` static entry points, so companion mods do not need to touch internals
- Aligned to the v5.5.3 wire protocol, with single-player passthrough guards
- Dual-mode Debug/Release logging with structured comms diagnostics
- Fixed upstream defects: UseAck, canned food, firearms, prosthetics, tick nests, stimulants, Unicode, Russian locale, drag finalizer

Network design limits are the same as upstream: no client prediction, no network ownership, no deterministic RNG, no anticheat. This mod only adds a simple co-op experience.

## Status

Refactoring is still in progress and some modules are not fully verified yet. Current builds are usable, but the diff against upstream is still being narrowed down. Please report issues on the tracker.

## Install Guide

### Requirements

- Game: [Casualties: Unknown](https://store.steampowered.com/app/4576510)
- [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)

### Steps

1. Go to the game root directory (Steam: `Manage > Browse local files`)
2. Unzip `BepInEx*.zip` into the game root directory
3. Unzip the release, open the `mod` folder and move the contents into the game root directory
4. Launch the game through Steam
5. Multiplayer related buttons should appear in the main menu

To go back to singleplayer, press the `Deactivate MP Mod` button in `Settings > General`.

Linux / Proton: add `WINEDLLOVERRIDES="winhttp=n,b" %command%` to the launch options.

If Steam lobbies do not work, launch the game from the Steam Library instead of a shortcut.

### Warning

- If installation doesn't work, try again
- A good internet connection is recommended for the best experience
- If you encounter glitches, crashes or other issues, blame this mod, not the original developer of the game

## Features

- Steam lobbies
- Real voice chat with in-game effects
- Middle mouse button to point at a location
- Right click to interact and heal other players
- Stay close to others to share body temperature
- Spectator mode when dead
- Sleeping is disabled
- Hunger and thirst are slowed down
- Carrying incapacitated players
- Any dead players respawn at next layer
- Unchipped option is individual
- Can run as a dedicated server (not truly headless)

All rules can be changed only by the host with the `rule <name> <value>` command.

## Full feature list

- Sleeping disabled (`EnableSleep`)
- Time scale buttons disabled (don't try to re-enable this)
- Player friendly fire via the `PVP` rule
- Middle mouse click highlights a location at your cursor for other players
- Stand close to other players to share body temperature
- Text and voice chat depend on your character's ability to speak; the same distortions apply to your messages
- MP3 player becomes a boombox instead of replacing background music
- Right click a player to open an interaction context menu
  - Inspect the health panel and perform any actions
  - Inspect their inventory and take their items
  - Use any usable item on them (feeding)
- When a player dies, they are put into spectator mode
  - All dead players respawn after living players reach the next level
  - If the `Permadeath` rule is true, respawns are disabled
  - Players can be revived at traders
- To continue to the next layer, half of all players must reach the end
  - Stragglers get killed if they take too long
  - Only the Host gets prompted to continue the run
  - DrillPod works the same, all players must be in the pod to continue
  - Tweaked by the `LayerFinishConditionPlayerPercent` rule
- Player health regen and decay are tweaked
  - Death is much faster if there are no comrades nearby (`AdditionalHealthDecay`)
  - Health regeneration is boosted (`AdditionalHealthRegen`)
- You can eat your friends after they die
  - The `CanAmputateHealthyPlayers` rule allows stealing limbs from living players
- Trader first impression reputation is calculated from the prettiest of the bunch

## Console

The Host must enter:

```
rule sv_cheats 1
```

To let a client into server console mode they need `admin` privilege (`adminpriv <player>` or `adminpriv-password`).

## Building

```
python scripts/prepare_workspace.py
```

The script copies the game and installs BepInEx. Visual Studio 2022 is recommended. Open `CasualtiesCohort.sln`, build, and hit F5. Multiple game instances have to be launched manually or via scripts.

## Credits

Credits are listed in the in-game "About" menu.

- [Krokosha666](https://github.com/Krokosha666) — original developer (retired)
- [Creature](https://github.com/creaturefeaturelarry) — upstream maintainer
- [Dune](https://github.com/Jyan-sei) — sync, optimizations
- [VtLoader](https://github.com/vtloader) — bug fixes, locale
- Gary the Cat, Dannad, Meow, Jimmyking, Kidihh, Dawn, mayzmae, ItsVoidSK — contributions
- And a long list of playtesters and supporters from the upstream project

## License

LGPL-2.1, see [LICENSE](LICENSE). This fork is distributed under the same terms as upstream.