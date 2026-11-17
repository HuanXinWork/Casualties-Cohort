using System;
using System.Collections.Generic;
using System.Diagnostics;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 拦截 <see cref="Item.SetupItems"/>：构建全局液体字节注册表，并将依赖单人 <see cref="PlayerCamera"/> 的物品使用委托重写为多人躯体安全实现。
/// </summary>
[HarmonyPatch(typeof(Item), nameof(Item.SetupItems))]
public static class Item_SetupItems_Listener {
    public static Dictionary<string, byte> LiquidIdRegistry = new();
    public static string[] LiquidNetIdToId;

    static void Postfix() {
        if (Plugin.dump_game_ids) {
            Plugin.log.LogInfo($"\ntotal item count: {Item.GlobalItems.Count}");
            Plugin.log.LogInfo($"############### DUMPING ALL ITEM IDs START ###########################\n{string.Join("\n", Item.GlobalItems.Keys)}");
            Plugin.log.LogInfo("############### DUMPING ALL ITEM IDs END #############################\n");
        }

        LiquidIdRegistry.Clear();
        LiquidNetIdToId = new string[Liquids.Registry.Count];
        byte counter = 0;
        foreach (var entry in Liquids.Registry) {
            LiquidIdRegistry.Add(entry.Key, counter);
            LiquidNetIdToId[counter] = entry.Key;
            counter++;
        }

        Item_DoAmputate_MultiplayerPatch.SeedAmputateToolsFromGlobalItems();

        Item.GlobalItems["scrapmetal"].useAction = delegate (Body body, Item item) {
            if (!body.canPlaceBlock || item.condition < 0.24f)
                return;

            Vector2 target = body.transform.position + (body.targetLookPos - body.transform.position).normalized * 5f;
            RaycastHit2D hit = Physics2D.Linecast(body.transform.position, target, PhysicsLayers.GroundMask);
            if ((bool)hit) {
                target = hit.point + hit.normal * 0.2f;
            }
            body.armsAnimator.Play("ArmsSwing", -1, 0f);
            Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(target);
            if (WorldGeneration.world.GetBlock(blockPos) <= 0) {
                Sound.Play("scrapmetal", target);
                if (Util.IsBodyLocal(body)) {
                    WorldGeneration.world.SetBlock(blockPos, 3);
                    item.condition -= 0.25f;
                    if (ItemSync.TryGetSyncInfo(item, out var si)) {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemPlaceBlock);
                        writer.Put(si.syncId);
                        writer.Put(target);
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                    }
                }
            }
        };

        Item.GlobalItems["scaffoldingpack"].useAction = delegate (Body body, Item item) {
            if (!body.canPlaceBlock)
                return;

            Vector2 target = body.transform.position + (body.targetLookPos - body.transform.position).normalized * 5f;
            RaycastHit2D hit = Physics2D.Linecast(body.transform.position, target, PhysicsLayers.GroundMask);
            if ((bool)hit) {
                target = hit.point + hit.normal * 0.2f;
            }
            body.armsAnimator.Play("ArmsSwing", -1, 0f);
            Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(target);
            if (WorldGeneration.world.GetBlock(blockPos) <= 0) {
                Sound.Play("scrapmetal", target);
                if (Util.IsBodyLocal(body)) {
                    WorldGeneration.world.SetBlock(blockPos, 21);
                    item.condition -= 0.01f;
                    if (item.condition <= 0f) {
                        body.attackCooldown = 0.5f;
                    }
                    if (ItemSync.TryGetSyncInfo(item, out var si)) {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemPlaceBlock);
                        writer.Put(si.syncId);
                        writer.Put(target);
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                    }
                }
            }
        };

        Item.GlobalItems["mp3player"].useAction = delegate (Body body, Item item) {
            item.battery.DrainCharge(0.005f);
            if (Util.IsBodyLocal(body) && item.condition > 0f) {
                MP3Menu_UpdateList_MultiplayerPatch.last_locally_used_mp3_player = item;
                Utils.Create("Special/MP3SongSelect", PlayerCamera.main.mainCanvas.transform);
                if (PlayerCamera.main.radialOpen)
                    PlayerCamera.main.radialOpen = false;
            } else {
                Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1f);
            }
        };

        ReplaceItemUseAction("epda", (body, item, ogAction) => {
            if (body.IsBodyLocal()) {
                ogAction(body, item);
                return;
            }
            if (item.condition <= 0f)
                return;

            item.battery.DrainCharge(0.126f);
            var epda = item.GetComponent<EPdaScript>();
            if (!body.IsMindwiped()) {
                if (!body.TryGetNetBody(out _) && !epda.hasBeenRead) {
                    epda.hasBeenRead = true;
                    body.skills.AddExp(2, 35f);
                }
                var data = item.GetComponent<AnyDataContainerComponent>();
                if (data.WasntTouched("hasBeenRead" + body.GetInstanceID())) {
                    epda.hasBeenRead = true;
                    body.skills.AddExp(2, 35f);
                }
            }
            Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1f);
        });

        Item.GlobalItems["handcrank"].useAction = delegate (Body body, Item item) {
            if (Util.IsBodyLocal(body)) {
                MinigameBase.main.StartMinigame(new HandCrankMinigame(), item);
            }
        };

        ItemInfo.Use improvedAmmoUse = delegate (Body body, Item item) {
            var ammo = item.GetComponent<AmmoScript>();
            if (ammo.rounds > 0) {
                if (!Net.IsRunning || Net.IsServer) {
                    GameObject spawned = Utils.Create(AmmoScript.AmmoTypeToItem(ammo.ammoType), ammo.transform.position, 0f);
                    body.AutoPickUpItem(spawned.GetComponent<Item>());
                }
                if (!Net.IsRunning || Net.IsServer || Util.IsBodyLocal(body))
                    ammo.rounds--;
                Sound.Play("gunloadshell", item.transform.position);
            }
        };

        Item.GlobalItems["riflemagazine"].useAction = improvedAmmoUse;
        Item.GlobalItems["smallmagazine"].useAction = improvedAmmoUse;
        Item.GlobalItems["boxof12gauge"].useAction = improvedAmmoUse;

        Item.GlobalItems["roselight"].useLimbAction = delegate (Limb limb, Item item) {
            item.condition = 0f;
            if (limb.infected) {
                PlayerCamera.main.showInfection[Array.IndexOf(limb.body.limbs, limb)] = true;
                Sound.Play("goo", limb.transform.position);
            }
        };

        Item.GlobalItems["blueprint"].useAction = delegate (Body body, Item item) {
            item.condition = 0f;
            body.skills.AddExp(2, 30f);
            int recipeIndex = item.GetComponent<BlueprintScript>().recipeIndex;
            Recipe recipe = Recipes.recipes[recipeIndex];
            if (Net.IsRunning) {
                if (Net.IsServer) {
                    recipe.INT = 0;
                    string alert = Locale.GetOther("learnedrecipe").Replace("r1", Locale.GetItem(recipe.simpleName));
                    PlayerCamera.main.DoAlert(alert);
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_BlueprintThing, (knetid)recipeIndex, true, false);
                }
            } else {
                recipe.INT = 0;
                string alert = Locale.GetOther("learnedrecipe").Replace("r1", Locale.GetItem(recipe.simpleName));
                PlayerCamera.main.DoAlert(alert);
            }
            if (Util.IsBodyLocal(body)) {
                PlayerCamera.main.selectedRecipe = recipe.index;
                if (!PlayerCamera.main.craftingPanel.activeSelf) {
                    PlayerCamera.main.OpenCraftScreen();
                } else {
                    PlayerCamera.main.RefreshRecipeList();
                }
            }
            Sound.Play(VanillaAssetKeys.SoundCombine, item.transform.position);
        };

        Item.GlobalItems["terrainscanner"].useAction = delegate (Body body, Item item) {
            if (item.condition > 0.01f) {
                if (Util.IsBodyLocal(body)) {
                    if ((bool)ScannerScript.main) {
                        UnityEngine.Object.Destroy(ScannerScript.main.gameObject);
                    }
                    (UnityEngine.Object.Instantiate(Resources.Load("Special/ScannerUI"), PlayerCamera.main.mainView.transform) as GameObject)
                        .GetComponent<ScannerScript>().pos = item.transform.position;
                } else {
                    Util.PlayWorldSoundOnScreenIfInRange("flashlighttoggle", item.transform.position, 0.7f, 1f);
                }
                item.battery.DrainCharge(0.02f);
            }
        };

        Item.GlobalItems["sleepingbag"].useAction = delegate (Body body, Item item) {
            if (Multiplayer.rules.DisableSleep) {
                if (body.IsBodyLocal()) {
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
                    Util.DoAlert(Lang.Get("sleep_is_disabled"));
                }
                return;
            }

            Body_get_canTakeNap_MultiplayerPatch.force = true;
            if (body.canTakeNap && !body.usingSleepingBag) {
                item.condition -= 0.0501f;
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
                body.TakeANap();
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;
                if (body.IsBodyLocal() && ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.TryGetNetBody(out var nb)) {
                    nb.SetNetHealthSyncIgnoreTime(1f);
                }

                body.usingSleepingBag = true;
                RaycastHit2D hit = Physics2D.Raycast(body.transform.position, Vector2.down, 100f, PhysicsLayers.GroundMask);
                if ((bool)hit) {
                    GameObject sleepBag = Utils.Create("Special/sleepingbaguse", hit.point + Vector2.up, 0f);
                    UnityEngine.Object.Destroy(sleepBag, 10f);
                }
            } else if (body.IsBodyLocal()) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            }
        };

        ModLog.Flow(LogDomain.Item, "Item_SetupItems_Listener", $"Patched {Item.GlobalItems.Count} items and {Liquids.Registry.Count} liquids");
    }

    public delegate void ReplacementItemUse(Body body, Item item, ItemInfo.Use originalUse);

    public static void ReplaceItemUseAction(string itemId, ReplacementItemUse action) {
        if (!Item.GlobalItems.TryGetValue(itemId, out var info)) {
            ModLog.Error($"ReplaceItemUseAction: Item \"{itemId}\" not found!\n{new StackTrace()}", LogDomain.Item);
            return;
        }
        ItemInfo.Use original = info.useAction;
        info.useAction = (b, i) => {
            try {
                if (action != null) {
                    action(b, i, original);
                } else {
                    original(b, i);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Item, $"ReplaceItemUseAction({itemId})", ex);
            }
        };
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueprintThing)]
    static void ReceiveBlueprintLearned(knetid _, NetDataReader reader) {
        reader.Get(out knetid recipeIndex);
        Recipe recipe = Recipes.recipes[(int)recipeIndex];
        recipe.INT = 0;
        string alert = Locale.GetOther("learnedrecipe").Replace("r1", Locale.GetItem(recipe.simpleName));
        PlayerCamera.main.DoAlert(alert);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemPlaceBlock)]
    static void ReceiveItemPlaceBlock(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid itemSyncId);
        reader.Get(out Vector2 placePos);

        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out var body) || !body.conscious)
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(itemSyncId, out var si) || !si.IsItem() || !ItemSync.CheckIfBodyReachThisItem(si, body))
            return;

        Item item = si.item;
        bool isScrapMetal = item.id == "scrapmetal";
        bool isScaffolding = item.id == "scaffoldingpack";
        if (!isScrapMetal && !isScaffolding)
            return;

        Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(placePos);
        if (WorldGeneration.world.GetBlock(blockPos) <= 0) {
            if (isScrapMetal) {
                WorldGeneration.world.SetBlock(blockPos, 3);
                item.condition -= 0.25f;
            } else {
                WorldGeneration.world.SetBlock(blockPos, 21);
                item.condition -= 0.01f;
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(placePos, $"S: ItemPlaceBlock {si}");
        }
    }
}
