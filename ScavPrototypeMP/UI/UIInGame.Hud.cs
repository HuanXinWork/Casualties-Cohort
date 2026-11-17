using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using System.Text;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together {
    /// <summary>
    /// 局内联机 HUD 主组件：负责队友方位指示箭头、Shift 物种头标、睡眠等待唤醒面板、背负/怀抱解除按钮与 HUD 顶层绘制编排。
    /// </summary>
    public partial class UIInGame : MonoBehaviour {
        public static UIInGame main;

        private static readonly float PLRDIRTAG_BARK_VISIBILITY_TIME = 3.0f;
        private static readonly bool PLRDIRTAG_SHOULD_NOTIFY_DYING = true;

        private static void _GUI__DrawPlayerDirection(NetBody nb, float a, bool shiftHeld) {
            Vector2 plrPos = (Vector2)nb.transform.position;

            if (plrPos == Util.ClampWorldPositionToCameraView(plrPos)) {
                if (shiftHeld) {
                    DrawShiftSpeciesMark(nb, a);
                }
                return;
            }

            Vector2 direction = KM.NormalTo(Camera.main.transform.position, plrPos, out float distance);
            direction = new Vector2(direction.x, -direction.y);

            Vector2 halfScreen = new Vector2(Screen.width, Screen.height) * 0.5f;
            Vector2 arrowScreenPos = halfScreen * (Vector2.one + direction * 0.6f);

            Color color = nb.color.WithAlpha(a);
            if (PLRDIRTAG_SHOULD_NOTIFY_DYING && nb.body.isDying) {
                float sine = (float)Math.Sin(nb.lastTimeBark + Time.realtimeSinceStartupAsDouble * 4.0);
                color = Color.Lerp(color, Color.red, sine * 0.5f + 0.5f);
            }

            GUI.skin.label.normal.textColor = color;

            Matrix4x4 oldMat = GUI.matrix;
            string txt = $"{nb.playerName} ( {Math.Floor(Util.TilesToMeters(distance))} M )";

            Vector2 labelSize = GUI.skin.label.CalcSize(FixGuiContent.Content(txt, 4));
            Vector2 labelPos = arrowScreenPos - labelSize * 0.5f;
            Rect labelRect = new Rect(labelPos.x, labelPos.y, labelSize.x, labelSize.y);
            GUI.Label(new Rect(labelPos.x, labelPos.y, labelSize.x + 200f, labelSize.y), txt);

            if (nb.IsPlayer) {
                Texture2D profilePic = nb.plr.IconAnySmallToLarge;
                if (profilePic != null) {
                    GUI.Label(new Rect(labelPos.x - labelSize.y * 1.1f, labelPos.y, labelSize.y, labelSize.y), profilePic);
                }
            }

            // 名字下方固定绘制原版伤势视图头像（避免自定义皮肤替换影响物种辨识度）
            float speciesSize = Mathf.Max(labelSize.y * 1.4f, 24f * UIBullshit.uiScale);
            Rect mark = DrawSpeciesMark(labelRect, nb, color, speciesSize, false);
            float xMin = labelRect.xMin < mark.xMin ? labelRect.xMin : mark.xMin;
            float yMin = labelRect.yMin < mark.yMin ? labelRect.yMin : mark.yMin;
            float xMax = labelRect.xMax > mark.xMax ? labelRect.xMax : mark.xMax;
            float yMax = labelRect.yMax > mark.yMax ? labelRect.yMax : mark.yMax;
            Rect tagBounds = Rect.MinMaxRect(xMin, yMin, xMax, yMax);

            arrowScreenPos += direction * labelSize.magnitude;
            arrowScreenPos = new Vector2(
                Mathf.Clamp(arrowScreenPos.x, tagBounds.xMin, tagBounds.xMax),
                Mathf.Clamp(arrowScreenPos.y, tagBounds.yMin, tagBounds.yMax));
            arrowScreenPos += direction * 32f;

            float arrowSize = 32f * UIBullshit.uiScale;
            arrowScreenPos -= Vector2.one * arrowSize * 0.5f;
            GUIUtility.RotateAroundPivot(Vector2.SignedAngle(Vector2.right, direction), arrowScreenPos);

            GUI.DrawTexture(new Rect(arrowScreenPos.x, arrowScreenPos.y, arrowSize, arrowSize), CoopModAssets.arrowicon.texture, ScaleMode.StretchToFill, true, 0f, color, 0f, 0f);

            GUI.matrix = oldMat;
            GUI.skin.label.normal.textColor = Color.white;
        }

        /// <summary>按住显示队友键时，为视野内可见队友头顶绘制物种图标与编队槽位号。</summary>
        private static void DrawShiftSpeciesMark(NetBody nb, float a) {
            Camera cam = Camera.main;
            if (cam == null || nb == null) {
                return;
            }
            Vector3 world = nb.transform.position;
            if (AnatomyConstants.IsValidLimbIndex(nb.body, (int)LimbIndex.Head)) {
                world = nb.body.limbs[(int)LimbIndex.Head].transform.position;
            }
            world.y += 0.55f;
            Vector3 sp = cam.WorldToScreenPoint(world);
            if (sp.z <= 0f) {
                return;
            }
            float size = Mathf.Max(28f * UIBullshit.uiScale, 22f);
            Rect anchor = new Rect(sp.x, Screen.height - sp.y, 0f, 0f);
            Color color = nb.color;
            color.a = a;
            DrawSpeciesMark(anchor, nb, color, size, true);
        }

        private static Rect DrawSpeciesMark(Rect anchor, NetBody nb, Color color, float size, bool overhead) {
            if (nb == null || size < 1f) {
                return anchor;
            }

            WoundView view = WoundView.view;
            if (view == null || view.headCharSprites == null || view.headCharSprites.Length < 6) {
                return anchor;
            }

            int chr = 0;
            if (nb.body != null) {
                chr = Mathf.Clamp(nb.body.charType, 0, 2);
            } else if (nb.plr != null) {
                chr = Mathf.Clamp(nb.plr.selectedCharacter, 0, 2);
            }

            Sprite outline = view.headCharSprites[chr * 2];
            Sprite middle = view.headCharSprites[chr * 2 + 1];
            if (outline == null) {
                return anchor;
            }

            int digit = 0;
            bool haveDigit = nb.plr != null
                && UI.TeammateList.TeammateListHost.Roster != null
                && UI.TeammateList.TeammateListHost.Roster.TrySlotDigit(nb.plr, out digit);
            string num = haveDigit ? digit.ToString() : "";
            float gap = 2f * UIBullshit.uiScale;
            float numW = 0f;
            if (num.Length > 0) {
                numW = GUI.skin.label.CalcSize(FixGuiContent.Content(num, 37)).x;
            }
            float pair = size + (numW > 0f ? numW + gap : 0f);
            float x;
            float y;
            if (overhead) {
                x = anchor.x - pair * 0.5f;
                y = anchor.y - size * 0.5f;
            } else {
                x = anchor.x + (anchor.width - pair) * 0.5f;
                y = anchor.yMax + 2f * UIBullshit.uiScale;
            }
            if (numW > 0f) {
                Color prevText = GUI.skin.label.normal.textColor;
                TextAnchor prevAlign = GUI.skin.label.alignment;
                GUI.skin.label.normal.textColor = color;
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                GUI.Label(new Rect(x, y, numW, size), num);
                GUI.skin.label.alignment = prevAlign;
                GUI.skin.label.normal.textColor = prevText;
            }
            Rect icon = new Rect(x + (numW > 0f ? numW + gap : 0f), y, size, size);
            DrawGuiSprite(icon, outline, color);
            if (middle != null) {
                DrawGuiSprite(icon, middle, color);
            }
            return new Rect(x, y, pair, size);
        }

        private static void DrawGuiSprite(Rect r, Sprite sprite, Color color) {
            if (sprite == null || sprite.texture == null) {
                return;
            }

            Rect tr = sprite.textureRect;
            Texture2D tex = sprite.texture;
            if (tex.width < 1 || tex.height < 1 || tr.width < 1f || tr.height < 1f) {
                return;
            }

            Rect uv = new Rect(tr.x / tex.width, tr.y / tex.height, tr.width / tex.width, tr.height / tex.height);
            Color prev = GUI.color;
            GUI.color = color;
            GUI.DrawTextureWithTexCoords(r, tex, uv);
            GUI.color = prev;
        }

        private static void _GUI_DoPlayerPointersGui() {
            double t = Time.realtimeSinceStartupAsDouble;

            // 即使控制台或弹出式聊天失去焦点，队友名字标签也保持游戏像素字体一致
            bool pinned = UIBullshit.PushFocusedChatFont(out Font labelFont, out Font boxFont, out Font fieldFont);
            int ogNameSize = GUI.skin.label.fontSize;
            if (pinned) {
                GUI.skin.label.fontSize = UIBullshit.GetCurFontSize();
            }

            try {
                bool shiftHeld = Input.GetKey(KeyBinds.GetBind(CoopKeybinds.showplrs));
                bool inPauseMenu = Util.IsInGameAndPauseMenu();

                foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
                    if (plr.IsLocal || plr.body == null || !plr.body.alive) {
                        continue;
                    }
                    float alpha = 1f;
                    if (!inPauseMenu && !shiftHeld) {
                        float diff = (float)(t - plr.netBody.lastTimeBark);
                        if (diff < PLRDIRTAG_BARK_VISIBILITY_TIME) {
                            alpha = Mathf.Clamp01(1.0f - diff / PLRDIRTAG_BARK_VISIBILITY_TIME);
                        } else {
                            continue;
                        }
                    }
                    _GUI__DrawPlayerDirection(plr.netBody, alpha, shiftHeld);
                }
            } finally {
                if (pinned) {
                    GUI.skin.label.fontSize = ogNameSize;
                    UIBullshit.PopFocusedChatFont(true, labelFont, boxFont, fieldFont);
                }
            }
        }

        private static bool _GUI_DoGeneralMPGui() {
            float uisc = UIBullshit.uiScale;
            float wdth2 = 340f * uisc;

            if (!Util.TryGetLocalBody(out Body locBody)) {
                return false;
            }

            if (Spectator.GetCameraFollow() == SpectatorLastTarget) {
                Spectator.SetCameraFollow(null);
            }

            if (locBody.sleeping && locBody.alive && !Multiplayer.rules.DisableSleep) {
                if (!Util.IsInPauseMenuOrMainMenu() && !ServerMain.CheckIfEveryoneIsSleeping()) {
                    float panelWidth = wdth2 + 20f * uisc;
                    float wakeupX = Math.Min(Screen.width * 0.5f + wdth2, Screen.width - panelWidth);
                    float wakeupY = Screen.height * 0.45f - 100f * uisc;

                    int consciousCounter = 0;
                    int sleepCounter = 0;
                    foreach (KeyValuePair<Body, ScavPlayer> b in ScavPlayer.BodyToPlayerDict) {
                        if (b.Key.conscious) {
                            consciousCounter++;
                        } else {
                            sleepCounter++;
                        }
                    }

                    float fullHeight = (100f + 10f + 40f + 10f + 10f + 30f * consciousCounter) * uisc;
                    UIBullshit._GUI_9SlicePanel(new Rect(wakeupX - (panelWidth - wdth2) * 0.5f, wakeupY, panelWidth, fullHeight), 0.9f, 0.5f, false, UIBullshit.unscaled_uiBlockNano);
                    wakeupY += 10f * uisc;

                    int ogFontSize = GUI.skin.label.fontSize;
                    GUI.skin.label.fontSize = (int)(ogFontSize * 1.3f);
                    try {
                        if (GUI.Button(new Rect(wakeupX, wakeupY, wdth2, 100f * uisc), Lang.Get("wake_up"))) {
                            if (locBody.sleeping) {
                                locBody.WakeUp();
                                if (locBody.TryGetNetBody(out NetBody nb)) {
                                    nb.SetNetHealthSyncIgnoreTime(1);
                                }
                            }
                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerWakeTheFuckUpSamurai, true);
                        }
                    } finally {
                        GUI.skin.label.fontSize = ogFontSize;
                    }

                    wakeupY += 110f * uisc;
                    GUI.Label(new Rect(wakeupX, wakeupY, wdth2 + 100f * uisc, 40f * uisc), Lang.Get("plr_sleeping_count") + sleepCounter.ToString());
                    wakeupY += 40f * uisc;

                    foreach (KeyValuePair<Body, ScavPlayer> b in ScavPlayer.BodyToPlayerDict) {
                        if (b.Key.conscious) {
                            GUI.Label(new Rect(wakeupX, wakeupY, wdth2 + 300f * uisc, 40f * uisc), b.Value.playerName + Lang.Get("plr_not_sleeping"));
                            wakeupY += 30f * uisc;
                        }
                    }
                }
                return true;
            }

            if (Net.IsDedicatedServer) {
                return true;
            }

            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me != null && me.body != null && me.netBody != null && !Util.IsInWoundView() && me.body.conscious) {
                float buttonHeight = 80f * uisc;
                if (me.netBody.piggybacking_on != null) {
                    if (UIBullshit._GUI_FocusableButton(new Rect(Screen.width * 0.5f - wdth2 * 0.5f, Screen.height * 0.9f - 10f * uisc, wdth2, buttonHeight), Lang.Get("piggyback_stop") + "\n" + me.netBody.piggybacking_on.bodyName, out bool focused)) {
                        me.netBody.StopPiggyback();
                    }
                    interaction_menu_focused = interaction_menu_focused || focused;
                } else if (me.netBody.carrying_person != null) {
                    if (UIBullshit._GUI_FocusableButton(new Rect(Screen.width * 0.5f - wdth2 * 0.5f, Screen.height * 0.9f - 10f * uisc, wdth2, buttonHeight), Lang.Get("carry_stop") + "\n" + me.netBody.carrying_person.bodyName, out bool focused)) {
                        me.netBody.carrying_person.SetNetIgnoreTime(0.2f);
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopCarryPerson);
                        me.netBody.carrying_person.StopPiggyback();
                    }
                    interaction_menu_focused = interaction_menu_focused || focused;
                }
            }

            if (Multiplayer.rules.SpectateWhileUnconscious) {
                if (locBody.conscious) {
                    return true;
                }
            } else if (locBody.alive) {
                return true;
            }

            return false;
        }

        private static void _GUI_DoMPUserInterface() {
            if (Multiplayer.rules.ShowPlayerDirections) {
                _GUI_DoPlayerPointersGui();
            }

            if (Con._DEV_HIDEHUD || Con.IsConsoleOpen()) {
                Util.OpenPauseMenu(false);
                StopPlayerInteractionMenu();
                return;
            }

            if (interaction_menu_target_body != null) {
                _GUI_PlayerInteractionGui();
            } else {
                interaction_menu_target_body = null;
                interaction_menu_focused = false;
            }

            if (!Con._DEV_FREECAM) {
                if (SpectatorModeActive) {
                    _GUI_DoSpectatorGui();
                    return;
                }
                if (_GUI_DoGeneralMPGui()) {
                    return;
                }
            }

            if (!Util.IsInPauseMenuOrMainMenu()) {
                float uisc = UIBullshit.uiScale;
                float wdth2 = 240f * uisc;
                float hght2 = 80f * uisc;
                if (Con._DEV_FREECAM) {
                    if (GUI.Button(new Rect(Screen.width - wdth2, Screen.height - 80f * uisc, wdth2, hght2), Lang.Get("spectate_freecamexit"))) {
                        Util.PlayUISound(PlayerCamera.UISoundType.Close);
                        Con._DEV_FREECAM = false;

                        if (SpectatorModeActive) {
                            (NetBody, float) res = NetBody.GetNearestBody(PlayerCamera.main.transform.position, spectator_only_alive, false);
                            if (res.Item1 != null) {
                                Spectator_SetTarget(res.Item1);
                            }
                        }
                    }
                } else if (GUI.Button(new Rect(Screen.width - wdth2, Screen.height - 80f * uisc, wdth2, hght2), Lang.Get("spectate"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.Click);
                    StartSpectatorMode();
                }
            }
        }

        internal void _GUI_DoInGameUI() {
            if (!FixGuiLayout.Allow()) {
                return;
            }

            interaction_menu_focused = false;

            if (!Multiplayer.IsNetworkActiveAndIsInGame()) {
                return;
            }

            if (!UIBullshit.WantsMpChrome()) {
                _GUI_DoMPUserInterface();
                UI.TeammateList.TeammateListHost.Draw();
                HeadpatSession.Draw();
                return;
            }

            RectOffset ogPadding = GUI.skin.button.padding;
            TextClipping ogClipping = GUI.skin.button.clipping;
            try {
                GUI.skin.button.padding = UIBullshit.ZeroPadding;
                GUI.skin.button.clipping = TextClipping.Overflow;
                _GUI_DoMPUserInterface();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIInGame._GUI_DoInGameUI", ex);
            } finally {
                GUI.skin.button.padding = ogPadding;
                GUI.skin.button.clipping = ogClipping;
            }

            UI.TeammateList.TeammateListHost.Draw();
            HeadpatSession.Draw();
        }
    }

    /// <summary>
    /// 将联机 IMGUI 悬停态、交互菜单焦点与聊天滚动条交互合并进原版 uGUI 指针遮挡检测。
    /// </summary>
    [HarmonyPatch(typeof(UIUtil), nameof(UIUtil.IsPointerOverUIElement), new Type[] { typeof(List<RaycastResult>) })]
    internal static class UIUtil_IsPointerOverUIElement_MultiplayerPatch {
        private static void Postfix(UIUtil __instance, ref bool __result) {
            if (Net.IsRunning && !__result) {
                __result = UIInGame.interaction_menu_focused
                    || UIBullshit.IS_CURSOR_OVERLAPPING
                    || Chat.MouseIsInteractingWithScrollbar();
            }
        }
    }

    /// <summary>
    /// 世界生成加载界面多玩家培养舱剪影克隆器：按在线玩家列表与各自所选物种并排渲染实验体剪影。
    /// </summary>
    internal class LoadingScreenSpecimenDuplicator : MonoBehaviour {
        private Vector2 OG_creaturepos = Vector2.zero;
        private readonly List<RectTransform> clones = new();
        private readonly List<int> cloneSpecies = new();
        private string _rosterKey = "";

        private static RectTransform og_creature_rect => WorldGeneration.world.genRects[2];

        private void Awake() {
            if (WorldGeneration.world != null && WorldGeneration.world.genRects != null && WorldGeneration.world.genRects.Length > 2) {
                OG_creaturepos = WorldGeneration.world.genRects[2].anchoredPosition;
            }
        }

        private void OnEnable() {
            _rosterKey = "";
            RebuildClones();
        }

        private void LateUpdate() {
            if (!gameObject.activeInHierarchy || WorldGeneration.world == null) {
                return;
            }
            WorldGeneration world = WorldGeneration.world;
            if (world.genRects == null || world.genRects.Length <= 2) {
                return;
            }

            // 原版每帧将 genRects[2] 刷成本地角色物种，需隐藏原节点只显示按房间名册克隆的剪影
            HideOgCreature();

            string key = BuildRosterKey();
            if (key != _rosterKey) {
                RebuildClones();
                _rosterKey = key;
            }

            OG_creaturepos = world.genRects[2].anchoredPosition;

            bool surfacePod = world.biomeDepth == 0 && world.biomeOverride == WorldGeneration.OverrideSceneType.None;
            Sprite[] pods = world.podImages;
            Color tint = Color.white;
            if (!surfacePod && world.layerGenerationCharColors != null && world.layerGenerationCharColors.Length > 0) {
                int di = Mathf.Clamp(world.biomeDepth, 0, world.layerGenerationCharColors.Length - 1);
                tint = world.layerGenerationCharColors[di];
            }

            Vector2 stepOffset = new Vector2(100f, 25f) * PlayerCamera.uiScale;
            for (int i = 0; i < clones.Count; i++) {
                RectTransform rt = clones[i];
                if (rt == null) {
                    continue;
                }
                ApplySpeciesSprite(rt.GetComponent<Image>(), cloneSpecies[i], pods, surfacePod, tint);
                float progress = (i + 0.5f) - (clones.Count * 0.5f);
                rt.anchoredPosition = OG_creaturepos - stepOffset * progress;
            }
        }

        private void HideOgCreature() {
            Image img = og_creature_rect != null ? og_creature_rect.GetComponent<Image>() : null;
            if (img == null) {
                return;
            }
            img.enabled = false;
            Color c = img.color;
            c.a = 0f;
            img.color = c;
        }

        private static string BuildRosterKey() {
            StringBuilder sb = new StringBuilder(64);
            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                if (plr == null) {
                    continue;
                }
                sb.Append(plr.playerId.id).Append(':').Append(SpeciesOf(plr)).Append(';');
            }
            return sb.ToString();
        }

        private static void ApplySpeciesSprite(Image img, int chr, Sprite[] pods, bool surfacePod, Color tint) {
            if (img == null || pods == null || pods.Length < 3) {
                return;
            }
            chr = Mathf.Clamp(chr, 0, 2);
            int half = pods.Length / 2;
            int idx = surfacePod ? chr : chr + half;
            if (idx < 0 || idx >= pods.Length || pods[idx] == null) {
                return;
            }
            img.sprite = pods[idx];
            img.color = tint;
            img.enabled = true;
        }

        private static int SpeciesOf(ScavPlayer plr) {
            if (plr == null) {
                return 0;
            }
#if FULLGAME
            if (plr.body != null) {
                return CharacterSpawn.ClampChar(plr.body.charType);
            }
            if (plr.TryGetNetBody(out NetBody nb) && nb.syncBody != null) {
                return CharacterSpawn.ClampChar(nb.syncBody.characterSelected.Value);
            }
            if (plr.IsLocal) {
                return CharacterSpawn.ClampChar(PreRunScript.characterSelected);
            }
            return CharacterSpawn.ClampChar(plr.selectedCharacter);
#else
            return Mathf.Clamp(plr.selectedCharacter, 0, 2);
#endif
        }

        private void RebuildClones() {
            if (WorldGeneration.world == null || WorldGeneration.world.genRects == null || WorldGeneration.world.genRects.Length <= 2) {
                return;
            }
            GameObject ogCreature = og_creature_rect.gameObject;

            for (int i = 0; i < clones.Count; i++) {
                if (clones[i] != null) {
                    Destroy(clones[i].gameObject);
                }
            }
            clones.Clear();
            cloneSpecies.Clear();

            WorldGeneration world = WorldGeneration.world;
            bool surfacePod = world.biomeDepth == 0 && world.biomeOverride == WorldGeneration.OverrideSceneType.None;
            Sprite[] pods = world.podImages;

            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                if (plr == null) {
                    continue;
                }
                int chr = SpeciesOf(plr);
                GameObject cloned = Instantiate(ogCreature);
                OnAddDuplicated(cloned, plr, chr);
                ApplySpeciesSprite(cloned.GetComponent<Image>(), chr, pods, surfacePod, Color.white);
            }
            HideOgCreature();
        }

        private void OnAddDuplicated(GameObject obj, ScavPlayer plr, int chr) {
            RectTransform rt = obj.GetComponent<RectTransform>();
            obj.name = "GenCharacter_" + plr.playerName + "_" + chr;
            rt.SetParent(og_creature_rect.parent);
            rt.sizeDelta = Vector2.zero;
            rt.SetSiblingIndex(og_creature_rect.GetSiblingIndex());
            clones.Add(rt);
            cloneSpecies.Add(chr);
            Image img = obj.GetComponent<Image>();
            if (img != null) {
                img.enabled = true;
            }
        }
    }
}
