#if !RELEASE
using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace Together;

public partial class Con {
    private static void RegisterDevLegacyCommands(ConsoleScript con) {
        RegisterCommand_LocalOnly(new Command("TEMP_known_entities_with_nonunique_id_to_og", "MP DEV DEV DELETEME", (string[] splited) => {
            log.l("TEMP_known_entities_with_nonunique_id_to_og START =============");
            foreach (var item in ScavMPBuildingSynchronizer.TEMP_known_entities_with_nonunique_id_to_og) {
                log.l($"{item.Key} -> {item.Value}");
            }
            log.l("TEMP_known_entities_with_nonunique_id_to_og END ==============");
        }, null));

        var krok = new Command("krok", "DEPRECATED- Multiplayer mod control. (legacy from before 1.12, no autocompletion)", delegate (string[] args) {
            con.LogToConsole(KrokoshaLegacyCommand(string.Join(" ", args), args.ToList()));
        }, null);
        RegisterCommand(krok);
    }

    /// <summary>
    /// 非发行版专属测试物资与实体自动生成协程（规范化替代原有的西里尔测试方法名）。
    /// </summary>
    public static IEnumerator SpawnDevStarterTestKit() {
        Plugin.log.LogInfo("Spawning testing pack of items...");

#if DEBUG
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(0, out var plr0) && plr0.body is Body body0 && body0 != null) {
            yield return null;
            GameObject d = SpawnThingOnPlayer("duffelbag", body0, true);
            SpawnThingOnPlayer("bikehelmet", body0, true);
            GameObject mdkit = SpawnThingOnPlayer("medkit", body0, true);
            SpawnThingOnPlayer("riflemagazine", body0, true, mdkit);
            SpawnThingOnPlayer("chocolatebar", body0, true, mdkit);
            SpawnThingOnPlayer("rifle", body0, true);
            SpawnThingOnPlayer("alcohol", body0, true);
            SpawnThingOnPlayer("glowplantfruit", body0, true);
            SpawnThingOnPlayer("mushtail", body0, true, d);
            SpawnThingOnPlayer("splint", body0, true, d);
            SpawnThingOnPlayer("bandage", body0, true, d);
            SpawnThingOnPlayer("heroin", body0, true, d);
            SpawnThingOnPlayer("foliage", body0, true, d);
            SpawnThingOnPlayer("flashlight", body0, true, d);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(1, out var plr1) && plr1.body is Body body1 && body1 != null) {
            yield return null;
            SpawnThingOnPlayer("pistol", body1, true);
            SpawnThingOnPlayer("flashlight", body1, true);
            GameObject tlbx = SpawnThingOnPlayer("toolbox", body1, true);
            SpawnThingOnPlayer("fentanyl", body1, true);
            SpawnThingOnPlayer("balaclava", body1, true);
            SpawnThingOnPlayer("dynamite", body1, true);
            SpawnThingOnPlayer("banana", body1, true, tlbx);
            SpawnThingOnPlayer("kneepads", body1, true);
            SpawnThingOnPlayer("heroin", body1, true, tlbx);
            SpawnThingOnPlayer("bandage", body1, true, tlbx);
            SpawnThingOnPlayer("bruisekit", body1, true, tlbx);
            SpawnThingOnPlayer("mushtail", body1, true, tlbx);
            SpawnThingOnPlayer("foliage", body1, true, tlbx);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(2, out var plr2) && plr2.body is Body body2 && body2 != null) {
            yield return null;
            SpawnThingOnPlayer("machete", body2, true);
            SpawnThingOnPlayer("lightbulb", body2, true);
            SpawnThingOnPlayer("pizzaslice", body2, true);
            GameObject p = SpawnThingOnPlayer("pouch", body2, true);
            SpawnThingOnPlayer("rag", body2, true);
            SpawnThingOnPlayer("sodacan", body2, true);
            SpawnThingOnPlayer("tornshirt", body2, true);
            SpawnThingOnPlayer("geigefruit", body2, true, p);
            SpawnThingOnPlayer("geofruit", body2, true, p);
            SpawnThingOnPlayer("foliage", body2, true, p);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(3, out var plr3) && plr3.body is Body body3 && body3 != null) {
            yield return null;
            SpawnThingOnPlayer("shotgun", body3, true);
            SpawnThingOnPlayer("milk", body3, true);
            SpawnThingOnPlayer("12gauge", body3, true);
            SpawnThingOnPlayer("pickaxe", body3, true);
            SpawnThingOnPlayer("hoodie", body3, true);
            SpawnThingOnPlayer("headlamp", body3, true);
            GameObject lp = SpawnThingOnPlayer("legpouch", body3, true);
            SpawnThingOnPlayer("dryfoliage", body3, true, lp);
            SpawnThingOnPlayer("naltrexone", body3, true, lp);
            SpawnThingOnPlayer("opium", body3, true, lp);
        }
#endif

        Utils.Create("soundcannon", new Vector2(77f, -127f), 0f);
        Utils.Create("trader1", new Vector2(20f, -52f), 0f);
        Utils.Create("trader2", new Vector2(25f, -52f), 0f);
        Utils.Create("trader3", new Vector2(30f, -52f), 0f);

        Utils.Create("rechargingstation", new Vector2(42f, -50f), 0f);
        Utils.Create("smallbattery", new Vector2(44f, -50f), 0f).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("mediumbattery", new Vector2(44f, -51f), 0f).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("largebattery", new Vector2(44f, -51.5f), 0f).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("smallbattery", new Vector2(45f, -50f), 0f).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("mediumbattery", new Vector2(45f, -51f), 0f).GetComponent<Item>().SetCondition(0.5f);
        Utils.Create("largebattery", new Vector2(45f, -51.5f), 0f).GetComponent<Item>().SetCondition(0.5f);

        Utils.Create("skullcrusher", new Vector2(45f, -40f), 0f);
        Utils.Create("turret", new Vector2(20f, -40f), 0f);
        Utils.Create("shadecrawler", new Vector2(96f, -59f), 0f);
        Utils.Create("overgrowntick", new Vector2(100f, -73f), 0f);
        Utils.Create("CaveTicks", new Vector2(113f, -73f), 0f);

        SpawnThingOnPlayer("plasticbag");
        yield return new WaitForSeconds(1.0f);
        for (int j = 0; j < 3; j++) {
            SpawnThingOnPlayer(ItemLootPool.pool["food"].PickRandom());
            SpawnThingOnPlayer(ItemLootPool.pool["water"].PickRandom());
            SpawnThingOnPlayer(ItemLootPool.pool["medical"].PickRandom());
        }
        yield return new WaitForSeconds(0.1f);
        for (int j = 0; j < 2; j++) {
            SpawnThingOnPlayer("duffelbag");
            SpawnThingOnPlayer("bikehelmet");
        }
        yield return new WaitForSeconds(0.1f);
        for (int j = 0; j < 5; j++) {
            SpawnThingOnPlayer("dynamite");
        }
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("toolbox");
        SpawnThingOnPlayer("medkit");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("rifle");
        SpawnThingOnPlayer("riflemagazine");
        SpawnThingOnPlayer("riflemagazine");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("shotgun");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("pistol");
        SpawnThingOnPlayer("smallmagazine");
        yield return new WaitForSeconds(0.1f);
        SpawnThingOnPlayer("fentanyl");
        SpawnThingOnPlayer("fentanyl");
        SpawnThingOnPlayer("fentanyl");

        yield return new WaitForSeconds(0.1f);
#if !DEBUG
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(0, out var p0) && p0.body is Body b0 && b0 != null) {
            yield return null;
            SpawnThingOnPlayer("bikehelmet", b0, true);
            SpawnThingOnPlayer("pistol", b0, true);
            SpawnThingOnPlayer("shotgun", b0, true);
            SpawnThingOnPlayer("shotgun", b0, true);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(1, out var p1) && p1.body is Body b1 && b1 != null) {
            yield return null;
            SpawnThingOnPlayer("rifle", b1, true);
            SpawnThingOnPlayer("dynamite", b1, true);
            SpawnThingOnPlayer("banana", b1, true);
            SpawnThingOnPlayer("bandage", b1, true);
            SpawnThingOnPlayer("shotgun", b1, true);
            SpawnThingOnPlayer("shotgun", b1, true);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(2, out var p2) && p2.body is Body b2 && b2 != null) {
            yield return null;
            SpawnThingOnPlayer("machete", b2, true);
            SpawnThingOnPlayer("lightbulb", b2, true);
            SpawnThingOnPlayer("pizzaslice", b2, true);
        }
        if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(3, out var p3) && p3.body is Body b3 && b3 != null) {
            yield return null;
            SpawnThingOnPlayer("milk", b3, true);
            SpawnThingOnPlayer("shotgun", b3, true);
            SpawnThingOnPlayer("12gauge", b3, true);
            SpawnThingOnPlayer("pickaxe", b3, true);
            GameObject lp = SpawnThingOnPlayer("legpouch", b3, true);
            SpawnThingOnPlayer("dryfoliage", b3, true, lp);
        }
        foreach (Body body in ScavPlayer.BodyToPlayerDict.Keys) {
            SpawnThingOnPlayer("machete", body, true);
        }
#endif
        yield return new WaitForSeconds(0.1f);
        for (int i = 0; i < 7; i++) {
            SpawnThingOnPlayer("12gauge");
        }

        yield return new WaitForSeconds(1.0f);
        Utils.Create("flashlight", new Vector2(69f, 45f), 270f).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(147f, 45f), 90f).GetComponent<CustomItemBehaviour>().state = 1;

        SpawnThingOnPlayer("machete").transform.position = new Vector2(76f, 44f);
        SpawnThingOnPlayer("wrench").transform.position = new Vector2(77.5f, 44f);
        SpawnThingOnPlayer("sledgehammer").transform.position = new Vector2(79f, 44f);
        SpawnThingOnPlayer("rifle").transform.position = new Vector2(83f, 44f);
        SpawnThingOnPlayer("tweezers").transform.position = new Vector2(84.5f, 44f);
        SpawnThingOnPlayer("saline").transform.position = new Vector2(84.5f, 46f);
        SpawnThingOnPlayer("tourniquet").transform.position = new Vector2(78.1f, 44f);
        SpawnThingOnPlayer("medicalsuture").transform.position = new Vector2(84.5f, 46f);

        Utils.Create("flashlight", new Vector2(-43f, 66f), 270f).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(0f, 53f), 0f).GetComponent<CustomItemBehaviour>().state = 1;
        Utils.Create("flashlight", new Vector2(48f, 66f), 90f).GetComponent<CustomItemBehaviour>().state = 1;

        Utils.Create("dropcapsule", new Vector2(20f, 70f), 0f);
        Utils.Create("lifepodchest", new Vector2(24.5f, 70f), 0f);
        Utils.Create("lifepodchest", new Vector2(29f, 70f), 0f);

        for (int i = 555; i < 567; i++) {
            WorldGeneration.world.SetBlock(new Vector2Int(579, i), 20);
            WorldGeneration.world.SetBlock(new Vector2Int(661, i), 20);
        }
        for (int i = 577; i < 590; i++) {
            WorldGeneration.world.SetBlock(new Vector2Int(467, i), 20);
            WorldGeneration.world.SetBlock(new Vector2Int(562, i), 20);
        }

        Vector2Int blockOrigin = WorldGeneration.world.WorldToBlockPos(new Vector2(-390f, 0f));
        for (int x = blockOrigin.x; x < blockOrigin.x + 50; x++) {
            for (int y = blockOrigin.y; y < blockOrigin.y + 50; y++) {
                FluidManager.main.fluid[x, y] = 1;
            }
        }
    }

    public static string KrokoshaLegacyCommand(string inputtext, List<string> args) {
        string helptext = "Krokosha Scav Multiplayer mod commands (HOST ONLY) (DEPRECATED): \n";
        helptext += "krok maxplayers [number] // Set player count limit.\n";
        helptext += "krok rule // Set multiplayer game rules\n";
        helptext += "krok rules // List multiplayer game rules\n";
        helptext += "krok CHEATS // enable all cheat commands\n";
#if TESTING
        helptext += "// (now dev-only, break the game)\n";
#endif
        helptext += "krok vomit [player] // vomit\n";
        helptext += "krok screenshotmode\n";
        helptext += "krok freeze // freezes monsters\n";
#if TESTING
        helptext += "krok syncinfo // more debug info\n";
        helptext += "krok syncevent // visualise events tied to a pos\n";
        helptext += "krok verbose // verbose\n";
        helptext += "krok cleanworld // despawn monsters and useless items\n";
        helptext += "krok objreset // unregister all objects, to test sync again\n";
        helptext += "krok sync // Toggle specific synchronization systems (DEV ONLY):\n";
#endif
        helptext += "\tkrok sync tile // World tiles sync.\n";
        helptext += "\tkrok sync fluid // World fluids sync.\n";
        helptext += "\tkrok sync health // Player health sync.\n";

        if (args.Count < 2) {
            return helptext;
        }

        if (args[1].ToLowerInvariant() == "cheats") {
            if (Net.IsClient && Net.IsRunning) {
                return "server-only";
            }
            bool tell_goahead = !CanCheat();
            bool target = args.Count > 2 ? ParseBool01(args[2]) : !Multiplayer.rules.sv_cheats;

            Multiplayer.rules.sv_cheats = target;
            Multiplayer.rules.AllowClientCheatCommands = target;
            Multiplayer.ApplyGameRules();

            if (Multiplayer.rules.sv_cheats) {
                return tell_goahead ? "Cheats enabled." : "no";
            }
            return "Cheats disabled.";
        }

        switch (args[1]) {
            case "objreset":
                NetObjectRegistry.UnregisterAllObjectsButKeepInventory();
                return "unregistered all registered objects ";

            case "syncinfo":
                NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = args.Count < 3
                    ? !NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING
                    : ParseBool01(args[2]);
                return $"syncinfo: {NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING} ";

            case "verbose":
                DebugMenuSettings.is_verbose = args.Count < 3
                    ? !Multiplayer.verbose
                    : ParseBool01(args[2]);
                return $"verbose: {Multiplayer.verbose} ";

#if TESTING
            case "cleanworld":
                ConFailIfCheatsDisabled();
                CleanWorld();
                return "cleanworld";
#endif

#if DEBUG || RELEASETEST
            case "freeze":
                foreach (var item in GameObject.FindObjectsOfType<SpiderHandler>()) {
                    if (item.TryGetComponent<Rigidbody2D>(out var rb)) {
                        rb.bodyType = RigidbodyType2D.Static;
                    }
                }
                return "freezed monsters";
#endif

            case "help":
                return helptext;

            case "maxplayers":
                ConFailIfNetworkAlreadyRunning();
                ConFailIfNetworkIsRunningAndIsClient();
                if (args.Count < 3) {
                    return "expected a number ";
                }
                Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp(ushort.Parse(args[2]), 0, 251);
                return $"Set player count limit to: {Multiplayer.PLAYER_COUNT_LIMIT} ";

            case "rules": {
                string rules = "Krokosha Co-op mod game rules:\n";
                foreach (FieldInfo fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    rules += $"krok rule {fld.Name} [{fld.FieldType.Name}] // current: {fld.GetValue(Multiplayer.rules)}\n";
                }
                return rules;
            }

            case "rule":
                if (Net.IsRunning && Net.IsClient) {
                    return "server-only ";
                }
                if (args.Count < 4) {
                    return "expected a rule name and value ";
                }
                ConFailIfNetworkIsRunningAndIsClient();
                string rulename = args[2];
                string rulevalstr = args[3];
                foreach (FieldInfo fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    if (fld.Name == rulename) {
                        try {
                            object typedValue = fld.FieldType == typeof(bool)
                                ? ParseBool01(rulevalstr)
                                : Convert.ChangeType(rulevalstr, fld.FieldType, CultureInfo.InvariantCulture);
                            TypedReference rulesref = __makeref(Multiplayer.rules);
                            fld.SetValueDirect(rulesref, typedValue);
                            Multiplayer.ApplyGameRules();
                            return $"Succesfully set rule '{fld.Name}' to '{fld.GetValue(Multiplayer.rules)}'.";
                        } catch (Exception ex) {
                            return $"Failed to set rule '{fld.Name}': {ex.Message}";
                        }
                    }
                }
                return $"Rule '{rulename}' does not exist!";
        }

        return "THIS IS DEPRECATED";
    }
}
#endif
