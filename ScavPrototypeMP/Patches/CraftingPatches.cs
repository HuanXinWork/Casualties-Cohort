using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 拦截原版 <see cref="PlayerCamera.TryCraft"/>，将客户端/房主合成操作统一路由至 <see cref="CraftCommit"/> 权威提交管线，
/// 并提供与原版 <see cref="RecipeResult.SpawnResult"/> 严格对齐的多人指定躯体产物生成实现。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryCraft))]
public static class PlayerCamera_TryCraft_MultiplayerPatch {
    private const string TitaniumSlabItemId = "titaniumslab";
    private const string HeavyMetalAchievementId = "HEAVYMETAL";

    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning)
            return true;
        CraftCommit.TryClick(__instance);
        return false;
    }

    public static List<Item> CheckIfTheseItemsAreEnoughForTheRecipe(Recipe recipe, List<Item> items) {
        var list = new List<Item>();
        var used = new List<Item>();
        foreach (RecipeItem ingredient in recipe.items) {
            Item matchingItem = ingredient.GetMatchingItem(items, used);
            if (matchingItem == null)
                return null;
            list.Add(matchingItem);
            if (ingredient.destroyItem && !ingredient.isLiquid)
                used.Add(matchingItem);
        }
        return list;
    }

    /// <summary>
    /// 为目标玩家躯体生成合成产物（对齐原版 <see cref="RecipeResult.SpawnResult"/>：智力不足惩罚、钛板 <c>HEAVYMETAL</c> 成就触发、液体灌装及电池/空瓶初始化）。
    /// </summary>
    internal static CraftSpawn RecipeResult_SpawnResult(int recipeInt, RecipeResult rres, Body crafterBody) {
        var made = new List<GameObject>();
        try {
            if (rres == null || crafterBody == null)
                return CraftSpawn.Fail(made);

            int intDelta = crafterBody.skills.INT - recipeInt;
            float conditionMultiplier = 1f;
            if (intDelta < 0 && UnityEngine.Random.value < 0.5f) {
                switch (intDelta) {
                    case -1:
                        break;
                    case -3:
                        crafterBody.DoGoreSound();
                        for (int i = 5; i <= 8; i += 3) {
                            crafterBody.limbs[i].pain += 40f;
                            crafterBody.limbs[i].skinHealth -= 15f;
                            crafterBody.limbs[i].bleedAmount += UnityEngine.Random.Range(2f, 5f);
                        }
                        return CraftSpawn.None();
                    default:
                        return CraftSpawn.None();
                }
                conditionMultiplier = UnityEngine.Random.Range(0.2f, 0.9f);
            }

            // 仅当本次合成者为本机玩家躯体时在本地触发 HEAVYMETAL 成就（远端客户端由其本机收到 CraftReceipt 回执后独立触发，防止误给房主解锁）
            if (rres.id == TitaniumSlabItemId && Util.IsBodyLocal(crafterBody)) {
                SteamManager.TryUnlockAchievement(HeavyMetalAchievementId);
            }

            Item resultItem = null;
            for (int j = 0; j < rres.amount; j++) {
                if (rres.isLiquid) {
                    foreach (Item item in crafterBody.GetAllItemsThorough()) {
                        if (item.TryGetComponent<WaterContainerItem>(out var container)
                            && container.stack.Count == 1
                            && container.stack[0].liquidId == rres.id
                            && container.SpaceLeft >= rres.resultCondition * conditionMultiplier) {
                            container.AddLiquid(rres.id, rres.resultCondition * conditionMultiplier);
                            if (item.TryGetParentContainer(out var parentCont)
                                && (parentCont.AboveHoldingWeight() || item.totalWeight > parentCont.maxWeightPerItem)) {
                                parentCont.UnloadItem(item);
                            }
                            return CraftSpawn.Made(item);
                        }
                    }

                    GameObject bottleObj = Utils.Create("craftingbottle", crafterBody.transform.position, 0f);
                    if (bottleObj == null)
                        return CraftSpawn.Fail(made);
                    made.Add(bottleObj);
                    Item bottleItem = bottleObj.GetComponent<Item>();
                    bottleItem.condition = rres.resultCondition;
                    crafterBody.AutoPickUpItem(bottleItem);
                    bottleItem.GetComponent<WaterContainerItem>().AddLiquid(rres.id, rres.resultCondition * conditionMultiplier);
                    UnityEngine.Object.Destroy(bottleObj, 300f);
                    resultItem = bottleItem;
                } else {
                    GameObject created = Utils.Create(rres.id, crafterBody.transform.position, 0f);
                    if (created == null)
                        return CraftSpawn.Fail(made);
                    made.Add(created);
                    Item spawnedItem = created.GetComponent<Item>();
                    if (spawnedItem == null)
                        return CraftSpawn.Fail(made);
                    spawnedItem.condition = rres.resultCondition * conditionMultiplier;
                    crafterBody.AutoPickUpItem(spawnedItem);
                    if ((bool)spawnedItem.battery)
                        spawnedItem.battery.UnloadBattery(skipSpawn: true);
                    if (!rres.dontDrainResultLiquid && spawnedItem.TryGetComponent<WaterContainerItem>(out var waterCont)) {
                        waterCont.stack = new List<LiquidStack>();
                        spawnedItem.condition = 0f;
                    }
                    resultItem = spawnedItem;
                }
            }

            if (resultItem == null)
                return rres.amount <= 0 ? CraftSpawn.None() : CraftSpawn.Fail(made);
            return CraftSpawn.Made(resultItem);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Craft, nameof(RecipeResult_SpawnResult), ex);
            return CraftSpawn.Fail(made);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_TryCraft)]
    static void ReceiveTryCraftRequest(knetid clientId, NetDataReader reader) {
        uint session = reader.GetUInt();
        uint attempt = reader.GetUInt();
        ushort recipeIndex = reader.GetUShort();
        ushort[] materialItemIds = reader.GetUShortArray();
        CraftCommit.HandleServer(clientId, session, attempt, recipeIndex, materialItemIds);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CraftReceipt)]
    static void ReceiveCraftReceipt(knetid _, NetDataReader reader) {
        CraftCommit.OnReceipt(reader);
    }
}
