using CasualtiesTogetherUtils;
using System.Collections.Generic;
using Together.Api;
using Together.MpConstants;
using UnityEngine;

namespace Together.UI.TeammateList {
    /// <summary>
    /// 局内右侧队友状态面板核心控制器：负责在线队友槽位编排、折叠持久化、右键交互菜单唤起、Shift+1..4 快捷打开伤势视图及拖拽递物响应。
    /// </summary>
    internal sealed partial class Roster {
        private const int PageSize = 4;
        private const float StepGap = 0.5f;
        private const float FullBlood = 100f;

        // 信号丢失阈值采用 3 倍交互距离，点击/快捷键直接打开伤势面板仍采用 1 倍交互距离
        private const float RangeMult = 3f;

        private const string LostConnection = "Lost Connection";
        private const string ChipNotDetected = "Chip Not Detected";
        private const string Terminated = "TERMINATED";
        private const string CollapsedPref = "Together_TeammateListCollapsed";

        private readonly List<Slot> _slots = new List<Slot>(8);
        private readonly Dictionary<ScavPlayer, int> _index = new Dictionary<ScavPlayer, int>(8);
        private readonly Dictionary<Body, int> _byBody = new Dictionary<Body, int>(8);
        private readonly Rect[] _hit = new Rect[PageSize];

        private bool _wasInGame;
        private int _rosterCount = -1;
        private float _nextRoster;
        private int _gen;
        private int _high;
        private int _page;
        private bool _collapsed = PlayerPrefs.GetInt(CollapsedPref, 0) != 0;

        private int _dragItemId;
        private int _dragSlot = -1;

        public void Tick() {
            bool inGame = Multiplayer.IsNetworkActiveAndIsInGame();
            if (inGame && !_wasInGame) {
                TeammateListHost.NoteLayerReady();
            }
            _wasInGame = inGame;
            if (!inGame) {
                return;
            }

            ReconcileIfChanged();
            RefreshPageRange();
            if (!Typing() && Bind.Pressed()) {
                _collapsed = !_collapsed;
                PlayerPrefs.SetInt(CollapsedPref, _collapsed ? 1 : 0);
                PlayerPrefs.Save();
            }
            if (_high != 0) {
                if (!Covered()) {
                    if (!Typing()) {
                        Hotkeys();
                    }
                    if (!_collapsed) {
                        Drag();
                    }
                }
                StepJob();
            }
        }

        public void NoteTargetUnchipped(ScavPlayer plr, bool unchipped) {
            if (plr == null || !_index.TryGetValue(plr, out int idx) || (uint)idx >= (uint)_slots.Count) {
                return;
            }
            _slots[idx].TargetUnchipped = unchipped;
        }

        public void ResampleChipFlags() {
            TeammateListHost.RefreshViewerChip();
            for (int i = 0; i < _high; i++) {
                Slot slot = _slots[i];
                if (slot?.Player == null) {
                    continue;
                }
                slot.TargetUnchipped = slot.Player.IsUnchipped();
            }
        }

        public void NoteYap(Body body, string line) {
            if (body == null || string.IsNullOrEmpty(line) || !_byBody.TryGetValue(body, out int idx) || (uint)idx >= (uint)_slots.Count) {
                return;
            }
            Slot slot = _slots[idx];
            if (slot.Player != null) {
                string capped = Cap(line);
                if (slot.Yap != capped) {
                    slot.Yap = capped;
                }
            }
        }

        /// <summary>查询指定队友在当前分页卡片上的 1..4 序号。</summary>
        internal bool TrySlotDigit(ScavPlayer plr, out int digit) {
            digit = 0;
            if (plr == null || !_index.TryGetValue(plr, out int index) || index < 0) {
                return false;
            }
            digit = (index % PageSize) + 1;
            return true;
        }

        private static bool Covered() {
            if (Util.IsInTradingMenu()) {
                return true;
            }
            WorldGeneration world = WorldGeneration.world;
            return world != null && world.cutsceneActive;
        }

        private void RightClick(Event current) {
            Vector2 mouse = current.mousePosition;
            int pageStart = _page * PageSize;
            for (int i = 0; i < PageSize; i++) {
                if (_hit[i].width <= 0f || !_hit[i].Contains(mouse)) {
                    continue;
                }
                int index = pageStart + i;
                if ((uint)index >= (uint)_slots.Count) {
                    return;
                }
                Slot slot = _slots[index];
                ScavPlayer plr = slot?.Player;
                if (plr == null || plr.body == null) {
                    return;
                }
                NetBody nb = plr.netBody;
                if (nb == null || nb.body == null) {
                    return;
                }
                if (UIInGame.interaction_menu_target_body != nb) {
                    UIInGame.StartPlayerInteractionMenu(nb);
                } else {
                    UIInGame.StopPlayerInteractionMenu();
                }
                current.Use();
                return;
            }
        }

        private void Hotkeys() {
            if (!Input.GetKey(KeyCode.LeftShift) && !Input.GetKey(KeyCode.RightShift)) {
                return;
            }

            int offset = -1;
            if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1)) {
                offset = 0;
            } else if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2)) {
                offset = 1;
            } else if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3)) {
                offset = 2;
            } else if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4)) {
                offset = 3;
            }

            if (offset >= 0) {
                OpenAt(_page * PageSize + offset);
            }
        }

        private void Drag() {
            PlayerCamera cam = PlayerCamera.main;
            if (cam == null) {
                return;
            }
            Item dragItem = cam.dragItem;
            if (dragItem == null) {
                _dragItemId = 0;
                _dragSlot = -1;
                return;
            }

            Vector2 guiMousePos = new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
            int targetSlot = -1;
            for (int i = 0; i < PageSize; i++) {
                if (_hit[i].width > 0f && _hit[i].Contains(guiMousePos)) {
                    targetSlot = _page * PageSize + i;
                    break;
                }
            }
            if (targetSlot >= 0) {
                int instanceId = dragItem.GetInstanceID();
                if (_dragItemId != instanceId || _dragSlot != targetSlot) {
                    _dragItemId = instanceId;
                    _dragSlot = targetSlot;
                    OpenAt(targetSlot);
                }
            }
        }

        private void OpenAt(int index) {
            if ((uint)index >= (uint)_slots.Count) {
                return;
            }
            Slot slot = _slots[index];
            if (slot.Player == null || slot.Body == null || PauseHandler.paused) {
                return;
            }
            Body localBody = Util.GetLocalBody();
            float interactDist = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance);
            if (localBody == null || !KM.DistanceCheck(localBody, slot.Body, interactDist)) {
                return;
            }

            PlayerCamera cam = PlayerCamera.main;
            if (cam == null) {
                return;
            }
            cam.radialOpen = false;
            if (cam.craftingPanel != null && cam.craftingPanel.activeSelf) {
                cam.OpenCraftScreen();
            }
            if (cam.tradeMenu != null && cam.tradeMenu.activeSelf) {
                cam.ToggleTradeMenu();
            }
            if (cam.currentContainer != null || (cam.containerMenu != null && cam.containerMenu.activeSelf)) {
                cam.CloseContainer();
            }

            MinigameBase mg = MinigameBase.main;
            if (mg != null && mg.currentMinigame != null && mg.currentMinigame.CanExit()) {
                mg.EndMinigame();
            }
            if (PlayerCamera_ToggleWoundView_MultiplayerPatch.IsOpen()) {
                WoundView view = WoundView.view;
                if (view == null || view.body != slot.Body) {
                    cam.ToggleWoundView(false);
                }
            }
            PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(slot.Body, true);
        }

        private static bool Typing() {
            if (Chat.CHAT_textbox_input_focused || UIMainMenu.IsOpen() || TogetherPanelApi.IsAnyExternalInputBlockerActive()) {
                return true;
            }
            ConsoleScript console = ConsoleScript.instance;
            return console != null && console.active;
        }

        private void ReconcileIfChanged() {
            Dictionary<knetid, ScavPlayer> clientIdToPlayerDict = ScavPlayer.ClientIdToPlayerDict;
            int count = clientIdToPlayerDict?.Count ?? 0;
            float unscaledTime = Time.unscaledTime;
            if (count == _rosterCount && unscaledTime < _nextRoster) {
                return;
            }
            _rosterCount = count;
            _nextRoster = unscaledTime + 2f;
            _gen++;

            if (clientIdToPlayerDict != null) {
                ScavPlayer localPlr = ScavPlayer.LocalPlayer;
                foreach (KeyValuePair<knetid, ScavPlayer> kvp in clientIdToPlayerDict) {
                    ScavPlayer plr = kvp.Value;
                    if (plr != null && plr != localPlr) {
                        Touch(plr);
                    }
                }
            }
            DropStale();
        }

        private void BindBody(Slot slot) {
            if (!_index.TryGetValue(slot.Player, out int idx)) {
                return;
            }
            Body body = slot.Player.body;
            if (slot.Body != body) {
                if (slot.Body != null) {
                    _byBody.Remove(slot.Body);
                }
                slot.Body = body;
                if (body != null) {
                    _byBody[body] = idx;
                }
            }
        }

        private void RefreshPageRange() {
            int start = _page * PageSize;
            int end = Mathf.Min(start + PageSize, _high);
            for (int i = start; i < end; i++) {
                Slot slot = _slots[i];
                if (slot != null && slot.Player != null) {
                    RefreshSlotRange(slot);
                }
            }
        }

        private static void RefreshSlotRange(Slot slot) {
            Body local = Util.GetLocalBody();
            Body target = slot.Body;
            if (local == null || target == null) {
                slot.InRange = false;
                return;
            }
            if (!Multiplayer.rules.TeammateListUsesRange) {
                slot.InRange = true;
                return;
            }
            float baseDist = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance);
            slot.InRange = KM.DistanceCheck(local, target, baseDist * RangeMult);
        }

        private void Touch(ScavPlayer plr) {
            Slot slot;
            if (_index.TryGetValue(plr, out int idx)) {
                slot = _slots[idx];
            } else {
                idx = FirstEmpty();
                if (idx < 0) {
                    idx = _slots.Count;
                    slot = new Slot();
                    _slots.Add(slot);
                } else {
                    slot = _slots[idx];
                }
                slot.Player = plr;
                _index[plr] = idx;
                if (idx + 1 > _high) {
                    _high = idx + 1;
                }
            }

            slot.Stamp = _gen;
            slot.NameColor = NameColor(plr);
            BindBody(slot);
            slot.TargetUnchipped = plr.IsUnchipped();
            RefreshSlotRange(slot);

            int species = slot.Body != null ? slot.Body.charType : 0;
            if (slot.Species != species || slot.Head == null) {
                slot.Species = species;
                SpeciesIcon.Assign(slot, species);
            }

            string name = plr.playerName ?? "";
            if (slot.Name != name) {
                slot.Name = name;
            }
        }

        private int FirstEmpty() {
            int count = _slots.Count;
            for (int i = 0; i < count; i++) {
                if (_slots[i].Player == null) {
                    return i;
                }
            }
            return -1;
        }

        private void DropStale() {
            int count = _slots.Count;
            int high = 0;
            for (int i = 0; i < count; i++) {
                Slot slot = _slots[i];
                if (slot.Player == null) {
                    continue;
                }
                if (slot.Stamp == _gen) {
                    high = i + 1;
                    continue;
                }
                _index.Remove(slot.Player);
                if (slot.Body != null) {
                    _byBody.Remove(slot.Body);
                }
                slot.Player = null;
                slot.Body = null;
                slot.Name = "";
                slot.Mood = "";
                slot.Yap = "";
                slot.Blood = "";
                slot.Mind = "";
                slot.Satiety = "";
                slot.Danger = 0f;
                slot.MoodleCount = 0;
            }
            _high = high;
            int maxPages = _high == 0 ? 1 : ((_high + PageSize - 1) / PageSize);
            if (_page >= maxPages) {
                _page = maxPages - 1;
            }
        }
    }
}
