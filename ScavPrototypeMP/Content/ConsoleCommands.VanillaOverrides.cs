using HarmonyLib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

public partial class Con {
    /// <summary>
    /// 覆写原版 12 条核心控制台命令，为其扩展多玩家目标选择器（<c>@a</c>/<c>@c</c>/<c>@m</c>/玩家名）与联机权限控制。
    /// </summary>
    private static void RegisterVanillaCommandOverrides(ConsoleScript con) {
        Command tp = ConsoleScript.SearchExact("tp");
        if (tp == null) throw new Exception("vanilla command 'tp' missing");
        tp.action = delegate (string[] args) {
            con.CheckForWorld();
            ConFailIfCheatsDisabled();

            Vector2 vector = Camera.main.ScreenToWorldPoint(Input.mousePosition);
            if (args.Length > 1) {
                vector = con.ParsePosition(args[1]);
            }

            if (args.Length > 2 && Net.IsRunning) {
                string plrname = args[2];
                bool succ = false;
                bool isall = plrname == "@a";

                var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (!isall) {
                        plrname = plr.playerName;
                    }
                    if (Net.IsServer) {
                        plr.Server_TeleportCharacter(vector);
                    } else {
                        plr.netBody.SetBodyPosition(vector);
                    }
                    succ = true;
                }, true, GetCommandCallerPlayer(), true, false);

                if (succ && res.Item1) {
                    con.LogToConsole($"Teleported: {plrname} to {vector}");
                } else if (res.Item1) {
                    con.LogToConsole($"Failed to teleport: {plrname}");
                } else {
                    con.LogToConsole($"Failed to teleport: {plrname} {res.Item2}");
                }
            } else {
                PlayerCamera.main.body.transform.position = vector;
                PlayerCamera.main.transform.position = vector;
                con.LogToConsole($"Teleported player to {vector}.");
            }
        };
        tp.argDescription = tp.argDescription.AddToArray(("player", "optional, Who to teleport"));

        Command kill = ConsoleScript.SearchExact("kill");
        if (kill == null) throw new Exception("vanilla command 'kill' missing");
        kill.action = delegate (string[] args) {
            con.CheckForWorld();
            if (Net.IsRunning) {
                ConFailIfCheatsDisabled();
            }

            if (args.Length > 1) {
                string plrname = args[1];
                bool isall = plrname == "@a";
                bool succ = false;
                PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    plr.body.brainHealth = 0f;
                    plr.body.heartRate = 0f;
                    plr.netBody.OnDamageEvent(new BodyDamageEvent("<key:plrdeath_command>", plr.netBody, 1f));
                    plrname = plr.playerName;
                    succ = true;
                    con.LogToConsole($"Killed: {plrname} ");
                }, true, GetCommandCallerPlayer(), true, false);

                if (succ) {
                    if (isall) {
                        con.LogToConsole("Killed everyone :) ");
                    }
                } else {
                    con.LogToConsole($"{plrname} not found. ");
                }
            } else {
                PlayerCamera.main.body.brainHealth = 0f;
                PlayerCamera.main.body.heartRate = 0f;
                con.LogToConsole("Killed the player.");
            }
        };
        kill.argDescription = kill.argDescription.AddToArray(("player", "optional"));

        Command amputate = ConsoleScript.SearchExact("amputate");
        if (amputate == null) throw new Exception("vanilla command 'amputate' missing");
        amputate.action = delegate (string[] args) {
            con.CheckForWorld();
            ConFailIfCheatsDisabled();

            if (args.Length > 2) {
                string plrname = args[2];
                bool succ = false;
                var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    Limb limb2 = plr.body.LimbByName(args[1]);
                    string str = DebugHelp.GetGameObjectFullDebugString(limb2);
                    if (!limb2) {
                        throw new Exception("\"" + str + "\" is not a valid limb!");
                    }
                    if (limb2.isAbdomen || limb2.isHead || limb2.isVital) {
                        throw new Exception("\"" + str + "\" is not an extremity and cannot be amputated!");
                    }
                    if (limb2.dismembered) {
                        throw new Exception(str + " is already dismembered!");
                    }
                    limb2.Dismember();
                    succ = true;
                    LogToConsole($"Dismembered {str}.");
                }, true, GetCommandCallerPlayer(), true, false);

                if (!succ && !res.Item1) {
                    con.LogToConsole($"{plrname} not found. ");
                }
            } else {
                Limb limb2 = PlayerCamera.main.body.LimbByName(args[1]);
                if (!limb2) {
                    throw new Exception("\"" + args[1] + "\" is not a valid limb!");
                }
                if (limb2.isAbdomen || limb2.isHead || limb2.isVital) {
                    throw new Exception("\"" + args[1] + "\" is not an extremity and cannot be amputated!");
                }
                if (limb2.dismembered) {
                    throw new Exception(args[1] + " is already dismembered!");
                }
                limb2.Dismember();
                LogToConsole("Dismembered \"" + limb2.fullName + "\".");
            }
        };
        amputate.argDescription = amputate.argDescription.AddToArray(("player", "optional"));

        Command setbodyfield = ConsoleScript.SearchExact("setbodyfield");
        if (setbodyfield == null) throw new Exception("vanilla command 'setbodyfield' missing");
        setbodyfield.action = delegate (string[] args) {
            con.CheckForWorld();
            con.CheckArgumentCount(args, 2);
            string text = args[1];
            FieldInfo field = typeof(Body).GetField(text);
            object obj = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(args[2]);

            if (args.Length > 3 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                var result = PerformActionOnPlayersByName(args[3], (ScavPlayer plr) => {
                    field.SetValue(plr.body, obj);
                    con.LogToConsole($"Set '{plr.playerName}' body field \"" + text + "\" to \"" + obj + "\".");
                }, true, GetCommandCallerPlayer(), true, false);

                if (!result.Item1) {
                    con.LogToConsole($"ERROR: Set body field: {args[3]}  - {result.Item2}");
                }
            } else {
                field.SetValue(PlayerCamera.main.body, obj);
                con.LogToConsole("Set player body field \"" + text + "\" to \"" + obj + "\".");
            }
        };
        setbodyfield.argDescription = setbodyfield.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

        Command setlimbfield = ConsoleScript.SearchExact("setlimbfield");
        if (setlimbfield == null) throw new Exception("vanilla command 'setlimbfield' missing");
        setlimbfield.action = delegate (string[] args) {
            con.CheckForWorld();
            con.CheckArgumentCount(args, 3);

            string limbname = args[1];
            bool is_all = limbname.ToLowerInvariant() == "all";
            Limb localplayer_limb = is_all ? PlayerCamera.main.body.GetHead() : PlayerCamera.main.body.LimbByName(limbname);
            if (!localplayer_limb) {
                throw new Exception("\"" + args[1] + "\" is not a valid limb!");
            }
            string text = args[2];
            FieldInfo field = typeof(Limb).GetField(text);
            object obj = TypeDescriptor.GetConverter(field.FieldType).ConvertFromInvariantString(args[3]);

            if (args.Length > 4 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[4];
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (is_all) {
                        foreach (Limb l in plr.body.limbs) {
                            field.SetValue(l, obj);
                        }
                    } else {
                        Limb targetlimb = plr.body.LimbByName(limbname);
                        field.SetValue(targetlimb, obj);
                    }
                    con.LogToConsole($"Set {plr.playerName}'s \"" + limbname + "\" field \"" + text + "\" to \"" + obj + "\".");
                }, true, GetCommandCallerPlayer(), true, false);

                if (!result.Item1) {
                    con.LogToConsole($"ERROR: Set limb field: {plrname}  - {result.Item2}");
                }
            } else {
                if (is_all) {
                    foreach (Limb l in PlayerCamera.main.body.limbs) {
                        field.SetValue(l, obj);
                    }
                } else {
                    field.SetValue(localplayer_limb, obj);
                }
                con.LogToConsole("Set \"" + limbname + "\" field \"" + text + "\" to \"" + obj + "\".");
            }
        };
        setlimbfield.argDescription = setlimbfield.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

        Command heal = ConsoleScript.SearchExact("heal");
        if (heal == null) throw new Exception("vanilla command 'heal' missing");
        heal.action = delegate (string[] args) {
            con.CheckForWorld();
            if (args.Length > 1 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[1];
                bool everyone = plrname == "@a";
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (plr.body == null) {
                        con.LogToConsole($"ERROR: Heal: {plr.playerName} has no body.");
                        return;
                    }
                    plr.body.ResetHealth();
                    if (!everyone) {
                        con.LogToConsole("Healed: " + plr.playerName);
                    }
                }, true, GetCommandCallerPlayer(), true, false);

                if (result.Item1) {
                    if (everyone) {
                        con.LogToConsole("Healed everyone.");
                    }
                } else {
                    con.LogToConsole($"ERROR: Heal: {plrname}  - {result.Item2}");
                }
            } else {
                if (PlayerCamera.main == null || PlayerCamera.main.body == null) {
                    con.LogToConsole("ERROR: Heal: no local body.");
                    return;
                }
                PlayerCamera.main.body.ResetHealth();
                con.LogToConsole("Healed the player.");
            }
        };
        heal.argDescription = heal.argDescription.AddToArray(("player", "optional, name of a player to heal"));

        Command addliquid = ConsoleScript.SearchExact("addliquid");
        if (addliquid == null) throw new Exception("vanilla command 'addliquid' missing");
        addliquid.action = delegate (string[] args) {
            con.CheckArgumentCount(args, 2);
            con.CheckForWorld();

            if (Net.IsRunning && args.Length > 3) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[3];
                bool everyone = plrname == "@a";
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    if (plr.body == null) {
                        return;
                    }
                    Body body = plr.body;
                    Item item = body.GetItem(0);
                    if (!item) {
                        con.LogToConsole($"{plr} - Not holding an item in the main hand!");
                        return;
                    }
                    WaterContainerItem component = item.GetComponent<WaterContainerItem>();
                    if (!component) {
                        con.LogToConsole($"{plr} - \"" + item.fullName + "\" is not a liquid container!");
                        return;
                    }
                    string text = args[1];
                    float num = con.ParseFloat(args[2]);
                    if (!Liquids.LiquidExists(text)) {
                        con.LogToConsole($"{plr} - \"" + text + "\" is not a valid liquid id!");
                        return;
                    }
                    component.AddLiquid(text, num);
                    con.LogToConsole($"Added {num}mL of {text} to {item.fullName} ({component.CurrentTotal}mL/{component.Capacity}mL).");
                }, true, GetCommandCallerPlayer(), true, false);

                if (result.Item1) {
                    if (everyone) {
                        con.LogToConsole("addliquided everyone.");
                    }
                } else {
                    con.LogToConsole($"ERROR: addliquid: {plrname}  - {result.Item2}");
                }
            } else {
                Body body = PlayerCamera.main.body;
                Item item = body.GetItem(0);
                if (!item) {
                    throw new Exception("Not holding an item in the main hand!");
                }
                WaterContainerItem component = item.GetComponent<WaterContainerItem>();
                if (!component) {
                    throw new Exception("\"" + item.fullName + "\" is not a liquid container!");
                }
                string text = args[1];
                float num = con.ParseFloat(args[2]);
                if (!Liquids.LiquidExists(text)) {
                    throw new Exception("\"" + text + "\" is not a valid liquid id!");
                }
                component.AddLiquid(text, num);
                con.LogToConsole($"Added {num}mL of {text} to {item.fullName} ({component.CurrentTotal}mL/{component.Capacity}mL).");
            }
        };
        addliquid.argDescription = addliquid.argDescription.AddToArray(("player", "optional, name of a player to addliquid"));

        Command alert = ConsoleScript.SearchExact("alert");
        if (alert == null) throw new Exception("vanilla command 'alert' missing");
        alert.action = delegate (string[] args) {
            con.CheckArgumentCount(args, 2);
            con.CheckForWorld();
            string text6 = string.Join(" ", args.Skip(2));
            bool important = con.ParseBool(args[1]);

            CasualtiesTogetherUtils.Util.DoAlert(text6, important);
            if (Multiplayer.IsNetworkActiveAndIsServer()) {
                ServerMain.Server_AnnounceAlert(text6, important, true, ServerMain.AllClientIdsExceptHost);
                con.LogToConsole("Announced display alert \"" + text6 + "\"");
            } else {
                con.LogToConsole("Displayed alert \"" + text6 + "\"");
            }
        };

        Command unchipped = ConsoleScript.SearchExact("unchipped");
        if (unchipped == null) throw new Exception("vanilla command 'unchipped' missing");
        unchipped.action = delegate (string[] args) {
            con.CheckArgumentCount(args, 1);
            con.CheckForWorld();

            bool unchippedVal = con.ParseBool(args[1]);
            if (args.Length > 2 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[2];
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    plr.SetUnchipped(unchippedVal);
                    con.LogToConsole($"Set unchipped of {plr.playerName} to {unchippedVal}.");
                }, true, GetCommandCallerPlayer(), true, false);

                if (!result.Item1) {
                    con.LogToConsole($"ERROR: Set unchipped: {plrname}  - {result.Item2}");
                }
            } else {
                WorldGeneration.world.SetUnchipped(unchippedVal);
                con.LogToConsole("Set unchipped to " + unchippedVal + ".");
            }
        };
        unchipped.argDescription = unchipped.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

        Command addxp = ConsoleScript.SearchExact("addxp");
        if (addxp == null) throw new Exception("vanilla command 'addxp' missing");
        addxp.action = delegate (string[] args) {
            con.CheckArgumentCount(args, 2);
            con.CheckForWorld();
            float num5 = Mathf.Max(con.ParseFloat(args[2]), 0f);

            if (args.Length > 3 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[3];
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    switch (args[1]) {
                        case "str":
                            plr.body.skills.AddExp(0, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} strength experience.");
                            break;
                        case "res":
                            plr.body.skills.AddExp(1, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} resilience experience.");
                            break;
                        case "int":
                            plr.body.skills.AddExp(2, num5);
                            con.LogToConsole($"Gave {plr.playerName} {Mathf.RoundToInt(num5)} intelligence experience.");
                            break;
                        default:
                            con.LogToConsole("\"" + args[1] + "\" is not a valid skill type!");
                            break;
                    }
                }, true, GetCommandCallerPlayer(), true, false);

                if (!result.Item1) {
                    con.LogToConsole($"ERROR: Set experience: {plrname}  - {result.Item2}");
                }
            } else {
                Body body = PlayerCamera.main.body;
                switch (args[1]) {
                    case "str":
                        body.skills.AddExp(0, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} strength experience.");
                        break;
                    case "res":
                        body.skills.AddExp(1, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} resilience experience.");
                        break;
                    case "int":
                        body.skills.AddExp(2, num5);
                        con.LogToConsole($"Gave the player {Mathf.RoundToInt(num5)} intelligence experience.");
                        break;
                    default:
                        throw new Exception("\"" + args[1] + "\" is not a valid skill type!");
                }
            }
        };
        addxp.argDescription = addxp.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

        Command resetskills = ConsoleScript.SearchExact("resetskills");
        if (resetskills == null) throw new Exception("vanilla command 'resetskills' missing");
        resetskills.action = delegate (string[] args) {
            con.CheckForWorld();
            if (args.Length > 1 && Net.IsRunning) {
                ConFailIfNetworkIsRunningAndIsClient();
                string plrname = args[1];
                var result = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                    Body body = plr.body;
                    body.skills.STR = 0;
                    body.skills.RES = 0;
                    body.skills.INT = 0;
                    body.skills.expSTR = 0f;
                    body.skills.expRES = 0f;
                    body.skills.expINT = 0f;
                    body.skills.UpdateExpBoundaries();
                    con.LogToConsole($"Reset all {plr.playerName} skills.");
                }, true, GetCommandCallerPlayer(), true, false);

                if (!result.Item1) {
                    con.LogToConsole($"ERROR: Reset experience: {plrname}  - {result.Item2}");
                }
            } else {
                Body body = PlayerCamera.main.body;
                body.skills.STR = 0;
                body.skills.RES = 0;
                body.skills.INT = 0;
                body.skills.expSTR = 0f;
                body.skills.expRES = 0f;
                body.skills.expINT = 0f;
                body.skills.UpdateExpBoundaries();
                con.LogToConsole("Reset all player skills.");
            }
        };
        resetskills.argDescription = resetskills.argDescription.AddToArray(("player", "optional, name of a player to mess with"));

        Command timescale = ConsoleScript.SearchExact("timescale");
        if (timescale == null) throw new Exception("vanilla command 'timescale' missing");
        timescale.action = delegate (string[] args) {
            con.CheckArgumentCount(args, 1);
            con.CheckForWorld();
            ConFailIfNetworkIsRunningAndIsClient();
            float num = con.ParseFloat(args[1]);
            if (Mathf.Abs(num) != 1f && num != 0f && Net.IsRunning) {
                num = Multiplayer.rules.DisableTimeManipulation ? 1f : Mathf.Round(num);
            }
            Time.timeScale = num;
            con.LogToConsole("Set timescale to " + num + ".");
        };
    }

    /// <summary>
    /// 为所有未列入非作弊白名单的原版命令统一注入作弊权限校验与服务端独占拦截。
    /// </summary>
    private static void WrapVanillaCheatAndServerCommands() {
        var ogcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "help", "heal", "coagulate", "kill", "spawn", "spawncategory", "tp", "skiplayer",
            "skiptext", "log", "talk", "framerate", "alert", "saveandquit", "resetskills",
            "fucklore", "timescale", "setconsoleheight", "setconsolecolor", "copylog", "clear",
            "addxp", "loglocale", "nukeplayerprefs", "openfolder", "setbodyfield", "setlimbfield",
            "amputate", "unchipped", "addcustomcommand", "addliquid", "locate",
            "removecustomcommand", "music", "bind", "repeat", "explode", "floodfill", "echo",
            "ui", "freecam", "starterkit", "noclip", "playsound", "fullbright", "plushies",
            "errorlogging",
        };
        var allowedcommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "krok rule EnableNametags", "krok rules", "krok help", "krok maxplayers", "krok kick",
            "pixelate", "help", "setvolume", "talk", "log", "framerate", "volume", "saveandquit",
            "fucklore", "setconsoleheight", "setconsolecolor", "copylog", "clear", "loglocale",
            "openfolder", "addcustomcommand", "removecustomcommand", "timescale", "music", "bind",
            "echo", "errorlogging",
        };

        foreach (Command command in ConsoleScript.Commands) {
            if (ogcommands.Contains(command.name) && !allowedcommands.Contains(command.name)) {
                var og_action = command.action;
                command.action = (string[] args) => {
                    ConFailIfCheatsDisabled();
#if FULLGAME
                    try {
                        AccessTools.DeclaredMethod(typeof(ConsoleScript), "CheckForCheats")
                            ?.Invoke(ConsoleScript.instance, null);
                    } catch (Exception cheatEx) {
                        log.warn($"CheckForCheats: {cheatEx.GetBaseException().Message}");
                    }
#endif
                    og_action(args);
                };
            }
        }

        var serveronlycommands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
            "spawn",
        };
        foreach (Command command in ConsoleScript.Commands) {
            if (serveronlycommands.Contains(command.name)) {
                var og_action = command.action;
                command.action = (string[] args) => {
                    ConFailIfNetworkIsRunningAndIsClient();
                    og_action(args);
                };
            }
        }
    }
}
