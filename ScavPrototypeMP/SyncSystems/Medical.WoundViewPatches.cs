using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 修复健康面板肢体悬停高亮固定读取本地躯体而非当前检视目标躯体的缺陷。
/// </summary>
[HarmonyPatch(typeof(WoundViewLimb), nameof(WoundViewLimb.OnPointerEnter))]
public static class WoundViewLimb_OnPointerEnter_MultiplayerPatch {
    private static bool Prefix(WoundViewLimb __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (__instance.woundview == null || __instance.woundview.body == null) {
            return false;
        }
        if (!__instance.woundview.body.limbs[__instance.limb].dismembered) {
            __instance.woundview.limbLookingAt = __instance.limb;
            __instance.woundview.limbImageFlash[__instance.limb] = 1f;
        }
        return false;
    }
}

/// <summary>
/// 在联机模式下将健康面板顶栏时间条周期切换展示存活人数与防分散计数。
/// </summary>
[HarmonyPatch(typeof(WoundView), nameof(WoundView.LayerTimerDisplay))]
internal static class WoundView_HLayerTimerDisplay_MultiplayerPatch {
    private static void Postfix(WoundView __instance) {
        if (!Net.IsRunning) {
            return;
        }

        if ((int)(Time.time * 0.5f) % 2 == 0) {
            __instance.timeTextBar.text = $"ALL:{ScavPlayer.BodyToPlayerDict.Count:D2}";
            if (Multiplayer.rules.TrackPlayerDeaths) {
                __instance.timeTextBar.text += $" L:{ScavPlayer.AllLivingPlayers.Count:D2}";
            }
            if (Multiplayer.rules.PlayerScatterDisallowed && Multiplayer.rules.ScatterMinGroupSize > 0 && UIInGame.main != null) {
                __instance.timeTextBar.text += $" V:{UIInGame.main.antidispersion_cur_range_count:D2}";
            }
        }
    }
}

/// <summary>
/// 同步检视他人伤势时的感染状态遮罩、CPR 按钮可用性，以及协同施救者头像、拖拽图标与远端鼠标光标。
/// </summary>
[HarmonyPatch(typeof(WoundView), nameof(WoundView.UpdateView))]
public static class WoundView_UpdateView_MultiplayerPatch {
    internal static bool[] OG_showInfection = null;

    public static GameObject CPRButton => PlayerCamera_ToggleWoundView_MultiplayerPatch.CPRButton;

    internal static HashSet<ScavPlayer> lastHealerList = new HashSet<ScavPlayer>();
    internal static List<GameObject> healerLimbTagIcons = new List<GameObject>();
    internal static GameObject healerListUIElement = null;

    private static List<Sprite> cursorSprites;
    private static readonly Dictionary<ScavPlayer, Image> playerCursorSprites = new Dictionary<ScavPlayer, Image>();
    private static readonly Dictionary<ScavPlayer, Image> dragItemSprites = new Dictionary<ScavPlayer, Image>();
    private static readonly List<ScavPlayer> ReusableLimbHealers = new List<ScavPlayer>();

    private static bool Prefix(WoundView __instance) {
        if (__instance.body == null) {
            __instance.body = Util.GetLocalBodyNullable();
            return false;
        }

        if (OG_showInfection == null && PlayerCamera.main != null) {
            OG_showInfection = PlayerCamera.main.showInfection;
        }

        if (!Net.IsRunning) {
            return true;
        }

        if (__instance.body.TryGetNetBody(out NetBody npc) && !npc.IsBodyLocal() && PlayerCamera.main != null) {
            PlayerCamera.main.showInfection = npc.localoverride_showInfection;
            for (int i = 0; i < PlayerCamera.main.showInfection.Length && i < __instance.body.limbs.Length; i++) {
                if (!__instance.body.limbs[i].infected) {
                    PlayerCamera.main.showInfection[i] = false;
                }
            }
        }

        if (CPRButton != null) {
            CheckIfCPRButtonShouldBeClickable(__instance);
        }

        return true;
    }

    private static void CheckIfCPRButtonShouldBeClickable(WoundView ww) {
        Image cprImage = CPRButton.GetComponent<Image>();
        UITooltip tooltip = CPRButton.GetComponent<UITooltip>();
        if (cprImage == null || tooltip == null) {
            return;
        }

        if (CPRHandler.CheckIfBodyIsNotOkAndNeedCPR(ww.body)) {
            if (CPRHandler.IsCPRBeingPerformedOnThisBody_IsMeOrNobody(ww.body)) {
                cprImage.color = Color.white;
                tooltip.tipDesc = ww.body.alive ? Lang.Get("wv_cprdesc") : Lang.Get("plr_dead");
                return;
            }
            tooltip.tipDesc = Lang.Get("wv_cprdesc_occupied");
        } else {
            tooltip.tipDesc = Lang.Get("wv_cprdesc_no");
        }
        cprImage.color = Color.gray;
    }

    private static void Postfix(WoundView __instance) {
        if (__instance.body == null) {
            return;
        }

        if (OG_showInfection != null && PlayerCamera.main != null) {
            PlayerCamera.main.showInfection = OG_showInfection;
        }

        if (!__instance.body.IsBodyLocal() && __instance.miseryText != null) {
            __instance.miseryText.gameObject.SetActive(false);
        }

        UpdateFriendlyHealers(__instance);
    }

    internal static void ClearHealerUI() {
        for (int i = 0; i < healerLimbTagIcons.Count; i++) {
            if (healerLimbTagIcons[i] != null) {
                UnityEngine.Object.Destroy(healerLimbTagIcons[i]);
            }
        }
        healerLimbTagIcons.Clear();

        if (healerListUIElement != null) {
            UnityEngine.Object.Destroy(healerListUIElement);
            healerListUIElement = null;
        }

        foreach (KeyValuePair<ScavPlayer, Image> item in playerCursorSprites) {
            if (item.Value != null) {
                UnityEngine.Object.Destroy(item.Value.gameObject);
            }
        }
        playerCursorSprites.Clear();
        dragItemSprites.Clear();
    }

    private static void UpdateFriendlyHealers(WoundView wv) {
        NetBody viewTarget;
        if (wv.checkTime == 0) {
            for (int i = 0; i < healerLimbTagIcons.Count; i++) {
                if (healerLimbTagIcons[i] != null) {
                    UnityEngine.Object.Destroy(healerLimbTagIcons[i]);
                }
            }
            healerLimbTagIcons.Clear();

            if (wv.body != null && wv.body.TryGetNetBody(out viewTarget) && viewTarget.syncBody != null) {
                knetid targetId = viewTarget.netId;
                bool allowDeadViewers = Multiplayer.rules.DeadTextchat;
                var healersSet = new HashSet<ScavPlayer>();
                foreach (ScavPlayer candidate in ScavPlayer.AllLivingPlayers) {
                    if (candidate != null && candidate.WoundviewTargetNetBodyId == targetId && (allowDeadViewers || candidate.IsAlive())) {
                        healersSet.Add(candidate);
                    }
                }

                if (!healersSet.SetEquals(lastHealerList)) {
                    lastHealerList = healersSet;
                    GeneratePlayerList(wv, viewTarget);
                }

                if (healersSet.Count > 0) {
                    Sprite happyFace = wv.moodIcons[1];
                    int headSiblingIndex = wv.limbImages[(int)LimbIndex.Head].transform.parent.GetSiblingIndex() + 1;
                    int siblingIndex = headSiblingIndex + 1 + wv.iconObj.Count;

                    try {
                        for (int k = 0; k < viewTarget.body.limbs.Length; k++) {
                            Limb limb = viewTarget.body.limbs[k];
                            ReusableLimbHealers.Clear();
                            foreach (ScavPlayer healer in healersSet) {
                                if (healer.IsInMinigame && healer.minigame_targetobject.TryGetLimb(out Limb l) && l == limb) {
                                    ReusableLimbHealers.Add(healer);
                                }
                            }
                            if (ReusableLimbHealers.Count == 0) {
                                continue;
                            }

                            Vector2 offCenterPos = new Vector2(17f, -17f) + (Vector2)wv.limbImages[k].transform.position;
                            float imgLerp = Mathf.Max(0.2f, wv.limbImageLerp[k]);
                            for (int m = 0; m < ReusableLimbHealers.Count; m++) {
                                ScavPlayer plr = ReusableLimbHealers[m];
                                float offset = (m * 50f - (ReusableLimbHealers.Count - 1) * 25f) * (imgLerp * 0.8f + 0.2f) * 0.5f;
                                Vector2 pos = new Vector2(offset, offset * 0.24f) + offCenterPos;

                                Image uiIcon = wv.GenerateIcon(pos, happyFace);
                                Sprite ico = plr.IconSmallSprite;
                                if (ico != null) {
                                    uiIcon.sprite = ico;
                                } else {
                                    uiIcon.sprite = CoopModAssets.happy;
                                    uiIcon.color = plr.playerColor;
                                }
                                uiIcon.name = $"FriendHeal {limb.fullName} {plr}";
                                uiIcon.rectTransform.SetSiblingIndex(siblingIndex);
                                healerLimbTagIcons.Add(uiIcon.gameObject);
                            }
                        }
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.Medical, nameof(UpdateFriendlyHealers), ex);
                    }
                }
            } else {
                if (healerListUIElement != null) {
                    UnityEngine.Object.Destroy(healerListUIElement);
                    healerListUIElement = null;
                }
                lastHealerList.Clear();
            }
        }

        if (wv.body != null && wv.body.TryGetNetBody(out viewTarget)) {
            UpdateDragItems(wv, viewTarget);
        }
    }

    private static void UpdateDragItems(WoundView wv, NetBody viewTarget) {
        foreach (KeyValuePair<ScavPlayer, Image> pair in dragItemSprites) {
            if (pair.Value == null) {
                continue;
            }

            Sprite dragItemSprite = null;
            Item item = null;
            ScavPlayer plr = pair.Key;
            if (plr != null) {
                if (plr.IsInMinigame && plr.minigame_targetobject.TryGetNetBody(out NetBody minigameTargetNb) && minigameTargetNb == viewTarget) {
                    item = plr.minigame_currentItem;
                    if (item != null) {
                        dragItemSprite = item.GetSprite();
                    }
                } else if (ItemSync.TryGetItem(plr.DragItemId, out _, out item) && item != null) {
                    dragItemSprite = item.GetSprite();
                }
            }

            pair.Value.gameObject.SetActive(dragItemSprite != null);
            if (pair.Value.gameObject.activeSelf) {
                pair.Value.sprite = dragItemSprite;
                pair.Value.SetNativeSize();
                pair.Value.rectTransform.sizeDelta = PlayerCamera.ImageSizeDelta(dragItemSprite.texture, dragItemSprite.pixelsPerUnit, 30);

                if (item != null) {
                    var tt = PlayerCamera.ItemHoverDescription(item);
                    UITooltip uiTooltip = pair.Value.gameObject.GetOrAddComponent<UITooltip>();
                    uiTooltip.skipLocale = true;
                    uiTooltip.tipName = tt.Item1;
                    uiTooltip.tipDesc = tt.Item2;
                }
            }
        }

        if (PlayerCamera.main == null || PlayerCamera.main.mainCanvas == null) {
            return;
        }
        if (cursorSprites == null) {
            cursorSprites = PlayerCamera.main.cursors.Select(t => Util.CreateSprite(t)).ToList();
        }

        CanvasScaler scaler = PlayerCamera.main.mainCanvas.GetComponent<CanvasScaler>();
        if (scaler == null) {
            return;
        }

        foreach (KeyValuePair<ScavPlayer, Image> pair in playerCursorSprites) {
            if (pair.Value == null) {
                continue;
            }
            ScavPlayer plr = pair.Key;
            Image dragImage = pair.Value;
            Image cursorObj = dragImage.transform.GetChild(0).GetComponent<Image>();
            if (plr != null && !plr.IsInMinigame) {
                cursorObj.gameObject.SetActive(true);

                if (dragItemSprites.TryGetValue(plr, out Image dragItem) && dragItem != null && dragItem.gameObject.activeSelf && dragItem.sprite != null) {
                    dragImage.color = new Color(1f, 1f, 1f, 0.8f);
                    dragImage.sprite = dragItem.sprite;
                    dragImage.rectTransform.sizeDelta = PlayerCamera.ImageSizeDelta(dragImage.sprite.texture, 5f, 128f);
                } else {
                    dragImage.color = new Color(0f, 0f, 0f, 0f);
                }

                int cursorId = 3;
                if (plr.DragItemId != 0) {
                    cursorId = 7;
                } else if (plr.ButtonAttack || plr.ButtonIteminteract) {
                    cursorId = 9;
                }

                cursorObj.sprite = cursorSprites[cursorId];
                cursorObj.rectTransform.offsetMin = Vector2.zero;
                cursorObj.SetNativeSize();
                cursorObj.rectTransform.sizeDelta *= 0.8f;

                Vector2 pos = plr.CursorScreenPos;
                Vector2 halfRes = scaler.referenceResolution * 0.495f;
                dragImage.rectTransform.anchoredPosition = (pos * 0.5f * scaler.referenceResolution.y).Clamp(-halfRes, halfRes);
            } else {
                cursorObj.gameObject.SetActive(false);
                dragImage.color = new Color(0f, 0f, 0f, 0f);
            }
        }
    }

    private static void GeneratePlayerList(WoundView wv, NetBody viewTarget) {
        foreach (KeyValuePair<ScavPlayer, Image> item in playerCursorSprites) {
            if (item.Value != null) {
                UnityEngine.Object.Destroy(item.Value.gameObject);
            }
        }
        playerCursorSprites.Clear();

        if (healerListUIElement != null) {
            UnityEngine.Object.Destroy(healerListUIElement);
            healerListUIElement = null;
        }
        dragItemSprites.Clear();

        int headSiblingIndex = wv.limbImages[(int)LimbIndex.Head].transform.parent.GetSiblingIndex() + 1;
        bool haveLocal = ScavPlayer.TryGetLocalNetBody(out NetBody localNetBody);
        bool canPush = haveLocal && localNetBody.body.conscious && Multiplayer.rules.AllowPush;
        bool canHug = haveLocal && localNetBody.body.alive && localNetBody.body.conscious;
        Color transparentWhite = new Color(1f, 1f, 1f, 0.6f);

        UIManager.CreateBlock(wv, "healerListUIElement", out healerListUIElement, out RectTransform listRootRt, out Image listRootImage);
        listRootRt.pivot = new Vector2(0f, 1f);
        listRootRt.offsetMax = Vector2.zero;
        listRootRt.offsetMin = Vector2.zero;
        listRootImage.color = transparentWhite;
        listRootRt.anchoredPosition = new Vector2(0.6f, 0.5f);
        listRootRt.anchorMin = new Vector2(0.6f, 0.5f);
        listRootRt.sizeDelta = new Vector2(600f, 40f);
        healerListUIElement.transform.SetSiblingIndex(headSiblingIndex);

        UIManager.CreateLabel(healerListUIElement.transform, "wv_viewers", null, null, out _, out _, out _);

        int rowIndex = 0;
        foreach (ScavPlayer plr in lastHealerList) {
            if (viewTarget.IsLocal && plr.IsLocal) {
                continue;
            }

            UIManager.CreateBlock(healerListUIElement.transform, $"healerListUIElementPlayer_{plr.playerId}", out _, out RectTransform rowRt, out Image rowImg);
            rowRt.pivot = new Vector2(0f, 1f);
            rowRt.offsetMax = Vector2.zero;
            rowRt.offsetMin = Vector2.zero;
            rowImg.color = transparentWhite;
            rowRt.anchorMax = new Vector2(1f, -rowIndex);
            rowRt.anchorMin = new Vector2(0f, -(rowIndex + 1));
            rowRt.sizeDelta = Vector2.zero;

            UIManager.CreateLabel(rowRt, rowRt.name, null, null, out _, out TextMeshProUGUI plrText, out _);
            plrText.text = plr.playerName;
            plrText.color = plr.playerColor;
            plrText.richText = false;
            CharStatusVisuals.TryMakeNametagFancy(plrText, plr);
            CharStatusVisuals.FancyNametagUpdateColor(plrText, plr.playerColor);

            Sprite icon = plr.IconMediumSprite;
            if (icon != null) {
                UIManager.CreateBlock(rowRt, "plrIcon", out _, out RectTransform iconRt, out Image iconImg);
                iconImg.sprite = icon;
                iconRt.anchorMin = new Vector2(0.98f, 0.5f);
                iconRt.anchorMax = iconRt.anchorMin;
                iconImg.SetNativeSize();
                iconRt.pivot = new Vector2(1f, 0.5f);
                iconRt.sizeDelta = new Vector2(rowRt.rect.height, rowRt.rect.height) * 0.8f;
            }

            UIManager.CreateBlock(rowRt, $"dragIcon_{plr.playerId}", out _, out RectTransform dragIconRt, out Image dragIconImg);
            dragIconImg.sprite = null;
            dragIconRt.anchorMin = new Vector2(0.02f, 0.5f);
            dragIconRt.anchorMax = dragIconRt.anchorMin;
            dragIconImg.SetNativeSize();
            dragIconRt.pivot = new Vector2(0f, 0.5f);
            dragIconRt.sizeDelta = new Vector2(rowRt.rect.height, rowRt.rect.height) * 0.7f;
            dragItemSprites[plr] = dragIconImg;

            if (!plr.IsLocal) {
                UIManager.CreateBlock(wv, $"dragItem_{plr.playerId}", out _, out RectTransform cursorHostRt, out Image cursorHostImg);
                cursorHostImg.sprite = null;
                cursorHostImg.color = new Color(0f, 0f, 0f, 0f);
                cursorHostImg.raycastTarget = false;
                cursorHostRt.offsetMax = new Vector2(40f, 40f);
                cursorHostRt.offsetMin = -cursorHostRt.offsetMax;
                cursorHostRt.anchorMin = Vector2.one * 0.5f;
                cursorHostRt.anchorMax = Vector2.one * 0.5f;
                cursorHostRt.pivot = Vector2.one * 0.5f;
                cursorHostImg.SetNativeSize();

                UIManager.CreateBlock(cursorHostRt, "playerCursor", out _, out RectTransform cursorRt, out Image cursorImg);
                cursorImg.sprite = null;
                cursorImg.color = plr.playerColor.ToColorWithAlpha(0.8f);
                cursorImg.raycastTarget = false;
                cursorRt.offsetMin = Vector2.zero;
                cursorRt.offsetMax = new Vector2(40f, 40f);
                cursorRt.anchorMin = new Vector2(0.5f, 0.25f);
                cursorRt.anchorMax = cursorRt.anchorMin;
                cursorRt.pivot = new Vector2(0f, 1f);
                cursorImg.SetNativeSize();

                playerCursorSprites[plr] = cursorHostImg;
            }

            if (viewTarget.IsLocal && canPush && !plr.IsLocal) {
                UIManager.CreateButton(rowRt, "plrint_push", null, null, out _, out TextMeshProUGUI pushText, out RectTransform pushRt, out Button pushBtn, out Image pushImg);
                pushImg.sprite = UIManager.uiBlockNano;
                pushImg.color = transparentWhite;
                pushRt.anchorMin = new Vector2(0.99f, 0.5f);
                pushRt.anchorMax = pushRt.anchorMin;
                pushRt.pivot = new Vector2(0f, 0.5f);
                pushRt.sizeDelta = new Vector2(pushText.preferredWidth, pushText.preferredHeight) * 0.9f;
                pushText.margin *= 1.5f;
                pushBtn.onClick.AddListener(() => localNetBody.Push(plr.netBody));
            }

            if (viewTarget.IsLocal && canHug && !plr.IsLocal && plr.netBody != null && plr.body != null && plr.body.alive && plr.body.conscious) {
                UIManager.CreateButton(rowRt, "plrint_hug", null, null, out _, out TextMeshProUGUI hugText, out RectTransform hugRt, out Button hugBtn, out Image hugImg);
                hugImg.sprite = UIManager.uiBlockNano;
                hugImg.color = transparentWhite;
                hugRt.anchorMin = new Vector2(0.99f, 0.5f);
                hugRt.anchorMax = hugRt.anchorMin;
                hugRt.pivot = new Vector2(1f, 0.5f);
                hugRt.sizeDelta = new Vector2(hugText.preferredWidth, hugText.preferredHeight) * 0.9f;
                hugText.margin *= 1.5f;
                hugBtn.onClick.AddListener(() => localNetBody.Hug(plr.netBody));

                UIManager.CreateButton(rowRt, "plrint_pat", null, null, out _, out TextMeshProUGUI patText, out RectTransform patRt, out Button patBtn, out Image patImg);
                patImg.sprite = UIManager.uiBlockNano;
                patImg.color = transparentWhite;
                patRt.anchorMin = new Vector2(0.99f, 0.5f);
                patRt.anchorMax = patRt.anchorMin;
                patRt.pivot = new Vector2(1f, 0.5f);
                patRt.sizeDelta = new Vector2(patText.preferredWidth, patText.preferredHeight) * 0.9f;
                patText.margin *= 1.5f;
                patRt.anchoredPosition = new Vector2(-(hugRt.sizeDelta.x + 8f), 0f);
                patBtn.onClick.AddListener(() => localNetBody.Pat(plr.netBody));
            }

            rowIndex++;
        }

        if (rowIndex == 0 && healerListUIElement != null) {
            UnityEngine.Object.Destroy(healerListUIElement);
            healerListUIElement = null;
        }
    }
}
