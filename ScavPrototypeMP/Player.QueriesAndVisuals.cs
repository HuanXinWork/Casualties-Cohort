using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {
    public partial class ScavPlayer {
        #region GLOBAL_UTILS

        public static List<NetBody> GetPlayerBodiesInRadius(Vector2 pos, float radius) {
            radius *= radius; // square it

            List<NetBody> b = new List<NetBody>();
            foreach (Body body in ScavPlayer.BodyToPlayerDict.Keys) {
                if (KM.DistanceCheck_PreSqr(body.transform.position, pos, radius) && body.TryGetNetBody(out NetBody c)) {
                    b.Add(c);
                }
            }
            return b;
        }

        public static List<ScavPlayer> GetPlayersInRadius(Vector2 pos, float radius, bool exceptHost = false) {
            radius *= radius; // square it

            List<ScavPlayer> b = new List<ScavPlayer>();
            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                if (KM.DistanceCheck_PreSqr(plr.pos, pos, radius)) {
                    b.Add(plr);
                }
            }
            if (exceptHost) {
                b.Remove(LocalPlayer);
            }
            return b;
        }

        public static (NetBody, float) GetNearestConsciousPlayerToThisBody(NetBody thisone) {
            float nearest = float.PositiveInfinity;
            NetBody nearestsci = null;
            foreach (KeyValuePair<Body, ScavPlayer> item in ScavPlayer.BodyToPlayerDict) {
                NetBody pb = item.Key.GetComponent<NetBody>();
                if ((pb.body.conscious || pb.body.sleeping) && thisone != (pb as NetBody)) {
                    float dist = KM.DistanceSqr(pb.transform.position, thisone.transform.position);
                    if (dist < nearest) {
                        nearest = dist;
                        nearestsci = item.Key.GetComponent<NetBody>();
                    }
                }
            }
            return (nearestsci, nearest);
        }

        public static (ScavPlayer, float) GetDistanceToNearestLivingPlayer(Vector2 target) {
            float nearest = float.PositiveInfinity;
            ScavPlayer nearestsci = null;
            foreach (ScavPlayer plr in AllLivingPlayers) {
                Body body = plr.body;
                if (plr.body == null) {
                    continue;
                }

                float dist = KM.DistanceSqr(body.transform.position, target);
                if (dist < nearest) {
                    nearest = dist;
                    nearestsci = plr;
                }
            }
            return (nearestsci, nearest);
        }

        public static ScavPlayer GetPlayerFromClientId(knetid clientid) {
            return ScavPlayer.ClientIdToPlayerDict.GetValueSafe(clientid);
        }

        public static bool TryGetPlayerFromClientId(knetid clientid, out ScavPlayer plr) => ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr);

        public static bool TryGetPlayerAndBodyFromClientId(knetid clientid, out ScavPlayer plr, out Body body) {
            if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr) && plr.body != null) {
                body = plr.body;
                return true;
            }
            plr = null;
            body = null;
            return false;
        }

        public static bool TryGetPlayerAndNetBodyFromClientId(knetid clientid, out ScavPlayer plr, out NetBody pb) {
            if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(clientid, out plr) && plr.body != null) {
                pb = plr.netBody;
                return true;
            }
            plr = null;
            pb = null;
            return false;
        }

        public static Body GetBodyFromClientId(knetid clientid) {
            ScavPlayer plr = GetPlayerFromClientId(clientid);
            if (plr != null) {
                return plr.body;
            }
            return null;
        }

        public static knetid GetClientIdFromBody(Body body) {
            NetBody pb = body.GetComponent<NetBody>();
            return pb.netId;
        }

        public static ScavPlayer GetPlayerFromBody(Body body) {
            return ScavPlayer.BodyToPlayerDict.GetValueSafe(body);
        }

        public string GetPersistentId() {
            if (Net.IsRunningSteam) {
                return "STEAM_" + SteamId.ToString();
            }
            return "NAME_" + playername_as_hex;
        }

        public string playername_as_hex => Encoding.UTF8.GetBytes(playerName).ToHexString();

        #endregion

        public const double FINGERPOINTINGTIME_MAX = 3.0;
        public bool is_pointingfingerat = false;
        public byte is_pointingfingeratTYPE = 0;
        public double is_pointingfingeratTIME = -9990.0;
        public Vector2 is_pointingfingeratTARGET = Vector2.zero;

        public void PointFingerAt(Vector2 pos, byte type) {
            bool alive = false;
            if (body) {
                alive = body.conscious;
                body.overrideLookTime = (float)(FINGERPOINTINGTIME_MAX - 0.5);
                body.overrideLookPos = pos;
            }

            is_pointingfingerat = ((Multiplayer.rules.CanCommunicateWithTheDeadTC()) || alive);
            is_pointingfingeratTIME = Time.realtimeSinceStartupAsDouble;
            is_pointingfingeratTARGET = pos;
            is_pointingfingeratTYPE = type;
            SpriteRenderer spr = locationPingCircle.GetComponent<SpriteRenderer>();

            if (type == 0) {
                spr.sprite = CoopModAssets.circlething;
            } else if (type == 1) {
                spr.sprite = CoopModAssets.alert2;
            } else {
                spr.sprite = CoopModAssets.circlething;
            }
        }

#if !RELEASE
        void OnGUI() {
            if (log.verbose) {
                if (IsInMinigame && minigame_targetobject.TryGetGameObject(out GameObject go)) {
                    int ogfont = GUI.skin.label.fontSize;

                    Vector2 pos = (Vector2)Camera.main.WorldToScreenPoint(go.transform.position);
                    pos = new Vector2(pos.x, Screen.height - pos.y);

                    if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {
                        float y = 0;
                        GUI.skin.label.normal.textColor = UnityEngine.Color.white;
                        GUI.skin.label.fontSize = 10;
                        GUI.Label(new Rect(pos.x, pos.y + y, 400, 200), $"MG: {this}\nT: {minigame_current_type}\nSESSION: {minigame_session}\nTARGET: {go}\nITEM: {minigame_currentItem_syncinfo}");
                    }

                    GUI.skin.label.normal.textColor = UnityEngine.Color.white;
                    GUI.skin.label.fontSize = ogfont;
                }
            }
        }
#endif

        void _UpdateFingerPointVisual() {
            if (is_pointingfingerat) {
                if (!Util.IsInWorld() || (Time.realtimeSinceStartupAsDouble - this.is_pointingfingeratTIME > ScavPlayer.FINGERPOINTINGTIME_MAX)) {
                    this.is_pointingfingerat = false;
                    locationPingCircle.SetActive(false);
                    locationPingArrow.SetActive(false);
                }
            }
            if (locationPingCircle != null && locationPingArrow != null) {
                double ttttt = is_pointingfingeratTIME + Time.realtimeSinceStartupAsDouble;

                SpriteRenderer circle_spr = locationPingCircle.GetComponent<SpriteRenderer>();
                float new_alpha = Mathf.MoveTowards(circle_spr.color.a, is_pointingfingerat ? 1 : 0, Time.deltaTime * 2f);

                bool keep_anim = (circle_spr.color.a + new_alpha) != 0;
                if (locationPingCircle.activeSelf != keep_anim) {
                    locationPingCircle.SetActive(keep_anim);
                }
                if (locationPingCircle.activeSelf) {
                    Vector2 targetpos = is_pointingfingeratTARGET;
                    float sine = ((float)Math.Sin(ttttt * 4.0));
                    if (is_pointingfingeratTYPE == 1) {
                        targetpos += Vector2.up * (4 + sine * 0.7f);
                    }

                    locationPingCircle.transform.position = KM.v3v2z(targetpos, -2.601f);
                    locationPingCircle.transform.localScale = Vector3.one * (1f + sine * 0.3f) * 32f * 0.5f;

                    circle_spr.color = playerColor.ToColorWithAlpha(new_alpha);
                }

                bool actually_do_arrow = body != null && keep_anim && IsConscious() && locationPingCircle.activeSelf && !KM.DistanceCheck(is_pointingfingeratTARGET, pos, 8f);
                if (locationPingArrow.activeSelf != actually_do_arrow) {
                    locationPingArrow.SetActive(actually_do_arrow);
                }
                if (locationPingArrow.activeSelf) {
                    Vector2 dir = KM.NormalTo(is_pointingfingeratTARGET, body.transform.position, out float _);
                    locationPingArrow.transform.position = KM.v3v2z(((Vector2)body.transform.position - (dir * 4f) + (dir * (float)Math.Sin(ttttt * 4.0) * 1f)), -2.6f);
                    locationPingArrow.transform.eulerAngles = new Vector3(0, 0, Vector2.SignedAngle(Vector2.right, dir) + 180f);

                    SpriteRenderer arrow_spr = locationPingArrow.GetComponent<SpriteRenderer>();
                    arrow_spr.color = circle_spr.color;
                }
            }
        }
    }
}
