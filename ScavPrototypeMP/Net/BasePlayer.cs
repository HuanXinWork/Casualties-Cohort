using CasualtiesTogetherUtils;
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 客户端每帧采集并上传给服务端的玩家输入命令数据包。
    /// </summary>
    public struct PlayerInputPacket {
        public SByteScalarVec2 Move;
        public Vector2 LookPos;
        public Vector2_4byte_512 CameraPos;
        public ShortScalarVec2 CursorPos;
        public bool IsInPauseMenu;
        public bool IsInConsole;
        public bool IsWindowFocused;
        public bool IsChatFocused;
        public bool IsInCraftingMenu;
        public bool IsInTradingMenu;

        public bool ButtonAttack;
        public bool ButtonIteminteract;

        public knetid WoundviewTargetNetBodyId;
        public knetid DragItemId;
        public bool IsInInventory;

        public void CollectInput() {
            if (ScavPlayer.TryGetLocalNetBody(out NetBody nb)) {
                Move = (SByteScalarVec2)nb.body.moveDir;
            }
            LookPos = Util.GetCursorWorldPos();
            CameraPos = (Vector2)Camera.main.transform.position;

            Vector2 mouseRelativeToCenter = Util.GetCursorPos();
            Vector2 res = Util.GetResolution();
            mouseRelativeToCenter -= res * 0.5f;
            mouseRelativeToCenter /= res.y;
            mouseRelativeToCenter *= 2f;
            CursorPos = (ShortScalarVec2)mouseRelativeToCenter;

            IsInConsole = Con.IsConsoleOpen();
            IsWindowFocused = Application.isFocused;
            IsChatFocused = Chat.CHAT_textbox_input_focused;
            IsInCraftingMenu = Util.IsInCraftingMenu();
            IsInTradingMenu = Util.IsInTradingMenu();
            IsInInventory = PlayerCamera.main != null && PlayerCamera.main.radialOpen;
            IsInPauseMenu = Util.IsInPauseMenu() || UIMainMenu.IsOpen();

            ButtonAttack = Input.GetKey(KeyBinds.GetBind("attack")) && !HeadpatSession.Open;
            ButtonIteminteract = Input.GetKey(KeyBinds.GetBind("iteminteract"));

            if (ScavPlayer.LocalPlayer != null) {
                if (PlayerCamera.main != null) {
                    if (Util.IsInWoundView() && WoundView.view != null && WoundView.view.body != null && WoundView.view.body.TryGetNetBody(out NetBody pacient) && pacient.syncBody != null) {
                        WoundviewTargetNetBodyId = pacient.netId;
                    } else {
                        WoundviewTargetNetBodyId = 0;
                    }
                    DragItemId = AnyObjectNetId.GetNetId(PlayerCamera.main.dragItem).netId;
                } else {
                    DragItemId = 0;
                    WoundviewTargetNetBodyId = 0;
                }
            }
        }

        public void ValidateInput(ScavPlayer plr) {
            if (!LookPos.IsFinite()) {
                LookPos = Vector2.zero;
            }

            if (plr.body != null && plr.body.moveDir != Vector2.zero) {
                IsWindowFocused = true;
                IsChatFocused = false;
                IsInTradingMenu = false;
            }

            if (plr.IsConscious()) {
                Vector2 pos = plr.netBody.position;
                Vector2 theAspect = new Vector2(50f, 30f);
                LookPos = LookPos.Clamp(pos - theAspect, pos + theAspect);

                if (DragItemId != 0) {
                    if (!ItemSync.TryGetItem(DragItemId, out SyncInfo _, out Item item) || !plr.IsConscious() || !ItemSync.CheckIfBodyReachThisItem(item, plr.body)) {
                        DragItemId = 0;
                    }
                }
                if (WoundviewTargetNetBodyId != 0) {
                    if (plr.body == null || !NetBody.TryGetNetBodyFromId(WoundviewTargetNetBodyId, out NetBody nb) || !KM.DistanceCheck(nb, plr.body, 30f)) {
                        WoundviewTargetNetBodyId = 0;
                    }
                }
            } else {
                IsInTradingMenu = false;
                DragItemId = 0;
                WoundviewTargetNetBodyId = 0;
            }
        }

        public void SetInput(BasePlayer plr) {
            plr.Move.Value = Move;
            plr.LookPos.Value = LookPos;
            plr.CameraPos.Value = CameraPos;
            plr.CursorPos.Value = CursorPos;
            plr.IsInPauseMenu.Value = IsInPauseMenu;
            plr.IsInConsole.Value = IsInConsole;
            plr.IsWindowFocused.Value = IsWindowFocused;
            plr.IsChatFocused.Value = IsChatFocused;
            plr.IsInCraftingMenu.Value = IsInCraftingMenu;
            plr.IsInTradingMenu.Value = IsInTradingMenu;
            plr.IsInInventory.Value = IsInInventory;
            plr.ButtonAttack.Value = ButtonAttack;
            plr.ButtonIteminteract.Value = ButtonIteminteract;
            plr.WoundviewTargetNetBodyId.Value = WoundviewTargetNetBodyId;
            plr.DragItemId.Value = DragItemId;
        }
    }

    /// <summary>
    /// 驱动玩家输入采样与服务端输入校验的 LiteEntitySystem 控制器实体。
    /// </summary>
    public class BasePlayerController : HumanControllerLogic<PlayerInputPacket, BasePlayer> {
        public BasePlayerController(EntityParams entityParams) : base(entityParams) {
        }

        protected override void Update() {
            base.Update();
        }

        protected override void VisualUpdate() {
            if (ControlledEntity == null) {
                return;
            }

            ref PlayerInputPacket nextCommand = ref ModifyPendingInput();
            nextCommand.CollectInput();
        }

        protected override void BeforeControlledUpdate() {
            base.BeforeControlledUpdate();

            if (ControlledEntity == null) {
                return;
            }

            if (ControlledEntity.player != null) {
                if (Net.IsServer) {
                    CurrentInput.ValidateInput(ControlledEntity.player);
                    CurrentInput.SetInput(ControlledEntity);
                }
                ControlledEntity.ApplyInput();
            }
        }
    }

    /// <summary>
    /// 玩家会话网络实体（承载 SteamId、昵称、配色、指针视野与小游戏会话状态等全部玩家级 SyncVar）。
    /// 注意：严格保持全部 SyncVar / SyncString 字段声明顺序与 v5.5.x 线协议一致。
    /// </summary>
    [EntityFlags(EntityFlags.UpdateOnClient)]
    public class BasePlayer : PawnLogic {
        public SyncVar<ulong> playerSteamId;
        public readonly SyncString playerName = new();
        public SyncVar<Color24> playerColor;

        public SyncVar<SByteScalarVec2> Move;
        [SyncVarFlags(SyncFlags.Interpolated)]
        public SyncVar<Vector2> LookPos;
        [SyncVarFlags(SyncFlags.Interpolated)]
        public SyncVar<Vector2_4byte_512> CameraPos;
        [SyncVarFlags(SyncFlags.Interpolated)]
        public SyncVar<ShortScalarVec2> CursorPos;
        public SyncVar<bool> IsInPauseMenu;
        public SyncVar<bool> IsInConsole;
        public SyncVar<bool> IsWindowFocused;
        public SyncVar<bool> IsChatFocused;
        public SyncVar<bool> IsInCraftingMenu;
        public SyncVar<bool> IsInTradingMenu;
        public SyncVar<bool> ButtonAttack;
        public SyncVar<bool> ButtonIteminteract;
        public SyncVar<knetid> WoundviewTargetNetBodyId;
        public SyncVar<knetid> DragItemId;

        public SyncVar<knetid> MinigameCurrentItemId;
        public SyncVar<AnyObjectNetId> MinigameTargetObjectId;
        public SyncVar<byte> MinigameTypeId;
        public SyncVar<ushort> MinigameSessionId;

        public SyncVar<bool> MuteVC;
        public SyncVar<bool> MuteTC;
        public SyncVar<bool> IsInInventory;

        public void ApplyInput() {
            player.IsInPauseMenu = IsInPauseMenu.Value;
            player.IsInConsole = IsInConsole.Value;
            player.IsAltTab = !IsWindowFocused.Value;
            player.IsChatFocused = IsChatFocused.Value;
            player.IsInCraftingMenu = IsInCraftingMenu.Value;
            player.ButtonAttack = ButtonAttack.Value;
            player.ButtonIteminteract = ButtonIteminteract.Value;

            player.IsInTradingMenu = IsInTradingMenu.Value;
            player.IsInInventory = IsInInventory.Value;
            player.WoundviewTargetNetBodyId = WoundviewTargetNetBodyId.Value;
            player.DragItemId = DragItemId.Value;
        }

        protected override void RegisterRPC(ref RPCRegistrator r) {
            base.RegisterRPC(ref r);

            r.BindOnChange(this, ref playerColor, _ => OnNameChange(), BindOnChangeFlags.ExecuteOnSync);
            r.BindOnChange(this, ref CursorPos, _ => OnCursorMove(), BindOnChangeFlags.ExecuteOnSync);
        }

        private void OnCursorMove() {
            if (player != null && player.ServerPlayerState != null) {
                player.ServerPlayerState.last_cursormove = Time.realtimeSinceStartupAsDouble;
            }
        }

        private void OnNameChange() {
#if !RELEASE
            log.l($"BasePlayer.OnNameChange  {playerName.Value} - {playerColor.Value}");
#endif
            if (player != null) {
                player.ApplyNameAndColor(playerName.Value, playerColor.Value);
            }
        }

        public ScavPlayer player { get; private set; }
        internal NetPlayer serverNetPlayer;

        private static ScavPlayer CreatePlayerPrefab(BasePlayer playerEntity, string playerName, Color24 playerColor) {
            if (Net.TryGetSteamTransport(out _)) {
                ulong steamId = playerEntity.playerSteamId.Value;
                if (!KSteam.CURRENT_LOBBY.members.ContainsKey((Steamworks.CSteamID)steamId)) {
                    // 拦截伪造不在当前 Steam 大厅成员列表内的 SteamID 玩家创建请求
                    if (log.verbose) {
                        log.warn($"Player SteamID is not in member list: {steamId}");
                    }
                    KSteam.UpdateLobbyInfo((Steamworks.CSteamID)KSteam.CURRENT_LOBBY.lobby_steamID, ref KSteam.CURRENT_LOBBY);
                    return null;
                }
            }

            GameObject plrPrefab = new GameObject("Player");
            plrPrefab.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(plrPrefab);
            ScavPlayer player = plrPrefab.AddComponent<ScavPlayer>();
            playerEntity.player = player;

            if (playerEntity.OwnerId == 0) {
                if (Net.IsServer) {
                    player.IsLocal = true;
                }
                player.IsHost = true;
            } else if (playerEntity.IsLocalControlled) {
                player.IsLocal = true;
                ScavPlayer.LocalPlayer = player;
            }

            player.playerId = playerEntity.OwnerId;
            player.playerName = playerName;
            player.playerColor = playerColor;
            player.BasePlayer = playerEntity;
            player.SteamId = playerEntity.playerSteamId.Value;

            player.ApplyNameAndColor(player.playerName, player.playerColor);
            return player;
        }

        internal static BasePlayer ServerCreatePlayer(string playerName, ulong steamId, Color24 playerColor, bool isHost) {
            NetPlayer serverPlayer = null;
            if (!isHost) {
                LESKrokNetServerPeer lesPeer = new LESKrokNetServerPeer();
                serverPlayer = Net.ServerEntityManager.AddPlayer(lesPeer);
                lesPeer.plrId = serverPlayer.Id;
            }
            BasePlayer playerEntity = Net.ServerEntityManager.AddEntity<BasePlayer>(e => {
                e.playerSteamId.Value = steamId;
                e.playerName.Value = playerName;
                e.playerColor.Value = playerColor;
            });
            playerEntity.serverNetPlayer = serverPlayer;
            if (!isHost) {
                Net.ServerEntityManager.AddController<BasePlayerController>(serverPlayer, playerEntity);
            }

            _ = CreatePlayerPrefab(playerEntity, playerName, playerColor);

#if !RELEASE
            log.l($"LiteEntitySystem: Player joined: \"{playerEntity}\" - {playerEntity.player}");
#endif
            return playerEntity;
        }

        public BasePlayer(EntityParams entityParams) : base(entityParams) {
            MinigameTypeId.Value = 255;
        }

        protected override void OnConstructed() {
            base.OnConstructed();

            CreatePlayerGameObject();
            playerName.ValueChanged += _ => OnNameChange();
        }

        private void CreatePlayerGameObject() {
            if (player == null && Net.IsClient) {
                if (Net.TryGetSteamTransport(out _) && playerSteamId.Value == 0) {
                    return;
                }
                _ = CreatePlayerPrefab(this, playerName.Value, playerColor.Value);
            }
        }

        protected override void OnLateConstructed() {
            base.OnLateConstructed();
        }

        protected override void OnSyncGroupsChanged(SyncGroup enabledGroups) {
            base.OnSyncGroupsChanged(enabledGroups);
        }

        protected override void OnLagCompensationStart() {
            base.OnLagCompensationStart();
        }

        protected override void OnLagCompensationEnd() {
            base.OnLagCompensationEnd();
        }

        protected override void OnDestroy() {
            base.OnDestroy();

            if (player != null) {
                Net.RemoveServerPlayer(serverNetPlayer);
                serverNetPlayer = null;

                player.BasePlayer = null;
                Object.Destroy(player.gameObject);
                player = null;
            }
        }

        protected override void Update() {
            base.Update();

            if (player != null) {
                if (IsServer && IsLocalControlled) {
                    PlayerInputPacket nextCommand = new PlayerInputPacket();
                    nextCommand.CollectInput();
                    nextCommand.SetInput(this);
                    ApplyInput();
                }
            } else {
                CreatePlayerGameObject();
            }
        }

        protected override void VisualUpdate() {
            base.Update();

            if (player != null && !IsServer && !IsLocalControlled) {
                ApplyInput();
            }
        }
    }
}
