using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Together.Api;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

public partial class NetBody {
    public static float DistanceForNearBodies = GameplayRanges.RagdollSyncGroupRadius;
    public const float default_body_temperature = 37f;

    public List<NetBody> NearBodies = new();
    public bool NearBodiesDistanceIs5 = false;
    protected bool last_alive = true;
    protected bool last_sleep = false;
    internal float soloSleepBonusStart = -1f;
    internal bool handSwitched;
    internal ushort handSwitchTick;
    /// <summary>标记本躯体实例是否已带着有效 <c>syncBody</c> 触发过 <see cref="TogetherPanelApi.NotifyBodyReadyOrRebuilt"/>，根绝早产通知与重建多连发。</summary>
    internal bool _notifiedBodyReady;

    public static void SpawnStarterPack(Body body) {
        if (WorldGeneration.world.totalTraveled <= 0
            && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.None
            && !SaveSystem.loadedRun) {
            switch (WorldGeneration.GetRunSettingInt("startingsupplies")) {
                case 1:
                    body.PickUpItem(Utils.Create("emergencylight", body.transform.position, 0f).GetComponent<Item>(), 3, force: true);
                    break;
                case 2:
                    body.PickUpItem(Utils.Create("lantern", body.transform.position, 0f).GetComponent<Item>(), 3, force: true);
                    body.PickUpItem(Utils.Create("dogfood", body.transform.position, 0f).GetComponent<Item>(), 4, force: true);
                    body.PickUpItem(Utils.Create("waterbottle", body.transform.position, 0f).GetComponent<Item>(), 5, force: true);
                    body.PickUpItem(Utils.Create("trashbag", body.transform.position, 0f).GetComponent<Item>(), 1, force: true);
                    break;
            }
            if (DateTime.Now.Month == 12) {
                Utils.Create("present", (Vector2)body.transform.position + Vector2.right * UnityEngine.Random.Range(-2f, 2f), 0f);
            }
        }
    }

    public static NetBody CreateNewNPC(Vector2 pos, string name = null, SyncBody sb = null) {
        _Internal_RegisterLiteNetLib(null, null, ref sb);
        NetBody nb = _Internal_CreateNetBody(sb, pos, name, null);
        nb.chara.name = $"NPC_ID_{sb.Id}_NAME_{name}";
        nb.ApplyNameAndColor(name, nb.color);
        return nb;
    }

    internal static NetBody CreateNewPlayerCharacter(ScavPlayer plr, SyncBody sb = null) {
        if (plr.body == null) {
            bool had_sb = sb != null;
            _Internal_RegisterLiteNetLib(null, plr, ref sb);

            if (sb.nb == null) {
                sb.nb = _Internal_CreateNetBody(sb, Body_PlaceBody_MultiplayerPatch.SpawnLocation, plr.playerName, plr);
            }
            NetBody nb = sb.nb;
            nb.SetPlayer(plr);

            if (Net.IsServer && had_sb) {
                string pid = plr.GetPersistentId();
                bool hasSavedPlrState = ServerMain.Server_LastPlayerStates.TryGetValue(pid, out _);
                bool hasPlayerFile = !SavesystemPatch.PreserveSave && !hasSavedPlrState && PlayerCheckpoint.HasPlayerFile(plr);
                bool hasSuitcase = hasSavedPlrState || hasPlayerFile;

                if (plr.LateJoin) {
                    bool endLab = WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11;
                    bool forceSpectate = (!hasSuitcase && Multiplayer.rules.LateJoinSpectate) || (endLab && !hasSuitcase);

                    if (forceSpectate) {
                        string why = endLab && !hasSuitcase
                            ? "late-joined EndLab without suitcase"
                            : "late-joined and LateJoinSpectate is on";
                        if (log.verbose) log.l($"Killing {plr} cuz {why}.");

                        plr.body.GetHead().Dismember();
                        plr.body.GetLowerTorso().Dismember();
                        plr.body.brainHealth = 0f;
                        plr.body.heartRate = 0f;
                        plr.body.bloodOxygen = 0f;
                        plr.body.bloodPressure = 0f;
                        MedicalSync.Server_QueueSendCharacterHealth(nb, true);

                        Util.CallLambdaWhen(() => {
                            if (!Util.IsInWorld()) return true;
                            return plr.ServerPlayerState.is_loaded_in;
                        }, () => {
                            Util.DelayCallLambda(1f, () => {
                                Multiplayer.Server_SendSimpleMessageToOneClient(NetmsgId.CLIENT_forcespectator, plr.playerId);
                                plr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("latejoin_spectator_alert"), true, true);
                            });
                        });
                    }
                }

                if (!hasSavedPlrState && !hasPlayerFile) {
                    SpawnStarterPack(plr.body);
                }
            }
            return nb;
        }

        _Internal_RegisterLiteNetLib(plr.netBody, plr, ref sb);
        return plr.netBody;
    }

    internal static void _Internal_RegisterLiteNetLib(NetBody nb, ScavPlayer plr, ref SyncBody sb) {
        if (sb == null && nb != null) {
            sb = nb.syncBody;
        }
        if (sb == null) {
            if (Net.IsServer) {
                sb = Net.ServerEntityManager.AddEntity<SyncBody>(e => {
                    e.nb = nb;
                    if (plr != null) {
                        e.SetParent(plr.BasePlayer);
#if FULLGAME
                        CharacterSpawn.ApplySelectedToSyncBody(e, plr);
#endif
                    }
                });
            } else {
                throw new Exception("Server Only.");
            }
        }
#if FULLGAME
        else if (Net.IsServer && plr != null) {
            CharacterSpawn.ApplySelectedToSyncBody(sb, plr);
        }
#endif

        if (nb != null && sb != null) {
            nb.syncBody = sb;
            _PRIV_NetIdToNetBody[sb.Id] = nb;
            // 若场景预置角色在 Start 时尚未绑定 SyncBody（当时 _notifiedBodyReady 为 false），在此绑定完成后补发唯一一次带有效 netId 的就绪通知
            if (nb.body != null && nb.isActiveAndEnabled && !nb._notifiedBodyReady) {
                nb._notifiedBodyReady = true;
                TogetherPanelApi.NotifyBodyReadyOrRebuilt(nb, nb.body.charType, isRebuild: false);
            }
        }
#if !RELEASE
        log.l($"_Internal_RegisterLiteNetLib: sbId:{sb?.Id} - {nb}  stack: {new StackTrace()}");
#endif
    }

    internal static NetBody _Internal_CreateNetBody(SyncBody sb, Vector2 pos, string name = null, ScavPlayer plr = null) {
#if !RELEASE
        log.l($"Creating Netbody: {sb} - {name}  stack: {new StackTrace()}");
#endif
        name ??= "bot_" + sb.Id;

#if FULLGAME
        int chr = CharacterSpawn.ResolveCharForPlayer(plr, sb);
        GameObject chara = CharacterSpawn.InstantiateCharacter(chr, name);
#else
        GameObject chara = UnityEngine.Object.Instantiate(ServerMain.CHARACTER_PREFAB, Vector3.zero, Quaternion.identity);
        chara.name = "Character_" + name;
        chara.SetActive(true);
#endif

        Body body = chara.GetComponentInChildren<Body>();
        body.name = "Body_" + name;
        body.targetLookPos = new Vector2(1000f, 460f);
        body.transform.position = pos;

#if FULLGAME
        body.charType = CharacterSpawn.ClampChar(chr);
        body.skills.Setup(body.charType);
        if (plr != null) {
            plr.selectedCharacter = (byte)body.charType;
        }
#endif

        NetBody nb = body.GetOrAddComponent<NetBody>();
        sb.nb = nb;
        if (sb != null) {
            nb.syncBody = sb;
#if FULLGAME
            if (Net.IsServer) {
                sb.characterSelected.Value = (byte)CharacterSpawn.ClampChar(chr);
            }
#endif
        }
        _PRIV_NetIdToNetBody[sb.Id] = nb;
        return nb;
    }

    internal static void DetachForMeshRebuild(NetBody npc) {
        if (npc == null) {
            return;
        }
        SyncBody sb = npc.syncBody;
        if (sb != null) {
            if (_PRIV_NetIdToNetBody.TryGetValue(sb.Id, out NetBody mapped) && ReferenceEquals(mapped, npc)) {
                _PRIV_NetIdToNetBody.Remove(sb.Id);
            }
            if (ReferenceEquals(sb.nb, npc)) {
                sb.nb = null;
            }
        }
        npc.syncBody = null;
        npc.plr = null;
    }

    public static void DestroyNPC(NetBody npc) {
        if (npc != null) {
            if (npc.IsPlayer) {
                ScavPlayer.AllLivingPlayers.Remove(npc.plr);
                ScavPlayer.AllDeadPlayers.Remove(npc.plr);
            }
            if (npc.IsBodyLocal()) {
                return;
            }
            if (npc.transform.parent != null) {
                Destroy(npc.transform.parent.gameObject);
            }
            Destroy(npc.gameObject);
        }
    }

    public static void DestroyNPC(Body body) {
        if (body != null) {
            if (body.transform.parent != null) {
                Destroy(body.transform.parent.gameObject);
            }
            Destroy(body.gameObject);
        }
    }

#if !RELEASE
    void OnGUI() {
        if (log.verbose) {
            int ogfont = GUI.skin.label.fontSize;
            Vector2 screenPos = Camera.main.WorldToScreenPoint((Vector2)body.targetLookPos);
            screenPos = new Vector2(screenPos.x, Screen.height - screenPos.y);

            const float bsize = 17f;
            const float elsize = 13f;
            float y = -elsize * 1.5f;

            GUI.skin.label.normal.textColor = Color.white;
            GUI.skin.label.fontSize = 10;
            GUI.Label(new Rect(screenPos.x, screenPos.y + y, 400f, bsize), $"CURSOR: {this}");

            GUI.skin.label.normal.textColor = Color.white;
            GUI.skin.label.fontSize = ogfont;
        }
    }
#endif

    void Update() {
        HugSession.TickVelocity();
        HeadpatSession.Tick();

        if (timeSinceRadUpdate > 0.05f) {
            irradiateIntensity -= Time.unscaledDeltaTime * ((irradiateIntensity > 1f) ? 2f : 0.5f);
        }
        timeSinceRadUpdate += Time.unscaledDeltaTime;

        if (plr == null && Net.IsRunning && IsLocal) {
            BackupLocalPlayerInit();
            if (log.verbose) log.l("Still searching for local SCI wtf");
        }

        bool suppressIdleSit = !idle_anim_allowed || carrying_person != null || piggybacking_on != null;
        if (suppressIdleSit) {
            if (body.idleTime + Time.deltaTime > 11f) {
                body.idleTime = 11f;
            }
            ClearSitAnims(body);
        }

        if (!Util.IsBodyLocal(body) && body.conscious) {
            Item item = body.GetItem(body.handSlot);
            if (prev_useiteminhand) {
                if (!item || !item.Stats.usableWithLMB || item.Stats.autoAttack) {
                    body.UseItemInHand();
                }
            } else if (prev_useiteminhand != useiteminhand) {
                if (!item || !item.Stats.usableWithLMB || !item.Stats.autoAttack) {
                    body.UseItemInHand();
                }
            }
            prev_useiteminhand = useiteminhand;
        }

        if (!body.alive) {
            body.halfMinuteCheckTime = 0f;
        }

        if (Net.IsServer) {
            TickScatterMoodPenalty();
        }
    }

    void TickScatterMoodPenalty() {
        if (body == null || !IsPlayer) {
            return;
        }
        if (!body.alive || !SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
            if (scatterMoodOffset != 0f || scatterMoodTick != 0f) {
                if (body.alive) {
                    ApplyScatterMoodOffset(0f);
                } else {
                    scatterMoodOffset = 0f;
                }
                scatterMoodTick = 0f;
            }
            return;
        }

        if (chip_punishment_protocol) {
            body.eyePanicTime = 1f;
            scatterMoodTick += Time.deltaTime;
            while (scatterMoodTick >= 5f) {
                scatterMoodTick -= 5f;
                ApplyScatterMoodOffset(Mathf.Max(scatterMoodOffset - 1f, -30f));
            }
            return;
        }

        if (scatterMoodOffset >= 0f) {
            scatterMoodOffset = 0f;
            scatterMoodTick = 0f;
            return;
        }
        scatterMoodTick += Time.deltaTime;
        while (scatterMoodTick >= 5f && scatterMoodOffset < 0f) {
            scatterMoodTick -= 5f;
            ApplyScatterMoodOffset(Mathf.Min(scatterMoodOffset + 2f, 0f));
        }
    }

    void ApplyScatterMoodOffset(float nextOffset) {
        float delta = nextOffset - scatterMoodOffset;
        scatterMoodOffset = nextOffset;
        if (delta == 0f || body == null) {
            return;
        }
        body.happiness = Mathf.Clamp(body.happiness + delta, -100f, 100f);
        last_mood = body.happiness;
        MedicalSync.Server_QueueSendCharacterHealth(this, false);
    }

    protected void LateUpdate() {
        HugSession.TickArms();

        if (piggybacking_on != null && body != null) {
            HandlePiggybackUpdate();
        }

        if (!last_alive && !body.alive) {
            body.temperature = Mathf.Lerp(36.6f, WorldGeneration.world.ambientTemperature, Mathf.Clamp01((float)((Time.timeAsDouble - timeOfDeath) / 30.0)));
            body.reversedControls = false;
        } else {
            if (!SharedMain.LocalWorldIsGenerated) return;

            if (body.happiness < last_mood && NearBodies.Count > 0) {
                int happier_body_count = 0;
                foreach (NetBody ob in NearBodies) {
                    if (ob.body.happiness > body.happiness) {
                        happier_body_count++;
                    }
                }
                float l = Mathf.Min(happier_body_count / 8f, 0.5f);
                body.happiness = Mathf.Lerp(body.happiness, last_mood, l);
            }

            last_mood = body.happiness;

            NetBody cprPacient = null;
            bool doCPR = IsPlayer && CPRHandler.PlayerIsPerformingCPR(plr, out cprPacient);
            if (doCPR) {
                if (plr != null) {
                    float targetHeight = plr.BasePlayer.ButtonAttack.Value ? 1f : 0f;
                    cprPress = Mathf.Lerp(cprPress, targetHeight, Time.deltaTime * CPRHandler.GetPushSpeed(this, cprPacient, plr.BasePlayer.ButtonAttack.Value));
                }
                if (cprPacient.body.IsRagdolling() || cprPacient.body.crouching) {
                    body.crouching = true;
                }
            } else if (lastCpr) {
                cprPress = 0f;
                body.armsAnimator.StopPlayback();
                body.armsAnimator.Play("Grounded");
            }
            lastCpr = doCPR;
        }
    }

    void SlowerUpdate() {
#if FULLGAME
        if (IsPlayer && body != null) {
            CharacterSpawn.EnsureBodySpecies(body);
        }
#endif
        for (int i = 0; i < localoverride_showInfection.Length; i++) {
            if (!body.limbs[i].infected) {
                localoverride_showInfection[i] = false;
            }
            if (body.limbs[i].infectionAmount > 25f) {
                localoverride_showInfection[i] = true;
            }
        }

        if (Net.IsServer && IsPlayer) {
            if (body.alive) {
                chip_punishment_protocol = SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive();
                if (chip_punishment_protocol) {
                    chip_punishment_protocol = SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(plr, out _, out _, out _);
                }
            } else {
                chip_punishment_protocol = false;
            }
        }

        NearBodies.Clear();
        float radiusSqr = DistanceForNearBodies * DistanceForNearBodies;
        foreach (NetBody other in all_instances) {
            if (other != null && other.IsPlayer && other != this && other.alive
                && KM.DistanceCheck_PreSqr(other.transform.position, transform.position, radiusSqr)) {
                NearBodies.Add(other);
            }
        }
        UpdateBodySleepShare();
        UpdateBodyTempShare();

        if (body.alive != last_alive) {
            butchering_dropmeat_skip_counter = 0;
            if (head.TryGetComponent(out FacialExpression face)) {
                face.eyeLight.enabled = body.alive;
            }

            if (!body.alive && last_alive) {
                OnDeath();
            } else {
                // 角色复活：退出本地观战并清理残留读条状态
                if (Util.IsBodyLocal(body)) {
#if FULLGAME
                    if (!ConcurrentEndings.KeepSpectating)
#endif
                        UIInGame.StopSpectatorMode();
                }
                body.GetCoUtilsInstance()?.CancelAll();
                if (Net.IsClient && !Multiplayer.rules.RespawnKeepSkills) {
                    body.ResetMind();
                }
            }
        }

        if (!Multiplayer.rules.DisableSleep) {
            if (body.sleeping && !last_sleep) {
                OnSleep();
                soloSleepBonusStart = Time.realtimeSinceStartup;
            } else if (!body.sleeping) {
                soloSleepBonusStart = -1f;
            }
        } else {
            soloSleepBonusStart = -1f;
        }

        if (IsBodyLocal() && !(Net.IsRunning && WorldMP.RunUnchippedLock.Locked)) {
            unchipped = Util.IsUnchipped();
        }

        last_sleep = body.sleeping;
        last_alive = body.alive;
    }

    void UpdateBodySleepShare() {
        NearBodiesDistanceIs5 = false;
        if (Multiplayer.rules.DisableSleep || !body.alive || !body.sleeping || NearBodies.Count == 0) {
            return;
        }
        foreach (NetBody onb in NearBodies) {
            if (onb != null && KM.DistanceCheck_PreSqr(transform.position, onb.transform.position, 30f)) {
                NearBodiesDistanceIs5 = true;
                return;
            }
        }
    }

    void UpdateBodyTempShare() {
        if (!body.alive) return;
        if (Net.IsRunning && !Net.IsServer) return;

        const float body_temperature_share_speed = 0.12f * 0.1f;
        BoxCollider2D bc = body.GetComponent<BoxCollider2D>();
        Collider2D[] touching = Physics2D.OverlapBoxAll(bc.transform.position, bc.size, 0f);
        foreach (Collider2D item2 in touching) {
            Body otherbody = item2.TryGetComponent<Limb>(out Limb limb) ? limb.body : null;
            if (otherbody != null && otherbody != body && otherbody.alive && otherbody.temperature > 20f) {
                float dist1 = otherbody.temperature - default_body_temperature;
                float dist2 = body.temperature - default_body_temperature;
                float goal = Math.Abs(dist2) > Math.Abs(dist1) ? dist1 : dist2;
                float avg = Mathf.Lerp(Mathf.Lerp(dist1, dist2, 0.5f), goal, 0.33f) + default_body_temperature;

                otherbody.temperature = Mathf.MoveTowards(otherbody.temperature, avg, body_temperature_share_speed);
                body.temperature = Mathf.MoveTowards(body.temperature, avg, body_temperature_share_speed);
                break;
            }
        }
    }

    void FixedUpdate() {
        HandlePiggybackUpdate();

        if (Net.IsRunning) {
            if (body.baseLimb.dismembered || ClientMain.IsInTemporaryWorldPause() || body.transform.position.y > 6000f) {
                body.moveDir = Vector2.zero;
                body.rb.velocity = Vector2.zero;
                body.rb.gravityScale = 0f;
            } else if (SharedMain.LocalWorldIsGenerating && !WorldMP.client_technically_finished_worldgen_now_just_waiting_for_server) {
                body.ForceStand();
                body.moveDir = Vector2.zero;
                body.rb.velocity = Vector2.zero;
                body.rb.gravityScale = 0f;
            }
        }

        if (!IsBodyLocal()) {
            if (Net.IsServer) {
                float time_since_last_packet = (float)(Net.ServerTick - _lastSyncPacketReceiveTick) / Net.ServerEntitySendRate;
                if (time_since_last_packet > 0.055f) {
                    body.moveDir = Vector2.zero;
                }
            }
        } else {
            moveDir = (SByteScalarVec2)body.moveDir;
        }
    }
}
