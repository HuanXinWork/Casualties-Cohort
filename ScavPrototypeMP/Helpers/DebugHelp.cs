using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using LiteNetLib;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 调试菜单开关与世界空间网络事件可视化辅助组件。
    /// </summary>
    public static class DebugMenuSettings {
#if !PLAYTEST
        [SettingDeclarerThingyBool("setting_debug_verbose", allow_saving = false)]
#endif
#if RELEASE && !PLAYTEST
        internal static bool is_verbose = false;
#else
        internal static bool is_verbose = true;
#endif

        [SettingDeclarerThingyBool("setting_debug_events", allow_saving = false)]
#if RELEASE && !PLAYTEST
        public static bool _DEV_VISUALISE_NET_EVENTS = false;
#else
        public static bool _DEV_VISUALISE_NET_EVENTS = true;
#endif

        [SettingDeclarerThingyBool("setting_debug_logsteam", allow_saving = false)]
#if RELEASE && !PLAYTEST
        public static bool _DEV_ENABLE_STEAM_LOG = false;
#else
        public static bool _DEV_ENABLE_STEAM_LOG = true;
#endif
    }

    public class DebugHelp : MonoBehaviour {
        public static string GetGameObjectFullDebugString(Component c) {
            return c != null ? GetGameObjectFullDebugString(c.gameObject) : "COMPONENT(NULL)";
        }

        public static string GetGameObjectFullDebugString(GameObject go) {
            if (go == null) {
                return "GAMEOBJECT(NULL)";
            }
            if (go.TryGetComponent(out Limb l)) {
                string bodyname = l.body != null ? l.body.name : "NULL_BODY";
                if (l.TryGetNetBody(out NetBody nb)) {
                    bodyname = nb.ToString();
                }
                return $"LIMB({l.name} - {bodyname})";
            }
            if (Util.TryGetBodyFromObject(go, out Body b)) {
                return GetPlayerFullDebugString(b);
            }
            SyncInfoGameObjectTracker tracker = go.GetComponent<SyncInfoGameObjectTracker>();
            return tracker != null ? tracker.ToString() : go.ToString();
        }

        public static string GetPlayerFullDebugString(Body body) {
            if (body == null) {
                return "BODY(NULL)";
            }
            if (body.TryGetNetBody(out NetBody nb)) {
                return nb.ToString();
            }
            return $"BODY({body.name})";
        }

        public static string GetPlayerFullDebugString(knetid cid) {
            if (ScavPlayer.TryGetPlayerFromClientId(cid, out ScavPlayer plr)) {
                return plr.ToString();
            }
            return $"PLR(ID:{cid}, UNKNOWN)";
        }

        private sealed class EventDisplayInfo {
            public Vector2 pos;
            public string text;
            public float duration;
            public Color color;
            public double start;
        }

        private static readonly List<EventDisplayInfo> events = new List<EventDisplayInfo>();

        public static bool _DEV_VISUALISE_NET_EVENTS => DebugMenuSettings._DEV_VISUALISE_NET_EVENTS;

        /// <summary>
        /// 仅在网络消息接收处记录世界坐标悬浮提示，用于区分远端中继消息与本地触发。
        /// </summary>
        public static void OnNetEvent(Vector2 pos, in string text, Color? color = null, float duration = 2f) {
            if (!_DEV_VISUALISE_NET_EVENTS) {
                return;
            }
            while (CheckOverlap(ref pos)) {
            }

            events.Add(new EventDisplayInfo {
                pos = pos,
                text = text,
                duration = duration,
                color = color ?? Color.blue,
                start = Time.realtimeSinceStartupAsDouble,
            });
        }

        static bool CheckOverlap(ref Vector2 pos) {
            foreach (EventDisplayInfo item in events) {
                if (KM.DistanceCheck(item.pos, pos, 1)) {
                    pos += Vector2.up;
                    return true;
                }
            }
            return false;
        }

        void OnGUI() {
            if (!_DEV_VISUALISE_NET_EVENTS || !Net.IsRunning || Camera.main == null) {
                return;
            }

            int ogfont = GUI.skin.label.fontSize;

            foreach (EventDisplayInfo item in events) {
                Vector2 pos = Camera.main.WorldToScreenPoint(item.pos);
                pos = new Vector2(pos.x, Screen.height - pos.y);

                if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {
                    GUI.skin.label.normal.textColor = item.color;
                    GUI.skin.label.fontSize = 10;
                    GUI.Label(new Rect(pos.x, pos.y, 600, 100), item.text);
                }
            }
            events.RemoveAll(i => Time.realtimeSinceStartupAsDouble - i.start > i.duration);

            GUI.skin.label.normal.textColor = Color.white;
            GUI.skin.label.fontSize = ogfont;
        }

        public static void Server_SendAnEmptyDebugEvent(in Vector2 pos, in string msg, IReadOnlyList<knetid> target_clients = null) {
            if (target_clients == null) {
                target_clients = ServerMain.AllClientIdsExceptHost;
            }

            var writer = Net.CreateWriter(NetmsgId.CLIENT_EmptyDebugEvent);
            writer.Put(pos);
            writer.Put(msg, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: target_clients);
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_EmptyDebugEvent)]
        static void Client_EmptyDebugEvent(knetid _, LiteNetLib.Utils.NetDataReader reader) {
            reader.Get(out Vector2 pos);
            reader.Get(out string msg, true);

            if (_DEV_VISUALISE_NET_EVENTS) {
                OnNetEvent(pos, msg);
            }
        }
    }
}
