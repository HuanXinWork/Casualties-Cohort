using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class Con {
    /// <summary>
    /// 注册世界环境、实体操作、物品发放、角色重生/换物种与局内玩法规则命令。
    /// </summary>
    private static void RegisterWorldAndGameplayCommands(ConsoleScript con) {
        RegisterCommand(new Command("objdelete", "MP - Despawn a specific object.", (string[] splited) => {
            if (Net.IsRunning) {
                ConFailIfCheatsDisabled();
                ConFailIfNetworkIsRunningAndIsClient();
            }
            GameObject go = GetObjectForCommand(splited.Length > 1 ? splited[1] : "cursor", false);
            if (go != null) {
                con.LogToConsole($"Deleted: {DebugHelp.GetGameObjectFullDebugString(go)}");
                GameObject.Destroy(go);
            } else {
                con.LogToConsole("Object wasn't found.");
            }
        }, null, ("obj", "Object to delete")));

        RegisterCommand(new Command("objinfo", "MP - Print object info.", (string[] splited) => {
            GameObject go = GetObjectForCommand(splited.Length > 1 ? splited[1] : "cursor", false);
            if (go != null) {
                string str = $"{DebugHelp.GetGameObjectFullDebugString(go)}";
                if (go.TryGetComponent(out Item i)) {
                    var container = ItemSync.ItemGetContainerInfo(i);
                    if (container.has_container) {
                        str += $" {container}";
                    }
                }
                str += $" pos:{(Vector2)go.transform.position}";
                con.LogToConsole(str);
            } else {
                con.LogToConsole("Object wasn't found.");
            }
        }, null, ("obj", "Object to inspect")));

        RegisterCommand(new Command("objtalkersay", "MP - Hijack object's Talker component.", (string[] splited) => {
            con.CheckArgumentCount(splited, 2);
            try {
                string objname = splited[1];
                string nameoftalkerobject = "";
                GameObject foundtalker_go = GetObjectOrBodyForCommand(objname);
                Talker foundtalker = null;

                if (!Util.TryGetTalkerOnObject(foundtalker_go, out foundtalker, out nameoftalkerobject)) {
                    Vector2 cursorpos = GetCommandCallerCursorPos();
                    Collider2D[] collider2Darray = Physics2D.OverlapPointAll(cursorpos);
                    for (int i = 0; i < collider2Darray.Length; i++) {
                        Collider2D collider2D2 = collider2Darray[i];
                        if (Util.TryGetTalkerOnObject(collider2D2.gameObject, out foundtalker, out nameoftalkerobject)) {
                            foundtalker_go = collider2D2.gameObject;
                            break;
                        }
                    }
                }

                string msg = string.Join(" ", splited.Skip(2));
                if (foundtalker != null && foundtalker_go != null) {
                    if (Multiplayer.IsNetworkActiveAndIsServer()) {
                        if (NetObjectRegistry.TryGetSyncInfo(foundtalker_go, out var si)) {
                            if (!ServerMain.ForceTalkerSayAndAnnounce(si, msg, null)) {
                                con.LogToConsole($"Failed to make {nameoftalkerobject} say \"{msg}\"");
                                return;
                            }
                        } else if (foundtalker_go.TryGetComponent(out NetBody pb)) {
                            if (!ServerMain.ForceTalkerSayAndAnnounce(pb, msg, null)) {
                                con.LogToConsole($"Failed to make {nameoftalkerobject} say \"{msg}\"");
                                return;
                            }
                        } else {
                            NetObjectRegistry.AlertObjectNotRegistered(true);
                        }
                    }

                    Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);
                    con.LogToConsole($"Made {nameoftalkerobject} say \"{msg}\"");
                } else {
                    con.LogToConsole("Specified talker wasn't found.");
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "objtalkersay", e);
            }
        }, null, ("obj", "Object with a Talker to talk")));

        RegisterCommand(new Command("earthquake", "MP - control earthquake", (string[] args) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(args, 1);
            con.CheckForWorld();
            WorldGeneration world = WorldGeneration.world;

            string arg = args[1].ToLowerInvariant();
            if (arg == "now") {
                world.earthquakeDelay = UnityEngine.Random.Range(600f, 1750f);
                world.earthquakeTime = UnityEngine.Random.Range(3f, 25f);
                Time.timeScale = 1f;
                con.LogToConsole("Started earthquake right now.");
            } else {
                WorldMP.earthquake_enabled = ParseBool01(arg, true);
                if (!WorldMP.earthquake_enabled) {
                    world.earthquakeTime = 0f;
                }
                con.LogToConsole($"Earthquake active: {WorldMP.earthquake_enabled}");
            }
        }, new Dictionary<int, List<string>> { { 0, new List<string> { "now", "disable", "enable" } } }, ("string", "Action")));

        RegisterCommand(new Command("dumpbodyvars", "MP - dump all fields of a body (for health desync debugging)", (string[] splited) => {
            con.CheckForWorld();
#if RELEASE
            ConFailIfCheatsDisabled();
#endif
            if (!Net.IsRunning) {
                log.l(Util.GetLocalBody().DumpBodyVars());
                return;
            }

            string plrname = splited.Length > 1 ? splited[1] : "@m";
            bool isall = plrname == "@a";
            bool hasplr = false;

            PerformActionOnBodiesByName(plrname, (NetBody npc) => {
                hasplr = true;
                if (!isall) {
                    plrname = npc.bodyName;
                }
                log.l(npc + " " + npc.body.DumpBodyVars());
            }, true, false, GetCommandCallerPlayer(), false);

            if (!hasplr) {
                con.LogToConsole($"{plrname} -> body not found. ");
            }
        }, new Dictionary<int, List<string>> {
            { 1, Resources.LoadAll<GameObject>("").Where((GameObject x) => (bool)x.GetComponent<Item>()).Select((GameObject x) => x.name).ToList() }
        },
            ("player", "to dumpbodyvars to"),
            ("string item_id", "Item resource ID, multiple is allowed")));

        RegisterCommand(new Command("give", "MP - give an item to a player", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfCheatsDisabled();
            con.CheckArgumentCount(splited, 2);

            List<string> items = splited.ToList().GetRange(2, splited.Length - 2);
            string plrname = splited[1];
            bool isall = plrname == "@a";
            bool hasplr = false;
            bool succ = false;

            PerformActionOnBodiesByName(plrname, (NetBody npc) => {
                hasplr = true;
                if (!isall) {
                    plrname = npc.bodyName;
                }

                List<string> givenItems = items;
                foreach (string itemname in items) {
                    if (SpawnThingOnPlayer(itemname, npc.body, true)) {
                        succ = true;
                    } else {
                        givenItems = new List<string>(givenItems);
                        givenItems.Remove(itemname);
                        con.LogToConsole($"{itemname} is not an item.");
                    }
                }
                items = givenItems;
                if (succ && givenItems.Count > 0) {
                    con.LogToConsole($"{npc.bodyName} got the {string.Join(" ", givenItems)}");
                }
            }, true, false, GetCommandCallerPlayer(), false);

            if (!hasplr) {
                con.LogToConsole($"{plrname} -> body not found. ");
            }
        }, new Dictionary<int, List<string>> {
            { 1, Resources.LoadAll<GameObject>("").Where((GameObject x) => (bool)x.GetComponent<Item>()).Select((GameObject x) => x.name).ToList() }
        },
            ("player", "to give to"),
            ("string item_id", "Item resource ID, multiple is allowed")));

        RegisterCommand(new Command("respawn", "MP - respawn a player", (string[] splited) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckForWorld();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 1);

            Vector2 goalpos = splited.Length >= 3
                ? con.ParsePosition(splited[2])
                : (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);

            string plrname = splited[1];
            bool isall = plrname == "@a";
            bool succ = false;
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!isall) {
                    plrname = plr.playerName;
                }
                plr.Server_RespawnCharacter(goalpos, false);
                succ = true;
            }, true, GetCommandCallerPlayer(), true, false);

            if (succ && res.Item1) {
                con.LogToConsole($"Respawned: {plrname}");
            } else if (res.Item1) {
                con.LogToConsole($"Failed to respawn: {plrname}");
            } else {
                con.LogToConsole($"Failed to respawn: {plrname} {res.Item2}");
            }
        }, new Dictionary<int, List<string>>(),
            ("player", "to respawn to"),
            ("position", "optional")));

#if FULLGAME
        RegisterCommand(new Command("species", "MP-SERVER - Force-swap a player's species. Optional wipe resets XP to that species' base skills.", (string[] splited) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckForWorld();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 3);

            if (!TryParseSpeciesArg(splited[1], out int chr)) {
                throw new Exception($"\"{splited[1]}\" is not a valid species (Milky / Expie / Dune).");
            }
            bool wipeXp = con.ParseBool(splited[2]);
            string plrname = splited[3];
            bool isall = plrname == "@a";
            bool succ = false;
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!isall) {
                    plrname = plr.playerName;
                }
                CharacterSpawn.ServerForceSwapSpecies(plr, chr, wipeXp);
                succ = true;
            }, true, GetCommandCallerPlayer(), true, false);

            if (succ && res.Item1) {
                con.LogToConsole($"Species -> {CharacterSpawn.SaveSpeciesName(chr)} wipeXp={wipeXp}: {plrname}");
            } else if (res.Item1) {
                con.LogToConsole($"Failed species swap: {plrname}");
            } else {
                con.LogToConsole($"Failed species swap: {plrname} {res.Item2}");
            }
        }, new Dictionary<int, List<string>> {
            { 0, new List<string> { "Milky", "Expie", "Dune" } },
        },
            ("species", "Milky / Expie / Dune"),
            ("bool wipeXp", "true = reset skill XP to species base"),
            ("player", "target player / @a / @s")));
#endif

        RegisterCommand(new Command("clearinventory", "MP - clear inventory of a player", (string[] splited) => {
            ConFailIfCheatsDisabled();
            con.CheckForWorld();

            bool delete = splited.Length > 1 && con.ParseBool(splited[1]);
            Action<Body> dothing = (Body b) => {
                if (delete) {
                    foreach (Item item in b.GetAllItemsThorough()) {
                        GameObject.Destroy(item.gameObject);
                    }
                }
                b.Body_DropAllItems();
            };

            if (Net.IsRunning && splited.Length > 2) {
                string plrname = splited[2];
                bool isall = plrname == "@a";
                bool succ = false;
                var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (!isall) {
                        plrname = plr.playerName;
                    }
                    dothing(plr.body);
                    succ = true;
                }, true, GetCommandCallerPlayer(), true, false);

                if (succ && res.Item1) {
                    con.LogToConsole($"Cleared inventory: {plrname}");
                } else if (res.Item1) {
                    con.LogToConsole($"Failed to clear inventory: {plrname}");
                } else {
                    con.LogToConsole($"Failed to clear inventory: {plrname} {res.Item2}");
                }
            } else {
                dothing(Util.GetLocalBody());
            }
        }, new Dictionary<int, List<string>>(),
            ("bool", "delete items"),
            ("player", "optional")));

        RegisterCommand(new Command("godmode", "MP - revive every player and keep resetting their health", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ref bool val = ref _DEV_GODMODE;
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !val;
            val = target;
            ConsoleScript.instance.LogToConsole($"godmode {val}");
        }, null));

        RegisterCommand(new Command("settile", "MP", (string[] splited) => {
            ConFailIfCheatsDisabled();
            ushort tileid = ushort.Parse(splited[1]);

            Vector2 goalpos;
            if (splited.Length < 4) {
                goalpos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
            } else {
                goalpos.x = float.Parse(splited[2], CultureInfo.InvariantCulture);
                goalpos.y = float.Parse(splited[3], CultureInfo.InvariantCulture);
            }
            Vector2Int realpos = WorldGeneration.world.WorldToBlockPos(goalpos);
            WorldGeneration.world.SetBlock(realpos, tileid);
            ConsoleScript.instance.LogToConsole($"Set tile {realpos.x} {realpos.y} id to {tileid} ({WorldGeneration.world.GetBlockInfo(tileid)?.name})");
        }, null));

        RegisterCommand(new Command("setfluid", "MP", (string[] splited) => {
            ConFailIfCheatsDisabled();
            byte fluidid = byte.Parse(splited[1]);

            Vector2 goalpos;
            if (splited.Length < 4) {
                goalpos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
            } else {
                goalpos.x = float.Parse(splited[2], CultureInfo.InvariantCulture);
                goalpos.y = float.Parse(splited[3], CultureInfo.InvariantCulture);
            }
            Vector2Int realpos = WorldGeneration.world.WorldToBlockPos(goalpos);
            FluidManager.main.fluid[realpos.x, realpos.y] = fluidid;
            ConsoleScript.instance.LogToConsole($"Set fluid {realpos.x} {realpos.y} id to {fluidid} ({FluidManager.WorldFluidToLiquidID[fluidid]})");
        }, null));

        RegisterCommand_LocalOnly(new Command("spawnpoint", "MP-SERVER- Set default spawn point. (It resets after world generates)", (string[] splited) => {
            ConFailIfCheatsDisabled();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 1);
            con.CheckForWorld();

            Vector2 pos = con.ParsePosition(splited[1]);
            Body_PlaceBody_MultiplayerPatch.SpawnLocation = pos;
            Multiplayer.rules.SpawnOnPlayer = false;
            log.l($"SpawnLocation set to: {pos}");
        }, null, ("position", "New spawn point")));

        RegisterCommand(new Command("skybg", "MP dev", (string[] splited) => {
            con.CheckForWorld();
            bool has_params = false;
            float r = 0f;
            Color c = Color.white;
            if (splited.Length == 3) {
                has_params = true;
                c = con.ParseColor(splited[1]);
                r = con.ParseFloat(splited[2]);
            }

            if (WorldMP.HasSkyBackground()) {
                WorldMP.RemoveSkyBackground();
            } else {
                ConFailIfCheatsDisabled();
                if (has_params) {
                    WorldMP.CreateSkyBackground(c, r);
                } else {
                    WorldMP.CreateSkyBackground();
                }
            }
            ConsoleScript.instance.LogToConsole($"skybg {WorldMP.HasSkyBackground()} ");
        }, null, ("color", "skyColor"), ("float", "rainIntensity")));

        RegisterCommand_LocalOnly(new Command("snowbg", "MP dev", (string[] splited) => {
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !PlayerCamera.main.backgroundSnow.activeSelf;
            PlayerCamera.main.backgroundSnow.SetActive(target);
            ConsoleScript.instance.LogToConsole($"snowbg {PlayerCamera.main.backgroundSnow.activeSelf} ");
        }, null));

        RegisterCommand(new Command("map", "MP dev", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNotInMainMenu();

            string m = splited.Length > 1 ? splited[1] : "debug";
            PreRunScript prs = PreRunScript.instance;

            if (m == "debug") {
                log.l("Loading debug world");
                WorldMP.LoadDebugWorld();
            } else if (m == "tutorial") {
                log.l("Loading tutorial world");
                prs.StartTutorial();
            } else {
                log.l("Loading vanilla world");
                if (splited.Contains("loadsave")) {
                    PlayerPrefs.SetInt("didbasiccourse", 1);
                    prs.LoadRun();
                } else {
                    prs.StartRun();
                }
            }
        }, new Dictionary<int, List<string>> { { 0, new List<string> { "debug", "tutorial", "vanilla" } } }));

        RegisterCommand(new Command("gamemode", "MP dev", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNotInMainMenu();

            if (GamemodeManager.HasGamemode()) {
                log.l($"Exited gamemode. {GamemodeManager.GetGamemode()}");
                GamemodeManager.DeleteGamemode();
            } else {
                con.CheckArgumentCount(splited, 1);
                string gamemodename = splited[1];
                var gm = GamemodeManager.GetAllAvailableGamemodes();

                foreach (var item in gm) {
                    if (item.Name == gamemodename) {
                        var br = GamemodeManager.SetGamemode(item);
                        try {
                            br.Init(splited.Skip(2).ToArray());
                        } catch (Exception ex) {
                            GamemodeManager.DeleteGamemode();
                            ModLog.Exception(LogDomain.Core, $"Failed to Init Gamemode {item.Name}", ex);
                        }
                        return;
                    }
                }
                con.LogToConsole($"Gamemode \"{gamemodename}\" was not found.");
            }
        }, new Dictionary<int, List<string>> {
            { 0, GamemodeManager.GetAllAvailableGamemodes().Select(x => x.Name).ToList() }
        },
            ("gamemode", ""),
            ("arg", "")));

        RegisterCommand(new Command("radline", "MP - Activate/Deactivate radline", (string[] args) => {
            con.CheckForWorld();
            ConFailIfNetworkIsRunningAndIsClient();

            bool target = args.Length > 1
                ? ConsoleScript.instance.ParseBool(args[1])
                : !RadiationLine.line.active;

            if (target) {
                RadiationLine.line.Activate();
            } else {
                RadiationLine.line.Deactivate();
                RadiationLine.line.timeGone = -10f;
            }
            ServerMain.timer_RareUpdateSyncClients += 1000f;
            ConsoleScript.instance.LogToConsole("Radiation line active: " + RadiationLine.line.active);
        }, null, ("bool", "Activate/Deactivate radline")));

        RegisterCommand(new Command("cleanworld", "MP- delete blast marks, block damages, casings, blood", (string[] splited) => {
            ConFailIfCheatsDisabled();
            con.CheckForWorld();

            CleanWorld();

            if (Net.IsServer) {
                var writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CustomWireCommands.ChannelCleanWorld);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
            }
        }, null));

        RegisterCommand(new Command("pasterunsettings", "MP - Paste run settings from a string.", delegate (string[] args) {
            if (Util.IsInWorld()) {
                ConFailIfCheatsDisabled();
            }
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(args, 1);
            WorldMP.ApplyRunSettings(args[1]);
        }, null, ("string base64", "")));

        Command runset = RegisterCommand(new Command("runset", "MP - Set a run setting.", delegate (string[] args) {
            if (Util.IsInWorld()) {
                ConFailIfCheatsDisabled();
            }
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(args, 2);

            string ruleName = args[1];
            string valueString = args[2];

            var originals = WorldMP.GetNormalRunSettings();
            if (!originals.TryGetValue(ruleName, out object value)) {
                throw new Exception($"Unknown run setting: {ruleName}");
            }

            object parsedValue = TypeDescriptor.GetConverter(value.GetType()).ConvertFromInvariantString(valueString);

            if (PreRunScript.instance != null) {
                if (PreRunScript.instance.runSettings != null) {
                    PreRunScript.instance.runSettings[ruleName] = parsedValue;
                }
                PreRunScript.instance.UpdateAllSettingDisplays();
            }
            if (WorldGeneration.runSettings != null) {
                WorldGeneration.runSettings[ruleName] = parsedValue;
            }

            con.LogToConsole($"Set run setting: {ruleName} to {parsedValue}");
        }, null, ("string name", ""), ("value", "")));

        runset.GetArgAutofillList(0).AddRange(WorldMP.GetNormalRunSettings().Keys);
    }
}
