#if FULLGAME
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版联机多线并发结局核心协调器（状态机与本机结局演出协程分部）：
/// 雇佣兵结局、芯片培养舱结局与无芯片逃生手术在各自客户端独立推进；
/// 仅当无芯片玩家实际击毙科学家开启逃生时才会引爆在场芯片玩家的项圈，且大厅等所有活跃结局结算完毕后统一返回主菜单。
/// </summary>
public static partial class ConcurrentEndings {
    public static bool ScientistDead;
    public static bool LocalMercResolved;
    public static bool LocalChipDetonateStarted;
    public static bool LocalWoncharGrantedForEndLab;
    public static bool ApplyingScientistShot;
    /// <summary>标记死亡芯片/雇佣兵终局演出已锁存，阻止终局实验室重复治愈并触发二轮结算卡。</summary>
    public static bool FinaleLatched;
    public static bool SuppressDeathScreen;
    public static bool KeepSpectating;
    /// <summary>允许芯片玩家在背景文本结束后显示一次培养舱结算回执，随后切入旁观模式。</summary>
    public static bool AllowChippedReceipt;
    /// <summary>本机结局结算卡是否正在展示中。</summary>
    public static bool EndingCardVisible;

    static readonly HashSet<knetid> _ascentDone = new HashSet<knetid>();
    private static readonly HashSet<knetid> _endingReceived = new HashSet<knetid>();
    private static readonly HashSet<knetid> _cutsceneFinished = new HashSet<knetid>();
    private static bool _unchippedEscapeFinished;
    private static bool _lobbyReturnStarted;
    static bool _ascentCutsceneStarted;
    static float _surgeryTpNext;
    static bool _anestheticBagsSpawned;
    internal static readonly Vector2 SurgeryEntrance = new Vector2(250f, 234f);
    internal static readonly Vector2 SurgeryRoom = new Vector2(287f, 243f);

    /// <summary>手术所需局部麻醉剂最低毫升数（原版每毫升折算 6 秒持续时间）。</summary>
    public const float SurgeryAnestheticMlRequired = 1f;
    public static float SurgeryAnestheticDurationRequired => 60f * (SurgeryAnestheticMlRequired * 0.1f);

    public static bool IsLocalUnchipped() {
        if (ScavPlayer.LocalPlayer != null) {
            return ScavPlayer.LocalPlayer.IsUnchipped();
        }
        return Util.IsUnchipped();
    }

    public static bool LocalHasMercScarf() {
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        return body != null && body.FindByIdThorough("mercscarf", out _);
    }

    public static void GrantWoncharForLocalCharacter() {
        int chr = -1;
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            chr = CharacterSpawn.ClampChar(body.charType);
        } else if (ScavPlayer.LocalPlayer != null) {
            chr = CharacterSpawn.ClampChar(ScavPlayer.LocalPlayer.selectedCharacter);
        }
        if (chr < 0) {
            return;
        }
        PlayerPrefs.SetInt("wonchar" + chr, 1);
        SteamManager.TryUnlockAchievement("CHARWIN" + chr);
    }

    /// <summary>无芯片玩家完成培养舱/逃生结局后广播解锁全队各自参战角色的通关记录。</summary>
    public static void AnnounceEndingCredit() {
        if (!Net.IsRunning) {
            return;
        }
        if (Net.IsServer) {
            SendEndingCredit(ServerMain.AllClientIdsExceptHost);
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_EndingCredit);
        writer.Put(true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static void SendEndingCredit(IReadOnlyList<knetid> who) {
        if (who == null || who.Count == 0) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_EndingCredit);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, who);
    }

    /// <summary>携带芯片的玩家抵达第 11 层终局实验室时立即提前授予通关角色解锁。</summary>
    public static void TryGrantEndLabWoncharEarly() {
        if (!Net.IsRunning || LocalWoncharGrantedForEndLab) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            return;
        }
        if (IsLocalUnchipped()) {
            return;
        }
        LocalWoncharGrantedForEndLab = true;
        GrantWoncharForLocalCharacter();
        FindTheBugInator.Mark("endlab-wonchar-early");
    }

    public static void ResetLayerLocalState() {
        ScientistDead = false;
        LocalMercResolved = false;
        LocalChipDetonateStarted = false;
        FinaleLatched = false;
        SuppressDeathScreen = false;
        KeepSpectating = false;
        AllowChippedReceipt = false;
        EndingCardVisible = false;
        _endingReceived.Clear();
        _cutsceneFinished.Clear();
        _unchippedEscapeFinished = false;
        _lobbyReturnStarted = false;
        MacroFleshRebuilderMp.ResetLayer();
        ScientistGate.Reset();
        _ascentDone.Clear();
        _ascentCutsceneStarted = false;
        _surgeryTpNext = 0f;
        _anestheticBagsSpawned = false;
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            LocalWoncharGrantedForEndLab = false;
        }
    }

    public static void ApplyScientistDeathVisuals(EndLabScript lab) {
        if (lab == null) {
            return;
        }
        ScientistDead = true;
        if (lab.scientistCoroutine != null) {
            lab.StopCoroutine(lab.scientistCoroutine);
            lab.scientistCoroutine = null;
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = "";
        }
        lab.doingEscape = true;
        if (lab.scientistAnim != null) {
            lab.scientistAnim.enabled = true;
            lab.scientistAnim.Play("ScientistShot");
            Collider2D col = lab.scientistAnim.GetComponent<Collider2D>();
            if (col != null) {
                col.enabled = false;
            }
            Utils.Create("HumanBlood", lab.scientistAnim.transform.position, 0f);
        }
        if (lab.scientistTalker != null) {
            lab.scientistTalker.Skip();
        }
        Sound.Play("cut5", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        if (IsLocalUnchipped()) {
            PlayerCamera cam = PlayerCamera.main;
            if (cam != null) {
                cam.blockItemUsage = false;
                if (cam.body != null) {
                    cam.body.forceWalk = false;
                    cam.body.forceNoMovement = false;
                }
            }
            if (WorldGeneration.world != null) {
                WorldGeneration.world.cutsceneActive = false;
            }
            SharedMain.IsInCutscene = false;
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
            MusicManager.main.StopSong();
            MusicManager.main.StartCoroutine(MusicManager.main.EscapeMusic());
        }
        EndingCarry.DropIllegal();
    }

    static bool SurgeryDoorPulls(Body body) {
        if (body == null || !body.TryGetNetBody(out NetBody nb) || nb == null) {
            return false;
        }
        if (nb.plr != null) {
            return nb.plr.IsUnchipped();
        }
        return nb.unchipped;
    }

    public static void HostTeleportIntoSurgery(Body body) {
        if (!Net.IsServer || body == null || !body.alive || !SurgeryDoorPulls(body)) {
            return;
        }
        if (Vector2.Distance(body.transform.position, SurgeryRoom) < 4f) {
            return;
        }
        if (body.TryGetNetBody(out NetBody nb)) {
            nb.SetBodyPosition(SurgeryRoom);
            body.SetVelocity(Vector2.zero);
            nb.Server_RemindPlayersCurrentState(true);
        } else {
            body.transform.position = SurgeryRoom;
            body.SetVelocity(Vector2.zero);
        }
        if (PlayerCamera.main != null && PlayerCamera.main.body == body) {
            PlayerCamera.main.transform.position = body.transform.position;
        }
        FindTheBugInator.Mark("surgery-tp");
    }

    /// <summary>任一无芯片玩家跨过手术室门时，将其余存活的无芯片队友一并拉入手术室（带芯片玩家留在原地）。</summary>
    public static void HostTeleportAllIntoSurgery() {
        if (!Net.IsServer) {
            return;
        }
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            HostTeleportIntoSurgery(pair.Key);
        }
        FindTheBugInator.Mark("surgery-tp-all");
    }

    static bool AnyPlayerNearSurgeryDoor() {
        foreach (var pair in ScavPlayer.BodyToPlayerDict) {
            Body b = pair.Key;
            if (b == null || !b.alive) {
                continue;
            }
            Vector2 p = b.transform.position;
            if (Vector2.Distance(p, SurgeryEntrance) < 10f || Vector2.Distance(p, SurgeryRoom) < 6f) {
                return true;
            }
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null || !local.alive) {
            return false;
        }
        Vector2 lp = local.transform.position;
        return Vector2.Distance(lp, SurgeryEntrance) < 10f || Vector2.Distance(lp, SurgeryRoom) < 6f;
    }

    static void RequestSurgeryTeleport(Body body) {
        if (body == null) {
            return;
        }
        if (Net.IsServer) {
            HostTeleportAllIntoSurgery();
            return;
        }
        if (Time.unscaledTime < _surgeryTpNext) {
            return;
        }
        _surgeryTpNext = Time.unscaledTime + 0.75f;
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.SurgeryTp);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    /// <summary>原版仅生成一袋麻药，联机下按房间人数围绕出生点环形补刷独立同步的麻醉剂袋。</summary>
    public static void HostSpawnExtraAnestheticBags(EndLabScript lab) {
        if (!Net.IsServer || lab == null || lab.injector == null || _anestheticBagsSpawned) {
            return;
        }
        _anestheticBagsSpawned = true;
        int players = ScavPlayer.BodyToPlayerDict.Count;
        if (players < 1) {
            players = 1;
        }
        GameObject src = lab.injector.gameObject;
        Vector3 basePos = src.transform.position;
        Quaternion rot = src.transform.rotation;
        for (int i = 0; i < players; i++) {
            float angle = (i + 0.5f) * (Mathf.PI * 2f / players);
            Vector3 pos = basePos + new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.85f;
            GameObject go = UnityEngine.Object.Instantiate(src, pos, rot);
            // 销毁从场景原件克隆过来的旧网络追踪器，确保 SafeNewGO 为新麻药袋分配独立 SyncId
            SyncInfoGameObjectTracker[] trackers = go.GetComponentsInChildren<SyncInfoGameObjectTracker>(true);
            for (int t = 0; t < trackers.Length; t++) {
                UnityEngine.Object.DestroyImmediate(trackers[t]);
            }
            FreshItemDrop existingDrop = go.GetComponent<FreshItemDrop>();
            if (existingDrop != null) {
                UnityEngine.Object.Destroy(existingDrop);
            }
            WaterContainerItem wci = go.GetComponent<WaterContainerItem>();
            if (wci != null) {
                wci.DrainAll();
                wci.AddLiquid("localanesthetic", 347f);
            }
            FreshItemDrop drop = go.AddComponent<FreshItemDrop>();
            drop.forceOn = true;
            NetObjectRegistry.SafeNewGO(go);
        }
        FindTheBugInator.Mark("surgery-anes-bags", players.ToString());
    }

    public static bool HasEnoughAbdomenAnesthetic() {
        float need = SurgeryAnestheticDurationRequired;
        CoUtils local = CoUtils.instance;
        if (local != null && local.DurationOf("localanestheticDownTorso") >= need) {
            return true;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            return false;
        }
        CoUtils onBody = body.GetComponent<CoUtils>();
        if (onBody == null) {
            return false;
        }
        if (onBody.DurationOf("localanestheticDownTorso") >= need) {
            return true;
        }
        const int lowerTorsoIdx = (int)LimbIndex.LowerTorso;
        if (body.limbs != null && body.limbs.Length > lowerTorsoIdx && body.limbs[lowerTorsoIdx] != null) {
            string named = "localanesthetic" + body.limbs[lowerTorsoIdx].name;
            if (onBody.DurationOf(named) >= need) {
                return true;
            }
        }
        return false;
    }

    public static IEnumerator MpEscapeToSurgery(EndLabScript lab) {
        yield return new WaitForSeconds(5f);
        if (lab.announcer != null) {
            lab.announcer.Talk(Locale.GetOther("escapealert"));
        }
        yield return new WaitUntil(AnyPlayerNearSurgeryDoor);
        if (GlobalDark.main != null) {
            GlobalDark.main.Darken();
            yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        float waited = 0f;
        while (body != null && Vector2.Distance(body.transform.position, SurgeryRoom) > 4f) {
            if (waited <= 0f) {
                RequestSurgeryTeleport(body);
                waited = 0.75f;
            }
            waited -= Time.deltaTime;
            yield return null;
            body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        }
        if (body == null || lab == null) {
            yield break;
        }
        if (PlayerCamera.main != null) {
            PlayerCamera.main.transform.position = body.transform.position;
        }
        if (lab.endGuardSpawner != null) {
            lab.endGuardSpawner.openProgress = 1.45f;
        }
        Sound.Play("endlabDoor", body.transform.position, twoDimensional: false, pitchShift: false);
        lab.doTimer = true;
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
            MusicManager.main.specialLoop = true;
            MusicManager.main.PlaySong(lab.end1);
        }
        if (lab.injector != null) {
            FreshItemDrop drop = lab.injector.GetComponent<FreshItemDrop>();
            if (drop == null) {
                drop = lab.injector.gameObject.AddComponent<FreshItemDrop>();
            }
            drop.forceOn = true;
            if (Net.IsServer) {
                HostSpawnExtraAnestheticBags(lab);
            } else {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CustomWireCommands.SurgeryBags);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery1");
        }
        yield return new WaitUntil(HasEnoughAbdomenAnesthetic);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery2");
        }
        lab.canStartSurgery = true;
        yield return new WaitUntil(() => (bool)MinigameBase.main.currentMinigame && MinigameBase.main.currentMinigame is CargoMinigame);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery3");
        }
        CargoMinigame minigame = MinigameBase.main.currentMinigame as CargoMinigame;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return ((bool)minigame && minigame.stage == 1) || (b != null && !b.hasCargo);
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery4");
        }
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && !b.hasCargo;
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery5");
        }
        if (lab.removedHeart != null) {
            lab.removedHeart.SetActive(true);
        }
        yield return new WaitUntil(() => MinigameBase.main == null || !MinigameBase.main.currentMinigame);
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery6");
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(lab.end2);
        }
        if (lab.surgicalDevice != null) {
            MacroFleshRebuilderMp.EnsureSurgicalDeviceUsable();
        }
        lab.doTimer = false;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.x > 353f;
        });
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = "";
        }
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            yield break;
        }
        body.forceWalk = true;
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.x > 366f;
        });
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            yield break;
        }
        body.forceWalk = false;
        body.Ragdoll();
        body.shock = 50f;
        body.Scream();
        if (lab.doorJoint != null) {
            UnityEngine.Object.Destroy(lab.doorJoint);
        }
        Sound.Play("robothit", body.transform.position, twoDimensional: false, pitchShift: false);
        yield return new WaitUntil(() => {
            Body b = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            return b != null && b.transform.position.y > 270f && b.transform.position.x < 370f;
        });
        if (GlobalDark.main != null) {
            GlobalDark.main.Darken();
            yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        }
        body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceNoMovement = true;
        }
        if (WorldGeneration.world != null) {
            WorldGeneration.world.StartCoroutine(WorldGeneration.world.EscapeEnding());
        }
    }

    public static void OnScientistShotLocal() {
        EndLabScript lab = EndLabScript.instance;
        if (lab == null) {
            return;
        }
        bool already = ScientistDead && lab.doingEscape;
        ApplyScientistDeathVisuals(lab);
        FindTheBugInator.Mark("scientist-shot", already ? "already" : "first");
        if (already) {
            return;
        }

        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceWalk = false;
            body.talker.Talk(Locale.GetCharacter("labshoot"), null, force: false, resetTalkTimer: true);
        }

        if (IsLocalUnchipped()) {
            bool alive = body != null && body.alive;
            if (alive && lab.escapeCoroutine == null) {
                lab.escapeCoroutine = lab.StartCoroutine(MpEscapeToSurgery(lab));
            } else if (!alive) {
                ReportEnding("done");
            }
        } else if (!LocalChipDetonateStarted) {
            LocalChipDetonateStarted = true;
            lab.StartCoroutine(DelayedChipDetonate());
        }
    }

    public static void LatchDeadFinale() {
        FinaleLatched = true;
        SuppressDeathScreen = true;
        FindTheBugInator.Mark("finale-latch");
        if (WorldGeneration.world != null) {
            WorldGeneration.world.cutsceneActive = true;
        }
    }

    public static void EnterFinaleSpectator() {
        KeepSpectating = true;
        SuppressDeathScreen = true;
        FindTheBugInator.Mark("finale-spectate");
        if (PlayerCamera.main != null && PlayerCamera.main.endScreen != null) {
            PlayerCamera.main.endScreen.gameObject.SetActive(false);
        }
        if (!UIInGame.SpectatorModeActive) {
            UIInGame.StartSpectatorMode();
        }
    }

    public static IEnumerator EmptyFinale() {
        yield break;
    }

    public static IEnumerator DeadPlayerLoreThenSpectate(string loreKey) {
        LatchDeadFinale();
        ReportEnding("recv");
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null && body.alive && PlayerCamera.main != null) {
            PlayerCamera.main.StartSelfDestruct();
            float wait = 8f;
            while (wait > 0f && body.alive) {
                wait -= Time.unscaledDeltaTime;
                yield return null;
            }
        }
        if (PlayerCamera.main != null && PlayerCamera.main.endScreen != null) {
            PlayerCamera.main.endScreen.gameObject.SetActive(false);
        }
        if (WorldGeneration.world != null) {
            WorldGeneration.world.cutsceneActive = true;
        }
        SharedMain.IsInCutscene = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther(loreKey), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        WorldGeneration world = WorldGeneration.world;
        if (world != null && world.cutsceneUnder != null && world.cutsceneImages != null && world.cutsceneImages.Length > 7) {
            world.cutsceneUnder.sprite = world.cutsceneImages[7];
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
            MusicManager.main.specialLoop = false;
        }
        ScrollableText.closeType = ScrollableText.TextCloseType.Instant;
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null) {
            AllowChippedReceipt = true;
            yield return cam.StartCoroutine(cam.EndSequence(1));
        }
        if (!UIInGame.SpectatorModeActive) {
            EnterFinaleSpectator();
        }
    }

    static IEnumerator DelayedChipDetonate() {
        LatchDeadFinale();
        yield return new WaitForSecondsRealtime(Random.Range(1f, 5f));
        string loreKey = "biopodnotealt";
        EndLabScript lab = EndLabScript.instance;
        if (lab != null && lab.doingEscape) {
            loreKey = "biopodnotealt2";
        }
        yield return DeadPlayerLoreThenSpectate(loreKey);
    }

    public static IEnumerator ChippedBiopodDialogue(string loreKey) {
        LatchDeadFinale();
        ReportEnding("recv");
        if (ScientistDead && !LocalChipDetonateStarted) {
            LocalChipDetonateStarted = true;
            Body dying = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (dying != null && dying.alive && PlayerCamera.main != null) {
                PlayerCamera.main.StartSelfDestruct();
            }
        }
        WorldGeneration world = WorldGeneration.world;
        if (world != null) {
            world.cutsceneActive = true;
        }
        SharedMain.IsInCutscene = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther(loreKey), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        if (world != null && world.cutsceneUnder != null && world.cutsceneImages != null && world.cutsceneImages.Length > 7) {
            world.cutsceneUnder.sprite = world.cutsceneImages[7];
        }
        if (MusicManager.main != null) {
            MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
            MusicManager.main.specialLoop = false;
        }
        ScrollableText.closeType = ScrollableText.TextCloseType.Instant;
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null) {
            AllowChippedReceipt = true;
            yield return cam.StartCoroutine(cam.EndSequence(1));
        }
        if (ScientistDead && !UIInGame.SpectatorModeActive) {
            EnterFinaleSpectator();
        }
    }

    /// <summary>防止实验室中普通昏迷误触发芯片结局：挂起检查直到玩家苏醒或真正发生逃生枪击。</summary>
    public static IEnumerator IgnoreEarlyBrainBiopod() {
        WorldGeneration world = WorldGeneration.world;
        if (world == null) {
            yield break;
        }
        bool changed = false;
        if (!world.cutsceneActive) {
            world.cutsceneActive = true;
            changed = true;
        }
        while (!ScientistDead) {
            Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            bool dying = body != null && body.bloodPressure < 10f && body.consciousness < 5f;
            if (!dying) {
                break;
            }
            yield return null;
        }
        if (changed && !ScientistDead && !FinaleLatched) {
            world.cutsceneActive = false;
        }
    }

    public static IEnumerator MpMercEnding(WorldGeneration world) {
        LatchDeadFinale();
        ReportEnding("recv");
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        Time.timeScale = 1f;
        world.cutsceneActive = true;
        SharedMain.IsInCutscene = true;
        Body body = PlayerCamera.main.body;
        body.forceNoMovement = true;
        MusicManager.main.StopSong();
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther("mercnote1"), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        GlobalDark.main.SetSpeedMultiplier(0.1f);
        world.cutsceneUnder.gameObject.SetActive(true);
        world.cutsceneUnder.sprite = world.cutsceneImages[9];
        yield return new WaitForSecondsRealtime(3f);
        yield return new WaitUntil(() => Input.GetKeyDown(KeyBinds.GetBind("attack")));
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        ScrollableText.CreateText(Locale.GetOther("mercnote2"), cover: true);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/endlabdrone"));
        MusicManager.main.specialLoop = true;
        world.cutsceneUnder.gameObject.SetActive(false);
        world.cutsceneOver.gameObject.SetActive(false);
        world.cutsceneUnder.sprite = world.cutsceneImages[11];
        yield return new WaitUntil(() => ScrollableText.onLastPanel || !ScrollableText.textExists || GlobalDark.main.IsDarkening());
        MusicManager.main.StopSong();
        float timer = 5f;
        while (timer > 0f && ScrollableText.textExists && !GlobalDark.main.IsDarkening()) {
            timer -= Time.unscaledDeltaTime;
            yield return null;
        }
        Sound.Play("heartsplosion", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        GlobalDark.main.SetDarkAmount(1f);
        GlobalDark.main.SetStayDark(stay: true);
        GlobalDark.main.Darken();
        yield return null;
        ScrollableText.ForceClose();
        yield return new WaitForSecondsRealtime(4f);
        GlobalDark.main.SetStayDark(stay: false);
        ScrollableText.CreateText(Locale.GetOther("mercnote3"), cover: true);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/biopod"));
        MusicManager.main.specialLoop = false;
        SteamManager.TryUnlockAchievement("TOBECONTINUED");
        GrantWoncharForLocalCharacter();
        PlayerPrefs.SetInt("didmerc", 1);

        world.cutsceneActive = false;
        SharedMain.IsInCutscene = false;
        body.forceNoMovement = false;
        LocalMercResolved = true;

        if (PlayerCamera.main != null) {
            PlayerCamera.main.StartSelfDestruct();
        }
        yield return new WaitForSecondsRealtime(5f);
        ReportEnding("done");
        EnterFinaleSpectator();
    }

    public static IEnumerator MpSurfaceBreachThenRequestLab(WorldGeneration world) {
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        Time.timeScale = 1f;
        world.cutsceneActive = true;
        SharedMain.IsInCutscene = true;
        yield return new WaitUntil(() => PlayerCamera.main != null && PlayerCamera.main.body != null);
        Body body = PlayerCamera.main.body;
        body.forceNoMovement = true;
        ScrollableText.closeType = ScrollableText.TextCloseType.OnFade;
        ScrollableText.CreateText(Locale.GetOther("surfacebreachnote"), cover: true);
        MusicManager.main.cutsceneVolMult = 1f;
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachA"));
        MusicManager.main.specialLoop = true;
        yield return new WaitUntil(() => !ScrollableText.textExists);
        GlobalDark.main.SetSpeedMultiplier(0.1f);
        MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/surfacebreachB"));
        MusicManager.main.specialLoop = false;
        world.cutsceneUnder.gameObject.SetActive(true);
        world.cutsceneUnder.sprite = world.cutsceneImages[6];
        world.cutsceneOver.gameObject.SetActive(true);
        world.cutsceneOver.sprite = world.cutsceneImages[body.charType];
        yield return new WaitForSecondsRealtime(3f);
        yield return new WaitUntil(() => Input.GetKeyDown(KeyBinds.GetBind("attack")));
        GlobalDark.main.SetSpeedMultiplier(1f);
        GlobalDark.main.Darken();
        yield return new WaitUntil(() => !GlobalDark.main.IsDarkening());
        ScrollableText.CreateText(Locale.GetOther("labenternote"), cover: true);
        world.cutsceneUnder.gameObject.SetActive(false);
        world.cutsceneOver.gameObject.SetActive(false);
        yield return new WaitUntil(() => !ScrollableText.textExists);
        MusicManager.main.StopSong();
        world.cutsceneActive = false;
        SharedMain.IsInCutscene = false;
        body.forceNoMovement = false;
        body.forceWalk = true;

        RequestAdvanceToEndLab();
    }
}
#endif
