using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

public static class CoopKeybinds {
    const string prefix = "casualtiestogether_";
    public const string pointfingerat = prefix + "pointfingerat"; // keyCode.Mouse2
    public const string chat = prefix + "chat"; // keyCode.Slash
    public const string voicechat = prefix + "voicechat"; // keyCode.V
    public const string woundview = prefix + "woundview"; // KeyCode.R
    public const string push = prefix + "push"; // keyCode.F
    public const string hug = prefix + "hug"; // keyCode.H
    public const string carry = prefix + "carry"; // keyCode.O
    public const string inventory = prefix + "inventory"; // keyCode.I
    public const string piggyback = prefix + "piggyback"; // keyCode.P
    public const string showplrs = prefix + "showplrs"; // keyCode.LeftShift

    public static Dictionary<string, KeyCode> as_dict = new Dictionary<string, KeyCode> {
        { CoopKeybinds.pointfingerat, KeyCode.Mouse2 },
        { CoopKeybinds.chat, KeyCode.Slash },
        { CoopKeybinds.voicechat, KeyCode.V },
        { CoopKeybinds.woundview , KeyCode.R },
        { CoopKeybinds.push, KeyCode.F },
        { CoopKeybinds.hug, KeyCode.H },
        { CoopKeybinds.piggyback, KeyCode.P },
        { CoopKeybinds.carry, KeyCode.O },
        { CoopKeybinds.inventory, KeyCode.I },
        { CoopKeybinds.showplrs, KeyCode.LeftShift },
    };
    public static bool IsWoundViewSameAsOG() {
        return KeyBinds.GetBind(CoopKeybinds.woundview) == KeyBinds.GetBind("woundview");
    }
}

[HarmonyPatch(typeof(InvButton), "body", MethodType.Getter)]
public static class InvButton_get_body_MultiplayerPatch {
    public static Body focused_body;
    public static Vector2 focused_body_position_smooth = Vector2.zero;

#if !RELEASE
    static bool _getter_working_announced = false;
    static void Prefix() {
        if (!_getter_working_announced) {
            _getter_working_announced = true;
            Plugin.log.LogInfo($"MethodType.Getter  is working: {new StackTrace()}");
        }
    }
#endif

    public static void Postfix(InvButton __instance, ref Body __result) {
        if (Net.IsRunning) {
            if (!PlayerCamera.main.radialOpen || focused_body == PlayerCamera.main.body) {
                focused_body = null;
            }
            __result = focused_body ?? PlayerCamera.main.body;
        }
    }
}

public partial class ClientMain : MonoBehaviour {

    public static int LOCAL_PING = -1; // in ms
    public static float RTT_IN_SECONDS_with_margin => (float)LOCAL_PING * (0.00401f); // 0.00202f

    public static float AdaptiveSyncTimerDelta = 0.03f;
    public static float ServerPerformanceScale = 1f;

    public static int SERVER_FPS = 60;
    public static int SERVER_TPS = 60;
    public static byte MY_CONNECTION_QUALITY = 0;

    public static ServerWorldStateSingleton ServerWorldState => Net.EntityManager?.GetSingleton<ServerWorldStateSingleton>();

    public static bool ServerIsGeneratingWorld {
        get {
            return ServerWorldState?.server_is_generating_world.Value ?? false;
        }
        set {
            var state = ServerWorldState;
            if (state != null) {
                state.server_is_generating_world.Value = value;
            }
        }
    }
    public static bool IsInTemporaryWorldPause() {
        return (SharedMain.LocalWorldIsGenerating || (Net.IsClient && ClientMain.ServerIsGeneratingWorld));
    }

    public static void DoVerboseObjInfo(Component itemorbuild, ref string ttname, ref string ttdesc) {
        if (log.verbose) {
            if (itemorbuild == null) {
                return;
            }
            ttname += "</color><color=#ffffffff>";
            if (NetObjectRegistry.TryGetSyncInfo(itemorbuild, out var si)) {
                ttname += $" (netID: {si.syncId})";
                ttdesc = $"{ttdesc}\nIGNORE TICK: {(Net.ServerTick - si.lastUpdateTick)}";
#if !RELEASE
                if (Net.ClientEntityManager != null) {
                    ttdesc += $"\nPROC TICK: {(Net.ClientEntityManager.LastProcessedTick)}";
                }
                ttdesc += $"\nPOS: {si._position.Value}";
                ttdesc += $"\nINTPOS: {si._position.InterpolatedValue}";
#endif
                ttdesc += $"\nRELAX: {si.relaxed_combat_sync}";
            } else {
                if (NetObjectRegistry.ObjectShouldNotBeSynced(itemorbuild.gameObject)) {
                    ttname += $" (netID: IGNORED)";
                } else {
                    ttname += $" (netID: NONE??? BUG!!!!!)";
                }
            }
            if (itemorbuild != null && itemorbuild.TryGetComponent<BodyGetterOverrider>(out var bodyoverrider)) {
                var txt = "UNKNOWN";
                var b = bodyoverrider.GetBody();
                if (b.IsBodyLocal()) {
                    txt = "LOCAL";
                } else if (b != null && b.TryGetComponent(out NetBody nb)) {
                    txt = nb.ToString();
                }
                ttdesc = $"{ttdesc}\nBODY OVR: {txt}";
            }
            if (itemorbuild is Item item) {
                ttdesc = $"{ttdesc}\nitem ID: {item.id}";
            }
            if (itemorbuild is BuildingEntity build) {
                ttdesc = $"{ttdesc}\nbuilding ID: {build.id}";
            }
        }
    }

    public static void WriteMyBindsState(NetDataWriter writer) {
        writer.Put((ushort)KeyBinds.cachedBinds.Count);
        foreach (var item in KeyBinds.cachedBinds) {
            writer.Put(item.Key, true);
            writer.Put(Input.GetKey(item.Value));
        }
    }
    public static Dictionary<string, bool> ReadBinds(NetDataReader reader) {
        var d = new Dictionary<string, bool>();
        reader.Get(out ushort count);
        for (int i = 0; i < count; i++) {
            reader.Get(out string key, true);
            reader.Get(out bool down);
            d[key] = down;
        }
        return d;
    }
    public static Dictionary<string, bool> GetMyBindsState() {
        var d = new Dictionary<string, bool>();
        foreach (var item in KeyBinds.cachedBinds) {
            d[item.Key] = Input.GetKey(item.Value);
        }
        return d;
    }

    void Start() {
        Multiplayer.RegisterCustomClientReceiver(CustomWireCommands.ChannelCustomCommand, RunCustomCommand);

        Multiplayer.RegisterCustomClientReceiver(CustomWireCommands.ChannelCleanWorld, (reader) => {
#if DEBUG
            log.l("Client received cleanworld");
#endif
            Con.CleanWorld();
        });

        // 切场景或重新生成世界时归零同步节流时间戳，保证进图首帧可立即上报角色状态
        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {
            _last_sync_time = 0;
        };
    }

    void Update() {
        {
            if (Net.IsRunning && Net.IsClient) {
                // for server to not get overwhelmed
                ServerPerformanceScale = Mathf.Clamp(Mathf.Lerp(ClientMain.SERVER_FPS, ClientMain.SERVER_TPS, 0.5f) * Time.fixedDeltaTime, 0.01f, 1f);
            } else {
                ServerPerformanceScale = 1;
            }
            AdaptiveSyncTimerDelta = Time.unscaledDeltaTime * ServerPerformanceScale;
        }

        var locplr = ScavPlayer.LocalPlayer;
        if (Net.IsConnected && locplr != null) {
            if (Net.IsClient) {
                UnicodeFontFallback.Client_TrySendUnicodeNameIfNeeded();
            }
            double raw_sync_delta = Time.realtimeSinceStartupAsDouble - _last_sync_time;
            if (raw_sync_delta < 0) {
                _last_sync_time = 0;
                raw_sync_delta = Time.realtimeSinceStartupAsDouble;
            }
            var current_sync_delta = (float)raw_sync_delta;
            bool cansendsyncnow = current_sync_delta * ServerPerformanceScale >= NetTimingConstants.CharacterSyncIdleInterval;

            if (locplr.BasePlayer != null) {
                locplr.BasePlayer.CameraPos.Value = Camera.main.transform.position;
            }

            if (Util.IsWorldGenerated()) {
                var locbody = locplr.body;
                if (locbody != null) {
                    if (!cansendsyncnow) {
                        if (locbody.alive) {
                            var curmovedir_is_not_zero = locbody.moveDir != Vector2.zero;
                            var newlook = (Vector2Int16)(locbody.targetLookPos);

                            if (curmovedir_is_not_zero || !locbody.standing) {
                                cansendsyncnow = current_sync_delta >= NetTimingConstants.CharacterSyncMovingInterval;
                            }

                            if (!cansendsyncnow) {
                                if (newlook != last_looktarget) {
                                    cansendsyncnow = current_sync_delta >= NetTimingConstants.CharacterSyncAimingInterval;
                                } else {
                                    if (last_movedir_thing == Vector2.zero) {
                                        if (curmovedir_is_not_zero) {
                                            cansendsyncnow = true;
                                        }
                                    } else {
                                        if (locbody.moveDir == Vector2.zero) {
                                            cansendsyncnow = true;
                                        }
                                    }
                                }
                            }

                            last_movedir_thing = locbody.moveDir;
                            last_looktarget = (Vector2Int16)(locbody.targetLookPos);
                        }
                    }

                    if (cansendsyncnow) {
                        ClientMain.Client_SendCharacterSyncPacket();
                    }
                }
            }
        }
    }

    public static void Client_SendCharacterSyncPacket() {
        if (!ScavPlayer.TryGetLocalNetBody(out var loc_nb)) {
            Plugin.log.LogError($"BRO WTFFFFFFF IS THIS !!?!??!?!?!?!??!?!?!?!??!?!!?!?!?!??");
            return;
        }

        var mydata = new ClientToServer_NetBodySyncPacket(loc_nb);

        var writer = Net.CreateWriter(NetmsgId.SERVER_ClientCharacterSyncUpdate);
        writer.Put(mydata);
        if (mydata.IncludeRagData() && loc_nb.alive) {
            WriteRagdollPacket(ref writer, loc_nb.body);
        }
        writer.CompressWriter();
        Net.Client_Send(DeliveryMethod.Unreliable, writer);
        // 更新节流基准时间，确保高频/低频自适应周期生效（移动启停边沿仍保持 0ms 即时触发）
        _last_sync_time = Time.realtimeSinceStartupAsDouble;
    }

    public static void Client_SendCharacterSelected(int ch) {
        Client_SendCharacterReady(true, ch);
    }

    public static void Client_SendCharacterReady(bool ready, int ch) {
        LiteNetLib.Utils.NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_SelectChar);
        writer.Put(ready);
        writer.Put(ch);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static float _RAGDOLL_DIST_MARGIN = 0.05f * 0.05f;
    static float _RAGDOLL_ANGVEL_QUANTIZE = 100;
    public static void WriteRagdollPacket(ref LiteNetLib.Utils.NetDataWriter writer, in Body body) {
        foreach (var limb in body.limbs) {
            writer.Put((Vector2)limb.rb.position);
            writer.Put((float)limb.rb.rotation);
            writer.Put((Vector2_4byte_200)limb.rb.velocity);
            writer.Put(Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE));
        }
    }

    public static void ReadRagdollPacket(ref LiteNetLib.Utils.NetDataReader reader, Body body) {
        body.Ragdoll();
        bool apply = body != null;
        var center = body.GetUpperTorso().GetPosition();
        foreach (var limb in body.limbs) {
            reader.Get(out Vector2 limb_rb_position);
            reader.Get(out float limb_rb_rotation);
            reader.Get(out Vector2_4byte_200 limb_rb_velocity);
            reader.Get(out short limb_rb_angularVelocity);

            if (apply && !limb.dismembered && Vector2.SqrMagnitude(limb.rb.position - limb_rb_position) > _RAGDOLL_DIST_MARGIN) {
                if (Net.IsServer) {
                    if (limb_rb_rotation.IsFinite() && (Vector2.SqrMagnitude(limb_rb_position - center) < 100)) {
                    } else {
#if !RELEASE
                        log.sus($"{DebugHelp.GetPlayerFullDebugString(body)} something is sus about {limb} ");
#endif
                        apply = false;
                        continue;
                    }
                }

                limb.transform.position = limb_rb_position;
                limb.rb.rotation = limb_rb_rotation;
                limb.rb.velocity = limb_rb_velocity;
                limb.rb.angularVelocity = Util_MPExtensions.UnquantizeFloatFromShort(limb_rb_angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);
            }
        }
        // 客机接收布娃娃包后保留各肢体报文落点，仅服务端归位躯干中心，避免客机侧被每包强拉回中心造成冻结感
        if (Net.IsServer && body.IsRagdolling() && body.TryGetNetBody(out var nb)) {
            nb.SetBodyPosition(center);
        }
    }

    void LateUpdate() {
    }

    internal static double _last_sync_time = 0;
    Vector2 last_movedir_thing = Vector2.zero;
    Vector2Int16 last_looktarget = new();

    // used for "Feed" feature in player interaction menu
    public static bool ItemCanBeUsedOnThisBody(Item item, Body target) {
        // cant use tools
        if (CombatStuff.ItemIsUsedForAttacking(item)) {
            return false;
        }

        if (item.Stats.wearable) {
            return true; // allow putting on clothes
        }

        if (item.id == "dynamite") {
            return false;
        }

        if (item.id == "autopump") {
            return true;
        }
        // no only local player may use it
        if (item.id == "mp3player") return false;

        return true;
    }

    #region PLAYER_INTERACTIONS

    internal static void _PLRINT_Piggyback(NetBody target) {
        if (target.IsPiggybackable() && target.body.overEncumberance < 0.5) {
            var me = ScavPlayer.GetLocalNetBodyNullable();

            if (me != null) {
                target = target.GetPiggybackStackTop();

                var my_up = me.CountPiggybackStackUp();
                var down = target.CountPiggybackStackDown();

                if (down + my_up + 1 > Multiplayer.rules.PiggybackMaxStack) {
                    return;
                }

                if (me.StartPiggyback(target)) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestPiggyback, target.netId, true);
                }
            }
        }
    }
    internal static void _PLRINT_Carry(NetBody target) {
        var me = ScavPlayer.GetLocalNetBodyNullable();

        if (Net.IsServer || target.StartPiggyback(me, true)) {
            target.SetNetIgnoreTime();
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestCarryPerson, target.netId, true);
        }
    }

    internal static void _PLRINT_Inventory(NetBody target) {
        if (Multiplayer.rules.NoInventoryLock || !target.body.conscious) {
            InvButton_get_body_MultiplayerPatch.focused_body = target.body;
            InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = Camera.main.WorldToScreenPoint(InvButton_get_body_MultiplayerPatch.focused_body.transform.position);
            PlayerCamera.main.radialOpen = true;
        }
    }
    internal static void _PLRINT_GiveItem(NetBody target, Item item) {
        if (item != null && item.TryGetSyncInfo(out var si)) {
            Util.DoAlert(String.Format(Lang.Get("plrint_giveitem_offered"), target.bodyName, PlayerCamera.ItemHoverDescription(item).Item1));

            var writer = Net.CreateWriter(NetmsgId.SERVER_GiveItem);
            writer.Put(si.syncId);
            writer.Put(target.netId);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }
    }

    #endregion

    public static void Do_The_Modifier_Announcement(string layerPrefix, string layerDescription) {
        //OG: WorldGeneration.FinishWorldGeneration
        string text = Locale.GetOther("layer") + " " + (WorldGeneration.world.biomeDepth + 1) + "\n" + WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth];
        if (!string.IsNullOrEmpty(layerPrefix)) {
            string text2 = "<color=\"orange\">" + layerPrefix + "</color> ";
            text = Locale.GetOther("layer") + " " + (WorldGeneration.world.biomeDepth + 1) + "\n" + text2 + WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth];
            string text3 = "<color=\"orange\">" + layerDescription + "</color>";
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.DoAlertDelayed(text3, important: false, 6f));
        }
        PlayerCamera.main.DoAlert(text, important: true);
    }

    internal class SavedHingeJointState : MonoBehaviour {
        public Vector3 a;
        public Quaternion b;
        public Vector3 aa;
        public Quaternion bb;
        public bool useLimits;
        public float limitsmax;
        public float limitsmin;
        public float referenceAngle;
        public float h;
        public float l;

        public Rigidbody2D connectedBody;
        public HingeJoint2D hinge;
    }

    internal static void _InitializePlayerCharacter() {
        if (ServerMain.CHARACTER_PREFAB == null) {
            var og_char = GameObject.Find("Experiment"); // player character is stored only in the scene, i must get it

            if (og_char != null) {
                {
                    var a = og_char.GetComponentsInChildren<HingeJoint2D>();
                    foreach (var hinge in a) {
                        var ogggg = hinge.AddComponent<SavedHingeJointState>();
                        ogggg.a = hinge.transform.localPosition;
                        ogggg.b = hinge.transform.localRotation;
                        ogggg.aa = hinge.connectedBody.transform.localPosition;
                        ogggg.bb = hinge.connectedBody.transform.localRotation;
                        ogggg.useLimits = hinge.useLimits;
                        ogggg.limitsmax = hinge.limits.max;
                        ogggg.limitsmin = hinge.limits.min;
                        ogggg.referenceAngle = hinge.referenceAngle;

                        ogggg.hinge = hinge;
                        ogggg.connectedBody = hinge.connectedBody;
                    }
                }

                ServerMain.CHARACTER_PREFAB = GameObject.Instantiate(og_char);
                ServerMain.CHARACTER_PREFAB.SetActive(false);
            } else {
                // fullgame may spawn Milky/Dune as the local body instead of leaving Experiment in the scene
                Body fallback_body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                GameObject fallback_char = fallback_body != null
                    ? (fallback_body.transform.parent != null ? fallback_body.transform.parent.gameObject : fallback_body.gameObject)
                    : null;

                if (fallback_char == null) {
                    foreach (Body b in UnityEngine.Object.FindObjectsOfType<Body>()) {
                        if (b.GetComponent<NetBody>() == null && !b.CompareTag("Enemy") && b.transform.childCount > 0) {
                            fallback_char = b.transform.parent != null ? b.transform.parent.gameObject : b.gameObject;
                            break;
                        }
                    }
                }

                if (fallback_char != null) {
                    HingeJoint2D[] hinges = fallback_char.GetComponentsInChildren<HingeJoint2D>();
                    foreach (HingeJoint2D hinge in hinges) {
                        SavedHingeJointState ogggg = hinge.gameObject.AddComponent<SavedHingeJointState>();
                        ogggg.a = hinge.transform.localPosition;
                        ogggg.b = hinge.transform.localRotation;
                        ogggg.aa = hinge.connectedBody.transform.localPosition;
                        ogggg.bb = hinge.connectedBody.transform.localRotation;
                        ogggg.useLimits = hinge.useLimits;
                        ogggg.limitsmax = hinge.limits.max;
                        ogggg.limitsmin = hinge.limits.min;
                        ogggg.referenceAngle = hinge.referenceAngle;
                        ogggg.hinge = hinge;
                        ogggg.connectedBody = hinge.connectedBody;
                    }

                    ServerMain.CHARACTER_PREFAB = GameObject.Instantiate(fallback_char);
                    ServerMain.CHARACTER_PREFAB.SetActive(false);
                } else if (Net.IsRunning) {
                    Multiplayer.DoMultiplayerStatusMessageError("PLAYER CHARACTER NOT FOUND!!!!!!!!!!!!!!!! RESTART THE GAME! RESTART !!!!!!!!!!!!!!!!!!!!");
                }
            }
        }
    }
}
