using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Linq;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    public partial class Plugin {
        static IEnumerator DEV_RunTestScenario() {
#if !RELEASE
            switch (test_scenario) {
                case "amputate_and_regrow": {
                    if (!Plugin.cmd_start__is_client) {
                        yield return new WaitForSecondsRealtime(0.1f);

                        Con.RunCommand($"krok tp @a -3.25 67.75");

                        yield return new WaitForSecondsRealtime(0.2f);
                        Body body = ScavPlayer.GetBodyFromClientId(1);

                        body.limbs[(int)LimbIndex.PrimaryUpperArm].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.SecondaryUpperArm].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.BackThigh].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.FrontThigh].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.Head].Dismember();
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.LowerTorso].Dismember();

                        yield return new WaitForSecondsRealtime(0.1f);
                        body.limbs[(int)LimbIndex.UpperTorso].Dismember();

                        yield return new WaitForSecondsRealtime(1.0f);
                        body.ResetHealth();
                        body.shock = 0;
                        body.Stand(true);
                    } else {
                        Body body = Util.GetLocalBody();

                        yield return new WaitForSecondsRealtime(1.9f);
                        while (body.limbs[(int)LimbIndex.Head].dismembered) { yield return new WaitForSeconds(0.1f); }
                        body.ResetHealth();
                        body.shock = 0;
                        body.Stand(true);
                        yield return new WaitForSecondsRealtime(0.1f);
                        body.Jump();
                    }

                    break;
                }

                case "jungle_perf": {
                    Con.RunCommand("volume 0.011", true);

                    if (!Plugin.cmd_start__is_client) {
                        Multiplayer.rules.ScatterPunishDistance = 1500;
                        Multiplayer.ApplyGameRules();

                        WorldGeneration.world.trapRarityMultiplier = 4;
                        Con.RunCommand("skiplayer 4", true);

                        yield return new WaitForSecondsRealtime(3f);

                        while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(2.0f);

                        Con.RunCommand("give @a pistol shotgun mp3player shotgun legpouch bigpack paincream waterbottle foliage foliage foliage foliage bandage foliage tourniquet splint bandage experimentflesh rifle machete bikehelmet hoodie sneakers fentanyl shotgun riflemagazine dynamite", true);

                        yield return new WaitForSeconds(2.0f);

                        Con.RunCommand("godmode", true);

                        while (!WorldGeneration.world.generatingWorld) {
                            yield return new WaitForSeconds(5f);

                            if (ScavPlayer.BodyToPlayerDict.All(x => x.Key.moveDir == Vector2.zero)) {
                                Con.RunCommand($"tp {Mathf.Round(UnityEngine.Random.Range(-1f, 1f) * 500)},{Mathf.Round(UnityEngine.Random.Range(-1f, 1f) * 500)} @r");
                            }
                        }
                    }

                    break;
                }

                case "sleep_test": {
                    if (!Plugin.cmd_start__is_client) {
                        Multiplayer.rules.EnableSleep = true;
                        Multiplayer.ApplyGameRules();

                        yield return new WaitForSecondsRealtime(3f);

                        while (!Util.IsWorldGenerated()) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(2.0f);

                        Con.RunCommand("setbodyfield energy 20 @a", true);
                        foreach (ScavPlayer item in ScavPlayer.BodyToPlayerDict.Values) {
                            MedicalSync.Server_QueueSendCharacterHealth(item.netBody);
                        }

                        yield return new WaitForSeconds(2.0f);

                        if (Util.TryGetLocalBody(out Body lb)) {
                            WoundView.view.TakeANap();
                        }
                    } else {
                        while (!Util.IsWorldGenerated()) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(7.0f);

                        Con.RunCommand("setbodyfield energy 13", true);
                        Multiplayer.rules.EnableSleep = true;

                        WoundView.view.TakeANap();
                    }

                    break;
                }

                case "save_test": {
                    Con.RunCommand("volume 0.011", true);

                    if (!Plugin.cmd_start__is_client) {
                        Con.RunCommand("give @a fentanyl");
                        Con.RunCommand("give id:1 hoodie pistol");

                        yield return new WaitForSecondsRealtime(3f);

                        Con.RunCommand("saveandquit");
                    }

                    break;
                }

                case "saveload_test": {
                    Con.RunCommand("volume 0.011", true);

                    if (!Plugin.cmd_start__is_client) {
                        Con.RunCommand("give @a fentanyl");
                        Con.RunCommand("give id:1 hoodie pistol");

                        yield return new WaitForSecondsRealtime(3f);

                        Con.RunCommand("saveandquit");

                        yield return new WaitForSecondsRealtime(3f);

                        PreRunScript prs = PreRunScript.instance;
                        if (prs != null) {
                            prs.LoadRun();
                        }
                        yield return new WaitForSecondsRealtime(3f);
                    }

                    break;
                }

                case "cpr_test": {
                    if (!Plugin.cmd_start__is_client) {
                        yield return new WaitForSecondsRealtime(0.1f);

                        Con.RunCommand($"give @a legpouch bigpack aed manualdefibrillator bandage shotgun morphine");

                        yield return new WaitForSecondsRealtime(1f);

                        while (!Application.isFocused) {
                            yield return new WaitForSecondsRealtime(0.1f);
                        }
                        while (true) {
                            yield return new WaitForSecondsRealtime(0.4f);

                            Together.log.l("STOPPING HEART, press Mouse3 to continue");
                            Body body = Con.GetBodyForCommand("@r", null).body;

                            body.heartRate = 0;

                            yield return new WaitForSecondsRealtime(1f);

                            while (!Input.GetKey(KeyCode.Mouse3)) {
                                yield return new WaitForSecondsRealtime(0.1f);
                            }
                            Together.log.l("DEFIBRILATION, press Mouse3 to continue");

                            body.fibrillationProgress = 90;

                            yield return new WaitForSecondsRealtime(1f);

                            while (!Input.GetKey(KeyCode.Mouse3)) {
                                yield return new WaitForSecondsRealtime(0.1f);
                            }
                        }
                    } else {
                        _ = Util.GetLocalBodyNullable();
                    }

                    break;
                }

                case "decaytest": {
                    Body body = Util.GetLocalBody();
                    if (!Plugin.cmd_start__is_client) {
                        yield return new WaitForSecondsRealtime(0.1f);

                        GameObject lp = Con.SpawnThingOnPlayer("foliagebag", body, true);
                        Con.SpawnThingOnPlayer("pizzaslice", body, true, lp);

                        yield return new WaitForSecondsRealtime(1f);
                    }

                    break;
                }

                default: {
                    break;
                }
            }
#endif
            yield return null;
        }
    }

#if DEBUG
    [HarmonyPatch(typeof(Settings), nameof(Settings.ApplyAll))]
    static class Settings_ApplyAll_MultiplayerPatch {
        static bool Prefix(Settings __instance) {
            try {
                Util.SetSetting("reswidth", Screen.width);
                Util.SetSetting("resheight", Screen.height);
                Util.SetSetting("fullscreen", Screen.fullScreen);
            } catch (Exception e) {
                log.error(e);
            }
            return true;
        }
    }
#endif
}
