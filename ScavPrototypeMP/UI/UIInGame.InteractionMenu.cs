using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using Together.Api;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 局内右键队友弹出的玩家交互菜单（内置医疗/背包/喂食/拥抱/背负/推搡/摸头 + 跨模组 <see cref="TogetherPanelApi.RegisteredPlayerActions"/> 扩展按钮）。
    /// </summary>
    public partial class UIInGame {
        public static NetBody interaction_menu_target_body = null;
        public static Vector2 interaction_menu_target_pos = Vector2.zero;
        public static bool interaction_menu_focused = false;

        public static bool DoPlayerInteractionMenuButton(in Rect rect, bool enabled, in string text, ref bool focused, bool continue_drag_item = false, bool accept_drag_item = false) {
            GUIStyle style = FixGuiStyle.Copy(GUI.skin.button, 6);
            GUI.enabled = enabled;

            bool result = UIBullshit._GUI_FocusableButton(rect, text, out focused, style) && enabled;

            if (focused) {
                if (enabled && PlayerCamera.main.dragItem != null && !Util.IsInWoundView()) {
                    if (continue_drag_item) {
                        return true;
                    }
                    if (accept_drag_item && Input.GetKeyUp(KeyBinds.GetBind("iteminteract"))) {
                        return true;
                    }
                }
                interaction_menu_focused = true;
            }

            return result;
        }

        private static void _GUI_PlayerInteractionGui() {
            if (Net.IsDedicatedServer || interaction_menu_target_body == null || interaction_menu_target_body.body == null) {
                return;
            }

            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me == null || me.body == null) {
                return;
            }

            float uisc = UIBullshit.uiScale;
            float buttonXSize = 100f * uisc;
            float buttonYSize = 45f * uisc;

            IReadOnlyList<TogetherPanelApi.ExternalPlayerAction> extActions = TogetherPanelApi.RegisteredPlayerActions;
            int extActionCount = extActions != null ? extActions.Count : 0;
            int extraRows = (extActionCount + 3) / 4;

            float menuFullSizeX = buttonXSize * 4f;
            float menuFullSizeY = buttonYSize * (2.5f + extraRows);

            Vector2 targetWorldPos = (Vector2)interaction_menu_target_body.body.transform.position + Vector2.up * 6f;
            Vector2 targetScreenPos = (Vector2)Camera.main.WorldToScreenPoint(targetWorldPos);
            targetScreenPos = new Vector2(targetScreenPos.x, Screen.height - targetScreenPos.y);

            if (interaction_menu_target_pos == Vector2.zero) {
                interaction_menu_target_pos = targetScreenPos;
            }
            interaction_menu_target_pos = new Vector2(
                Mathf.Clamp(interaction_menu_target_pos.x, menuFullSizeX, Screen.width - menuFullSizeX),
                Mathf.Clamp(interaction_menu_target_pos.y, menuFullSizeY, Screen.height - menuFullSizeY));

            Vector2 pos = interaction_menu_target_pos;

            int ogFontSize = GUI.skin.button.fontSize;
            int ogLabelFontSize = GUI.skin.label.fontSize;
            try {
                float scaledFontSize = (UIBullshit.DEFAULT_RETROGAME_FONTSIZE * 0.6f) * UIBullshit.uiScale * UIBullshit.CUSTOM_UI_SCALE_FOR_UIBULLSHIT;
                GUI.skin.button.fontSize = Mathf.RoundToInt(scaledFontSize);
                GUI.skin.label.fontSize = GUI.skin.button.fontSize;

                float curButtonX = 0f;
                float curButtonY = 0f;
                bool focused = false;
                string tooltip = null;

                Rect GetNextRect() {
                    return new Rect(
                        pos.x + buttonXSize * (-2f + curButtonX),
                        pos.y + buttonYSize * (-1f + curButtonY),
                        buttonXSize,
                        buttonYSize);
                }

                // 1. 查看伤势
                if (DoPlayerInteractionMenuButton(GetNextRect(), true, Lang.Get("plrint_woundview"), ref focused, true)) {
                    if (Util.QuickRaycastInteractionCheck(me.body.transform.position, interaction_menu_target_body.body.transform.position, true)) {
                        PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(interaction_menu_target_body.body);
                    } else {
                        PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                    }
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = "plrint_woundview_tooltip";
                }
                curButtonX += 1f;

                // 2. 给予物品 / 打开背包
                Item dragItem = PlayerCamera.main.dragItem;
                bool canGiveItem = dragItem != null;
                bool canAccessInv = Multiplayer.rules.NoInventoryLock || !interaction_menu_target_body.body.conscious || canGiveItem;
                if (DoPlayerInteractionMenuButton(GetNextRect(), canAccessInv, canGiveItem ? Lang.Get("plrint_give") : Lang.Get("plrint_inventory"), ref focused, false, true)) {
                    if (canGiveItem) {
                        ClientMain._PLRINT_GiveItem(interaction_menu_target_body, dragItem);
                        PlayerCamera.main.dragItem = null;
                    } else {
                        ClientMain._PLRINT_Inventory(interaction_menu_target_body);
                    }
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused && !canGiveItem) {
                    tooltip = canAccessInv ? "plrint_inv_allowed" : "plrint_inv_no";
                }
                curButtonX += 1f;

                // 3. 沙丘种啮咬吸食 / 拖拽物品喂食使用
                bool localIsDune = me.body != null && me.body.charType == 2;
                bool showDuneFeedOn = localIsDune && dragItem == null;
                if (showDuneFeedOn) {
                    bool canBite = interaction_menu_target_body.IsPlayer
                        && interaction_menu_target_body.body != null
                        && interaction_menu_target_body.body.alive
                        && interaction_menu_target_body != me.netBody;
                    if (DoPlayerInteractionMenuButton(GetNextRect(), canBite, Lang.Get("plrint_feed_on"), ref focused)) {
                        me.netBody.Bite(interaction_menu_target_body);
                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = canBite ? "plrint_feed_on_tooltip" : "plr_dead";
                    }
                    curButtonX += 1f;
                } else {
                    bool canFeed = dragItem != null
                        && (dragItem.Stats.usable || dragItem.Stats.wearable)
                        && ClientMain.ItemCanBeUsedOnThisBody(dragItem, interaction_menu_target_body.body)
                        && interaction_menu_target_body.body.alive;
                    string useLabel = !canFeed
                        ? Lang.Get("plrint_useitem")
                        : (dragItem.Stats.wearable
                            ? Lang.Get("plrint_useitem_wearable")
                            : (dragItem.IsEdible()
                                ? Lang.Get("plrint_useitem_food")
                                : (dragItem.Stats.category == "water" ? Lang.Get("plrint_useitem_water") : Lang.Get("plrint_useitem"))));

                    if (DoPlayerInteractionMenuButton(GetNextRect(), canFeed, useLabel, ref focused, false, true)) {
                        Body targetBody = interaction_menu_target_body.body;
                        if (!MedicalSync.IsRefusingHelp(targetBody)) {
                            if (ItemSync.TryGetSyncInfo(dragItem, out SyncInfo si)) {
                                si.SetIgnoreTimeForRoundTrip();
                                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestUseItem, si.syncId, interaction_menu_target_body.netId, true);
                            }
                            if (Multiplayer.rules.ConsentForFeeding && targetBody.conscious) {
                                PlayerCamera.main.DoAlert(string.Format(Lang.Get("plrint_feeditem_offered"), interaction_menu_target_body.bodyName, PlayerCamera.ItemHoverDescription(dragItem).Item1));
                            } else if (Net.IsClient) {
                                ItemSync.BetterUseItem(targetBody, dragItem);
                            }
                        } else {
                            PlayerCamera.main.DoAlert(Lang.Get("deny_refuse"));
                        }

                        PlayerCamera.main.dragItem = null;
                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = !interaction_menu_target_body.body.alive
                            ? "plr_dead"
                            : (canFeed ? "plrint_feed_item" : "plrint_drag_item_here");
                    }
                    curButtonX += 1f;
                }

                bool targetIsCarryingMe = interaction_menu_target_body.piggybacking_on == me.netBody || interaction_menu_target_body.carrying_person == me.netBody;

                // 4. 拥抱
                bool canHug = !targetIsCarryingMe
                    && interaction_menu_target_body.IsPlayer
                    && interaction_menu_target_body.body != null
                    && interaction_menu_target_body.body.alive
                    && interaction_menu_target_body.body.conscious;
                if (DoPlayerInteractionMenuButton(GetNextRect(), canHug, Lang.Get("plrint_hug"), ref focused)) {
                    me.netBody.Hug(interaction_menu_target_body);
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = "plrint_hug_tooltip";
                }

                // 第二行：背负、骑乘、推人、摸头
                curButtonX = 0f;
                curButtonY += 1f;

                if (Multiplayer.rules.AllowPiggyback) {
                    bool canCarry = interaction_menu_target_body.CanBeCarriedBySomeone()
                        && !targetIsCarryingMe
#if FULLGAME
                        && (me.netBody == null || !MacroFleshRebuilderMp.BlocksCarry(me.netBody))
#endif
                        ;
                    if (DoPlayerInteractionMenuButton(GetNextRect(), canCarry, Lang.Get("plrint_carry"), ref focused)) {
                        ClientMain._PLRINT_Carry(interaction_menu_target_body);
                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = canCarry ? "plrint_carry_allow" : "plrint_carry_no";
                    }
                    curButtonX += 1f;

                    bool canPiggyback = interaction_menu_target_body.IsPiggybackable()
                        && interaction_menu_target_body.body.overEncumberance < 0.5f
                        && !targetIsCarryingMe;
                    if (DoPlayerInteractionMenuButton(GetNextRect(), canPiggyback, Lang.Get("plrint_piggyback"), ref focused)) {
                        ClientMain._PLRINT_Piggyback(interaction_menu_target_body);
                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = canPiggyback ? "plrint_piggyback_allow" : "plrint_piggyback_no";
                    }
                    curButtonX += 1f;
                }

                if (Multiplayer.rules.AllowPush) {
                    bool canPush = !targetIsCarryingMe;
                    if (DoPlayerInteractionMenuButton(GetNextRect(), canPush, Lang.Get("plrint_push"), ref focused)) {
                        me.netBody.Push(interaction_menu_target_body);
                        StopPlayerInteractionMenu();
                        return;
                    }
                    if (focused) {
                        tooltip = "plrint_push_tooltip";
                    }
                    curButtonX += 1f;
                }

                bool canPat = !targetIsCarryingMe
                    && interaction_menu_target_body.IsPlayer
                    && interaction_menu_target_body.body != null
                    && interaction_menu_target_body.body.alive
                    && interaction_menu_target_body.body.conscious;
                if (DoPlayerInteractionMenuButton(GetNextRect(), canPat, Lang.Get("plrint_pat"), ref focused)) {
                    me.netBody.Pat(interaction_menu_target_body);
                    StopPlayerInteractionMenu();
                    return;
                }
                if (focused) {
                    tooltip = "plrint_pat_tooltip";
                }
                curButtonX += 1f;

                // 动态追加外部模组通过 TogetherPanelApi 注册的玩家交互动作按钮
                if (extActionCount > 0) {
                    Body targetBody = interaction_menu_target_body.body;
                    for (int i = 0; i < extActionCount; i++) {
                        TogetherPanelApi.ExternalPlayerAction extAction = extActions[i];
                        if (extAction == null) {
                            continue;
                        }

                        string extLabel;
                        bool extEnabled;
                        try {
                            extLabel = extAction.LabelProvider != null ? (extAction.LabelProvider(targetBody) ?? extAction.Id) : extAction.Id;
                            extEnabled = extAction.CanExecute == null || extAction.CanExecute(targetBody);
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.Interop, $"ExternalPlayerAction.Query({extAction.Id})", ex);
                            continue;
                        }

                        if (curButtonX >= 4f) {
                            curButtonX = 0f;
                            curButtonY += 1f;
                        }

                        if (DoPlayerInteractionMenuButton(GetNextRect(), extEnabled, extLabel, ref focused)) {
                            try {
                                extAction.Execute?.Invoke(targetBody);
                            } catch (Exception ex) {
                                ModLog.Exception(LogDomain.Interop, $"ExternalPlayerAction.Execute({extAction.Id})", ex);
                            }
                            StopPlayerInteractionMenu();
                            return;
                        }
                        curButtonX += 1f;
                    }
                }

                GUI.enabled = true;

                // 顶部玩家名称与网络 ID 铭牌
                Rect nametagRect = new Rect(pos.x - buttonXSize * 2f, pos.y - buttonYSize * 1.5f, buttonXSize * 4f, buttonYSize * 0.5f);
                UIBullshit._GUI_9SlicePanel(nametagRect, 1f, 0.8f, true, UIBullshit.unscaled_uiBlockNano);

                Color ogColor = GUI.skin.label.normal.textColor;
                TextAnchor ogAlign = GUI.skin.label.alignment;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                try {
                    string headerText;
                    if (interaction_menu_target_body.IsPlayer) {
                        ScavPlayer targetPlr = interaction_menu_target_body.plr;
                        GUI.skin.label.normal.textColor = targetPlr.playerColor;
                        headerText = interaction_menu_target_body.playerName + "  ID:" + targetPlr.playerId;
                    } else {
                        headerText = interaction_menu_target_body.bodyName + "  ID:" + interaction_menu_target_body.netId;
                        if (log.verbose) {
                            headerText += " (NPC)";
                        }
                    }
                    GUI.Label(nametagRect, headerText);
                } finally {
                    GUI.skin.label.normal.textColor = ogColor;
                    GUI.skin.label.alignment = ogAlign;
                }

                if (tooltip != null) {
                    UIBullshit._GUI_SetTooltip(Lang.Get(tooltip), null);
                }
            } finally {
                GUI.skin.button.fontSize = ogFontSize;
                GUI.skin.label.fontSize = ogLabelFontSize;
                GUI.enabled = true;
            }
        }

        public static void StartPlayerInteractionMenu(NetBody target) {
            if (target == null || target == interaction_menu_target_body || target.body == null) {
                return;
            }
            if (PlayerCamera.main == null || PlayerCamera.main.body == null || !PlayerCamera.main.body.conscious || SpectatorModeActive) {
                return;
            }

            float maxDistance = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance);
            if (!KM.DistanceCheck(PlayerCamera.main.body, target.body, maxDistance)) {
                PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
                return;
            }
            if (!Util.AccurateRaycastInteractionCheckObstruction(PlayerCamera.main.body, target.body, true)) {
                PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                return;
            }

            PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            interaction_menu_target_body = target;
        }

        public static void StopPlayerInteractionMenu() {
            interaction_menu_target_pos = Vector2.zero;
            if (interaction_menu_target_body != null) {
                interaction_menu_target_body = null;
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Close);
                }
            }
            interaction_menu_focused = false;
        }
    }
}
