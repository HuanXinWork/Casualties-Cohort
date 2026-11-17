using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace Together;

public partial class Voicechat {
    static void Client_SendMicDataFrame(float[] data, bool is_not_last) {
        float minmax = GetFrameMaxVol(data);
        my_output_volume = minmax;

        if (ENABLE_LOCAL_LOOPBACK) {
            if (ENABLE_LOCAL_LOOPBACK_VCINSTANCE == null) {
                var go = new GameObject("vc_loopback");
                DontDestroyOnLoad(go);
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE = go.AddComponent<VoicechatOutputDebugVisualiser>();
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.audioSource.rolloffMode = AudioRolloffMode.Logarithmic;
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.audioSource.spatialBlend = 0f;
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.force_no_effects = true;
            }
            if (Camera.main != null) {
                ENABLE_LOCAL_LOOPBACK_VCINSTANCE.transform.position = Camera.main.transform.position;
            }
            byte[] voiceblob = encoder.Encode(data, (ushort)SAMPLE_RATE);
            ENABLE_LOCAL_LOOPBACK_VCINSTANCE.OpusDecode((ushort)SAMPLE_RATE, voiceblob, out float[] voicedata);
            ENABLE_LOCAL_LOOPBACK_VCINSTANCE.ReceiveVoiceBlob((ushort)SAMPLE_RATE, voicedata, is_not_last);
            TEMPDEV_LAST_OPUS_ENCODED_SIZE = voiceblob.Length;
            TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = data.Length;
            return;
        }

        if (my_output_volume > MINIMUM_VOLUME_TRESHOLD) {
            AlwaysOn_TimeBelowTreshold = 0f;
        } else if (AlwaysOn_TimeBelowTreshold > MINIMUM_VOLUME_TRESHOLD_TIME) {
            return;
        }

        if (Net.IsConnected && !Net.IsDedicatedServer) {
            byte[] voiceblob = encoder.Encode(data, (ushort)SAMPLE_RATE);
            if (voiceblob.Length > 0) {
                TEMPDEV_LAST_OPUS_ENCODED_SIZE = voiceblob.Length;
                TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = data.Length;

                // 严格保持与原版 v5.5.x 100% 一致的线上字节顺序
                var writer = Net.CreateWriter(NetmsgId.SERVER_VoiceChatBlob);
                writer.Put((byte)Array.IndexOf(STANDART_FREQUENCIES, (ushort)SAMPLE_RATE));
                writer.PutBytesWithLength(voiceblob);
                writer.Put((ushort)data.Length);
                writer.Put(is_not_last);
                Net.Client_Send(DeliveryMethod.Unreliable, writer);
            } else {
#if TESTING
                log.warn($" VOICECHAT: Failed to encode opus to send???  data.Length:{data.Length} ");
#endif
            }
        }
    }

    public static bool CheckIfPlayerIsUnconsciousToMute(ScavPlayer plr) {
        if (Multiplayer.rules.SleepingMute) {
            return plr.IsAliveAndNotConscious();
        }
        return plr.IsAliveAndUnconsciousAndNotSleeping();
    }

    public static bool CheckIfIsAliveAndNotCriticallyDying(ScavPlayer plr) {
        return plr.IsAliveAndNotCriticallyDying();
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_VoiceChatBlobRelay)]
    static void ClientReceiver__VoiceChatBlobRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (my_listener_volume <= 0f || SharedMain.LocalWorldIsGenerating) return;

        reader.Get(out knetid speakerId);
        if (ScavPlayer.TryGetPlayerFromClientId(speakerId, out ScavPlayer plr) && plr.vc_output != null) {
            reader.Get(out byte freq_index);
            if (freq_index >= STANDART_FREQUENCIES.Length) {
                return;
            }
            ushort freq = STANDART_FREQUENCIES[freq_index];
            reader.Get(out byte[] data);
            reader.Get(out bool is_not_last);

            if (!plr.vc_output.OpusDecode(freq, data, out float[] frame)) {
#if !RELEASE
                Plugin.log.LogWarning(" VOICECHAT: CLIENT: Failed to decode opus??? ");
#endif
                return;
            }
            plr.vc_output.ReceiveVoiceBlob(freq, frame, is_not_last);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_VoiceChatBlob)]
    static void ServerReceiver__VoiceChatBlob(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!VCRULE_enabled || SharedMain.LocalWorldIsGenerating) return;

        reader.Get(out byte freq_index);
        reader.Get(out byte[] data);
        reader.Get(out bool is_not_last);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) && plr.vc_output != null) {
            if (plr.server_mute_vc) {
                if (plr.ServerPlayerState.didmutewarn < 20) {
                    plr.Server_DoAlertSingle("<key:mmct_plr_vcmuted>", false);
                }
                plr.ServerPlayerState.didmutewarn++;
                return;
            }

            if (freq_index >= STANDART_FREQUENCIES.Length) {
#if !RELEASE
                Plugin.log.LogInfo($"SUS: VOICECHAT client sent wrong samplerate index: {freq_index} ");
#endif
                return;
            }

            ushort freq = STANDART_FREQUENCIES[freq_index];
            if (freq < DEFAULT_MIN_SAMPLE_RATE || freq > DEFAULT_MAX_SAMPLE_RATE || freq > SAMPLE_RATE) {
                return;
            }

            if (plr.IsAlive() && VCRULE_speechimpaired) {
                if (VCRULE_mindwiperule && plr.IsAliveAndMindwiped()) {
                    return;
                }
                if (CheckIfPlayerIsUnconsciousToMute(plr)) {
                    return;
                }
                if (plr.body.brainHealth < 75f) {
                    int r = UnityEngine.Random.Range(0, 100);
                    if (plr.body.brainHealth + 10f < r) {
                        return;
                    }
                }
            }

            bool server_heard_it = false;
            float[] voicedata_frame = null;
            if (!plr.IsLocal) {
                if (!plr.vc_output.OpusDecode(freq, data, out voicedata_frame)) {
#if TESTING
                    log.warn(" VOICECHAT: SERVER: Failed to decode opus??? ");
#endif
                    return;
                }
                if (plr.vc_output.vc_buffered_amount + voicedata_frame.Length >= plr.vc_output.clip.samples) {
                    return;
                }
                if (Net.IsDedicatedServer) {
                    server_heard_it = true;
                    if (my_listener_volume > 0f && !Multiplayer.DedicatedServerDisableVisuals) {
                        plr.vc_output.ReceiveVoiceBlob(freq, voicedata_frame, is_not_last);
                    }
                }
            }

            List<ScavPlayer> targets = Server_VoiceRelayGetTargets(plr);
            if (ScavPlayer.LocalPlayer != null && targets.Remove(ScavPlayer.LocalPlayer)) {
                if (!server_heard_it && my_listener_volume > 0f) {
                    if (voicedata_frame == null && !plr.vc_output.OpusDecode(freq, data, out voicedata_frame)) {
#if TESTING
                        log.warn(" VOICECHAT: SERVER: Failed to decode opus??? 2 ");
#endif
                        return;
                    }
                    plr.vc_output.ReceiveVoiceBlob(freq, voicedata_frame, is_not_last);
                }
            }

            if (targets.Count == 0) {
                return;
            }

            List<knetid> target_client_ids = targets.Select(x => x.playerId).ToList();
            var writer = Net.CreateWriter(NetmsgId.CLIENT_VoiceChatBlobRelay);
            writer.Put(clientId);
            writer.Put(freq_index);
            writer.PutBytesWithLength(data);
            writer.Put(is_not_last);
            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: target_client_ids);
        }
    }

    static List<ScavPlayer> Server_VoiceRelayGetTargets(ScavPlayer yapper_plr) {
        Vector2 plrpos = yapper_plr.pos;
        var targets = new List<ScavPlayer>(ScavPlayer.AllPlayers);
        targets.Remove(yapper_plr);

        if (Util.IsWorldGenerated()) {
            float presqr = yapper_plr.vc_output.max_hear_distance * yapper_plr.vc_output.max_hear_distance;
            float dist_to_check_sqr = yapper_plr.ImpairedSpeech() ? (presqr * 0.5f) : presqr;

            if (!yapper_plr.IsAlive() && !VCRULE_deadchat) {
                // 过滤所有仍然健康存活的接收者，防止死后语聊关闭时活人听到亡者语音
                targets.RemoveAll(t => CheckIfIsAliveAndNotCriticallyDying(t));
            }

            targets.RemoveAll(t => !KM.DistanceCheck_PreSqr(t.pos, plrpos, dist_to_check_sqr));
        }

        return targets;
    }

    public static float GetFrameMaxVol(IEnumerable<float> voice_frame) {
        float max_vol = 0f;
        foreach (float f in voice_frame) {
            max_vol = Math.Max(Mathf.Abs(f), max_vol);
        }
        return max_vol;
    }

    public static float[] ResampleVoiceBlob(in IReadOnlyList<float> data, int input_samplerate, int target_samplerate) {
        double ratio = (double)target_samplerate / input_samplerate;
        int newarrlen = Mathf.RoundToInt((float)(data.Count * ratio));
        var output = new float[newarrlen];

        for (int i = 0; i < newarrlen; i++) {
            double source_i = i / ratio;
            int j = (int)Math.Floor(source_i);
            double fraction = source_i - j;
            float val = data[j];
            float nextval = (j + 1 < data.Count) ? data[j + 1] : val;
            output[i] = (float)(val + (nextval - val) * fraction);
        }
        return output;
    }
}
