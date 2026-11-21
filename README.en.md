# Casualties: Cohort for Casualties: Unknown

> Online multiplayer co-op mod for Casualties: Unknown (`v1.0.2`), a modularized fork of [Casualties: Together](https://github.com/creaturefeaturelarry/casualties-together) (current wire protocol baseline: `v5.5.4`)
> License: LGPL-2.1, same as upstream

中文说明：见 [README.md](README.md)

## Overview

Casualties: Cohort preserves the upstream BepInEx plugin GUID and primary assembly name (`CasualtiesMP.dll`), remaining wire-compatible with Casualties: Together `v5.5.4` lobbies while allowing companion mods (such as `SkinSync` and `CuSaveManager`) to work out of the box without modification.

What this fork provides:

- **Modularized codebase (`<= 800` lines per file)**: Every first-party source file is split by responsibility under 800 lines, with `Harmony003` analyzer warnings cleared to zero.
- **Explicit cross-mod extension surface**: Exposes `Together.Api` (panel input blockers, save/world hooks, custom networking) and `Together.ModHooks` static events so external mods do not need brittle private reflection.
- **Aligned with upstream `v5.5.4` protocol & features**: Includes live host migration (`HostTransfer`), collapsible rule categories with search & reset, dedicated main-menu language tab, 5-locale (`EN` / `es-ES` / `ru-RU` / `zh-CN` / `zh-TW`) 706-key alignment, adaptive deep-layer simulation bounds, and v4 ragdoll / grabber plant / crystal enemy sync improvements.
- **Dual-mode diagnostics & single-player passthrough**: Release vs. Debug structured communication logging, plus single-player passthrough guards across gameplay patches.
- **Game `v1.0.2` runtime stability & upstream bug fixes**: Fixes `PlayerCamera.SetTimeScale` 4-parameter signature mismatch, tutorial coroutine short-circuiting, cross-assembly DMD visibility, Mono `Dictionary` mutation-during-enumeration exceptions, OS dynamic font fallback for Unicode names/chat, `UseAck` compatibility probing, unlabeled canned food, firearm null guards, prosthetic suturing, tick nests, stimulants, inventory drag finalizers, and disconnect safety guards.

Core networking constraints remain identical to upstream: no client-side prediction, no distributed authority, no deterministic RNG, and no anti-cheat.

## Install Guide

### Requirements

- Game: [Casualties: Unknown](https://store.steampowered.com/app/4576510) (`v1.0.2`)
- [BepInEx 5.4.23.5 (x64)](https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.5/BepInEx_win_x64_5.4.23.5.zip)

### Steps

1. Open the game root directory (`Steam Library > Right-click game > Manage > Browse local files`).
2. Extract `BepInEx_win_x64_5.4.23.5.zip` directly into the game root directory (so `winhttp.dll` sits next to `CasualtiesUnknown.exe`).
3. Download `CasualtiesCohort-v5.5.4.zip` from [Releases](https://github.com/HuanXinWork/Casualties-Cohort/releases) and **extract it directly into the game root directory** (the archive root is `BepInEx/plugins/Together/`; there is no legacy `mod/` subfolder or deprecated `autoupdater_patcher.dll`).
4. Launch the game from your Steam Library. Multiplayer buttons will appear in the main menu.

> **Single-DLL quick update**: If the 7 runtime dependency DLLs are already installed under `BepInEx/plugins/Together/`, you can update by downloading `CasualtiesMP.dll` alone and overwriting `BepInEx/plugins/Together/CasualtiesMP.dll`.

### Release Assets

Every formal GitHub Release and daily `nightly` pre-release publishes 5 standard files:

| File | Purpose |
| --- | --- |
| `CasualtiesCohort-<tag>.zip` | Full Release package (`BepInEx/plugins/Together/` with `CasualtiesMP.dll` + 7 runtime dependency DLLs) |
| `CasualtiesCohort-<tag>+debug.zip` | Full Debug package (`CasualtiesMP.dll`, `CasualtiesMP.pdb` symbols + 7 runtime dependency DLLs, verbose diagnostics enabled) |
| `CasualtiesMP.dll` | Standalone Release plugin DLL |
| `CasualtiesMP+debug.dll` | Standalone Debug plugin DLL (rename to `CasualtiesMP.dll` when replacing manually) |
| `SHA256SUMS.txt` | SHA-256 checksums for the 4 binary assets above |

Pre-release builds tagged `v<ModVersion>-nightly.<YYYYMMDD>` track the latest commits on `main` between formal releases.

### Notes

- Return to singleplayer: `Settings > General > Deactivate MP Mod` (or pass `--mp-disable`).
- Linux / Proton launch option: `WINEDLLOVERRIDES="winhttp=n,b" %command%`.
- Always launch the game through the Steam Library if Steam lobbies fail to initialize.
- Joining a vanilla `v5.5.4` lobby may trigger a hash mismatch check if `Verify Game Hash` is enabled because the compiled DLLs differ; wire protocol compatibility remains intact.
- Report multiplayer bugs or crashes to this mod's issue tracker rather than to the base game developer.

## Features

- Steam lobbies, direct IP connection, and live host migration (`HostTransfer`)
- Categorized, searchable game rules menu with preset reset and a dedicated language switcher tab
- Proximity voice and text chat affected by in-game speech/hearing impairments (MP3 player acts as a spatial boombox)
- Middle mouse button location ping (double-click for alert ping)
- Right-click player interaction menu: inspect wounds & co-op medical minigames, inspect inventory, feed/dress items, piggyback, carry, and push
- Shared body heat and reduced mood decay near teammates; co-op tuned hunger, thirst, and bleed-out rates
- Spectator mode upon death (with optional freecam and wound inspection), respawning at the next layer or via trader recruitment (controlled by `Permadeath` and `ReviveFromTrader`)
- Layer transitions gated by `LayerFinishConditionPlayerPercent` (including the DrillPod)
- Per-player `Unchipped` status
- Dedicated server mode (non-headless)

All rules can be configured in the lobby UI or by the host via `rule <name> <value>` in the console.

## Console

To unlock cheat/server commands as Host:

```text
rule sv_cheats 1
```

Clients require admin privileges (`adminpriv <player>` or `adminpriv-password`) and the `admin` console toggle to run server commands.

## Building & CI/CD

### Zero-Secret Build (No Local Game Install Required)

The repository tracks stripped metadata reference stubs (`references/*.dll`, with all method body IL and embedded resources removed) alongside the 7 redistributable runtime libraries (`libs/dlls/*.dll`). With the .NET 8 SDK installed, you can compile and package directly after cloning without any secrets or local game files:

```powershell
# Compile ReleaseFullgame
dotnet build ScavPrototypeMP/CasualtiesTogether.csproj -c ReleaseFullgame

# Build ReleaseFullgame + DebugFullgame and package all 5 release assets
& ./scripts/package-release.ps1
```

### Building Against a Local Game Install & Regenerating Reference Stubs

- To compile against a full game installation and auto-copy output into `BepInEx/plugins/Together/`, set `CASUALTIESTOGETHER_GAME_PATH`, pass `-p:BaseGamePath="D:\SteamLibrary\steamapps\common\Casualties Unknown"`, or run `python scripts/prepare_workspace.py`.
- After a base game update, run `& ./scripts/generate-ref-stubs.ps1` on a machine with the game installed to regenerate `references/*.dll` via `BepInEx.AssemblyPublicizer`.

### GitHub Actions Automated Releases

[`.github/workflows/build-and-release.yml`](.github/workflows/build-and-release.yml) automates verification and publishing:

- **CI Build Verification**: Every push and pull request targeting `main` compiles Demo (`Release` / `Debug`) and Fullgame (`ReleaseFullgame` / `DebugFullgame`) configurations and uploads packaged artifacts.
- **Automatic Formal Releases**: Pushing an updated `<ModVersion>` in `Directory.Build.props` to `main` when `v<ModVersion>` does not yet exist on `origin` automatically tags the commit and publishes a formal GitHub Release with all 5 assets.
- **Daily Nightly Pre-releases**: Runs daily at `18:00` UTC on `main`; whenever new commits exist since the last formal or nightly tag, it creates `v<ModVersion>-nightly.<YYYYMMDD>` and publishes a Pre-release (also manually triggerable via `workflow_dispatch` with `auto` / `nightly` / `build_only`).

## Credits

Full credits are available in the in-game "About" menu.

- [Krokosha666](https://github.com/Krokosha666) — original developer (retired)
- [Creature](https://github.com/creaturefeaturelarry) — upstream maintainer
- [Dune](https://github.com/Jyan-sei) — sync, optimizations
- [VtLoader](https://github.com/vtloader) — bug fixes, localization
- Gary the Cat, Dannad, Meow, Jimmyking, Kidihh, Dawn, mayzmae, ItsVoidSK — code & playtesting contributions
- Plus all upstream playtesters and supporters

## License

LGPL-2.1, see [LICENSE](LICENSE). Distributed under the same license as upstream.
