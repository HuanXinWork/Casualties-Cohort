using CasualtiesTogetherUtils;
using OpusSharp.Core.Extensions;
using System;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 语音聊天主控制器（负责本地麦克风采集、按键说话/声控模式切换、嘴型驱动与背景音乐闪避）。
/// </summary>
public partial class Voicechat : CasualtiesTogetherSingleton {
    public static Voicechat singleton { get; private set; }
    public static ushort[] STANDART_FREQUENCIES => AudioOpusEncoding.STANDART_FREQUENCIES;

    public const int DEFAULT_MAX_SAMPLE_RATE = 48000;
    public const int DEFAULT_SAMPLE_RATE = 24000;
    public const int DEFAULT_MIN_SAMPLE_RATE = 8000;
    public const int DEFAULT_FRAME_DURATION_MS = 40;
    public static float DEFAULT_FRAME_DURATION_S => DEFAULT_FRAME_DURATION_MS / 1000f;

    private static AudioClip mic_clip;
    public static int SAMPLE_RATE = DEFAULT_SAMPLE_RATE;
    public static int FRAME_SIZE => CalculateFrameSizeForSamplerate(SAMPLE_RATE);

    public static int CalculateFrameSizeForSamplerate(int sr) {
        return sr / 1000 * DEFAULT_FRAME_DURATION_MS;
    }

    public static bool IS_RECORDING { get; protected set; }
    private static int last_mic_pos = 0;

    public const string NOMIC = "NO_MICROPHONE_SELECTED";
    internal const string _PREF_MICNAME = "KrokoshaMultiplayer_MicrophoneName";

    internal static string _PREF_MICMODE {
        get {
            var f = typeof(Voicechat).GetField(nameof(current_microphone_mode));
            return f.GetAttribute<SettingDeclarerThingyChoiceAttribute>().GetPrefKey(f);
        }
    }

    public static string current_microphone { get; protected set; }

    [SettingDeclarerThingyFloat(0, 2, "setting_vcattenuation")]
    public static float bg_music_attenuation = 1f;

    [SettingDeclarerThingyFloat(0, 2, "setting_mp3vol")]
    public static float mp3_listener_volume = 1.0f;

    [SettingDeclarerThingyFloat(0, 2, "setting_vclistenvol")]
    public static float my_listener_volume = 1.0f;

    [SettingDeclarerThingyFloat(0, 2, "setting_micvol")]
    public static float my_volume = 1.0f;

    [SettingDeclarerThingyChoice("setting_micmode", new string[] {
        "setting_micmode_choice_off",
        "setting_micmode_choice_ptt",
        "setting_micmode_choice_on",
        "setting_micmode_choice_toggle",
    })]
    public static byte current_microphone_mode = 1;

    public static bool IsMicModeAlwaysOn() => current_microphone_mode == 2;
    public static bool IsMicModePushToTalk() => current_microphone_mode == 1;
    public static bool IsMicModeToggleToTalk() => current_microphone_mode == 3;
    public static bool IsMicModeDisabled() => current_microphone_mode == 0;

    public static bool IsPushToTalkKeyPushed() {
        return Input.GetKey(KeyBinds.GetBind(CoopKeybinds.voicechat));
    }

    public static float MINIMUM_VOLUME_TRESHOLD = 0.05f;
    public static float MINIMUM_VOLUME_TRESHOLD_TIME = 0.5f;
    public static float AlwaysOn_TimeBelowTreshold = 0f;
    public static float my_output_volume = 0.0f;

    public static bool VCRULE_hearingloss => Multiplayer.rules.HearingLossVC;
    public static bool VCRULE_speechimpaired => Multiplayer.rules.SpeechImpairedVC;
    public static bool VCRULE_mindwiperule => Multiplayer.rules.MindwipeDisablesChat;
    public static bool VCRULE_deadchat => Multiplayer.rules.CanCommunicateWithTheDeadVC();
    public static bool VCRULE_enabled => Multiplayer.rules.VoicechatEnabled;

    public static bool listener_is_mindwiped = false;

    public static float MAX_HEAR_EFFECT_PROCESSING_DISTANCE => MAX_HEAR_DISTANCE * 1.3f;
    public static float MAX_HEAR_DISTANCE => Util.MetersToTiles(Multiplayer.rules.ProximityHearDistance);

    static Queue<float> my_mic_data_buffer = new();
    static AudioOpusEncoding encoder;

    internal static bool ENABLE_LOCAL_LOOPBACK = false;
    internal static VoicechatOutputDebugVisualiser ENABLE_LOCAL_LOOPBACK_VCINSTANCE = null;
    internal static int TEMPDEV_LAST_OPUS_ENCODED_SIZE = 0;
    internal static int TEMPDEV_LAST_OPUS_BEFORE_ENCODED_SIZE = 0;

    static bool _toggle_to_talk_toggled = false;
    static bool _push_to_talk_pushed = false;

    public static float smooth_bg_music_volume = 1f;
    public static float smooth_bg_music_volume_target = 1f;
    public static int system_framesize = 48000;

    private static bool _did_mindwipe_alert = false;
    private static bool _did_consciousness_muted_alert = false;

    const int MAX_FAILURES_UNTIL_DISABLE_VC = 5;
    static int START_FAILURE_COUNT = 0;
    static float _complexityupdatetimer = 0f;

    void KrokoshaSingletonEvent_OnSceneChange() {
        ENABLE_LOCAL_LOOPBACK = false;
        KrokoshaSingletonEvent_OnRulesUpdate();
    }

    void KrokoshaSingletonEvent_OnRulesUpdate() {
        foreach (StreamedAudioOutput item in FindObjectsOfType<StreamedAudioOutput>()) {
            item.OnHearabilityRuleUpdate();
        }
    }

    public static ushort GetClosestSamplerate(ushort my) {
        ushort res = STANDART_FREQUENCIES[0];
        foreach (ushort item in STANDART_FREQUENCIES) {
            if (my < item) {
                return res;
            }
            res = item;
        }
        return res;
    }

    public static ushort GetClosestNumber(ushort my, ushort[] ushorts) {
        ushort res = ushorts[0];
        foreach (ushort item in ushorts) {
            if (my < item) {
                return res;
            }
            res = item;
        }
        return res;
    }

    private void Awake() {
        current_microphone ??= NOMIC;
        encoder = new AudioOpusEncoding();
        singleton = this;
    }

    private void Start() {
        current_microphone = SettingsJsonThing.json.GetString(_PREF_MICNAME, NOMIC);
        if (current_microphone == NOMIC) {
            Plugin.log.LogInfo(" VOICECHAT: Start(): Attempting to select default microphone. ");
        }
        StartRecording(current_microphone);
        if (!IS_RECORDING && Microphone.devices.Length > 0) {
            StartRecording(Microphone.devices[0]);
        }
    }

    static void CopyMicSamplesIntoBufferAndApplyVolume(ref float[] samples, ref int i, int until) {
        while (true) {
            float vol = samples[i] * my_volume;
            i++;
            my_mic_data_buffer.Enqueue(vol);
            if (i >= until) {
                break;
            }
        }
    }

    public static bool VoiceChatIsEnabledLocallyOnly() {
        if (Util.IsGeneratingWorld()) {
            return false;
        }
        return my_volume > 0f && !IsMicModeDisabled();
    }

    public static bool VoiceChatIsEnabled() {
        if (!VoiceChatIsEnabledLocallyOnly()) {
            return false;
        }
        return !((!VCRULE_enabled && Net.IsRunning) || Net.IsDedicatedServer);
    }

    private static bool RecordMicUpdate() {
        if (!VoiceChatIsEnabled()) {
            _push_to_talk_pushed = false;
            _toggle_to_talk_toggled = false;
            return false;
        }

        if (!ENABLE_LOCAL_LOOPBACK) {
            if (IsMicModePushToTalk()) {
                if (IsPushToTalkKeyPushed()) {
                    _push_to_talk_pushed = true;
                }
                if (!_push_to_talk_pushed) {
                    return false;
                }
            } else if (IsMicModeToggleToTalk()) {
                if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.voicechat))) {
                    _toggle_to_talk_toggled = !_toggle_to_talk_toggled;
                }
                if (_toggle_to_talk_toggled) {
                    _push_to_talk_pushed = true;
                }
                if (!_push_to_talk_pushed) {
                    return false;
                }
            } else if (current_microphone_mode != 2) {
                return false;
            }

            ScavPlayer locplr = ScavPlayer.LocalPlayer;
            if (locplr != null && VCRULE_speechimpaired) {
                if (VCRULE_mindwiperule && locplr.IsAliveAndMindwiped()) {
                    if (!_did_mindwipe_alert) {
                        _push_to_talk_pushed = false;
                        _did_mindwipe_alert = true;
                        Util.DoAlert(Lang.Get("voicechat_deny_mindwipe"));
                        log.l("VOICECHAT: Can't speak = mindwiped");
                    }
                    return false;
                }
                if (CheckIfPlayerIsUnconsciousToMute(locplr)) {
                    if (!_did_consciousness_muted_alert) {
                        _push_to_talk_pushed = false;
                        _did_consciousness_muted_alert = true;
                        Util.DoAlert(Lang.Get("voicechat_deny_unconscious"));
                        log.l("VOICECHAT: Can't speak = unconscious");
                    }
                    return false;
                }
            }
        }

        if (!IS_RECORDING || mic_clip.frequency != SAMPLE_RATE) {
            StartRecording(current_microphone);
        }

        if (IS_RECORDING && mic_clip.channels > 0) {
            int pos = Microphone.GetPosition(GetMic());
            bool didloop = pos < last_mic_pos;
            int micpos_distance = didloop ? (pos - (last_mic_pos - mic_clip.samples)) : (pos - last_mic_pos);

            if (pos == last_mic_pos) {
                return true;
            }

            int i = 0;
            if (!didloop) {
                var sample = new float[micpos_distance];
                mic_clip.GetData(sample, last_mic_pos);
                CopyMicSamplesIntoBufferAndApplyVolume(ref sample, ref i, sample.Length);
            } else if (pos == 0) {
                var lastLoopSamples = new float[mic_clip.samples - last_mic_pos];
                mic_clip.GetData(lastLoopSamples, last_mic_pos);
                CopyMicSamplesIntoBufferAndApplyVolume(ref lastLoopSamples, ref i, lastLoopSamples.Length);
            } else {
                var lastLoopSamples = new float[mic_clip.samples - last_mic_pos];
                var currLoopSamples = new float[pos];
                mic_clip.GetData(lastLoopSamples, last_mic_pos);
                mic_clip.GetData(currLoopSamples, 0);
                CopyMicSamplesIntoBufferAndApplyVolume(ref lastLoopSamples, ref i, lastLoopSamples.Length);
                i = 0;
                CopyMicSamplesIntoBufferAndApplyVolume(ref currLoopSamples, ref i, currLoopSamples.Length);
            }

            last_mic_pos = pos;
            SendAllAvailableMicData();
            return true;
        }

        return false;
    }

    static void SendAllAvailableMicData(bool dontcare_if_it_dont_smaller_than_buffer = false) {
        if (my_mic_data_buffer.Count < FRAME_SIZE) {
            if (!dontcare_if_it_dont_smaller_than_buffer) {
                return;
            }
        } else {
            var frame = new float[FRAME_SIZE];
            UpdateOpusComplexity();
            while (my_mic_data_buffer.Count >= frame.Length) {
                for (int i = 0; i < frame.Length; i++) {
                    frame[i] = my_mic_data_buffer.Dequeue();
                }
                bool isnotlast = IsMicModeAlwaysOn()
                    || my_mic_data_buffer.Count >= frame.Length
                    || IsPushToTalkKeyPushed();
                Client_SendMicDataFrame(frame, isnotlast);
            }
            _push_to_talk_pushed = false;
        }
    }

    void UpdateMicrophone() {
        if (RecordMicUpdate()) {
            Body b = Util.GetLocalBodyNullable();
            if (b != null && b.alive) {
                BodyOpenMouthForSpeaking(b, my_output_volume);
            }
            if (my_output_volume < MINIMUM_VOLUME_TRESHOLD) {
                AlwaysOn_TimeBelowTreshold += Time.deltaTime;
            }
        } else {
            ENABLE_LOCAL_LOOPBACK = false;
            StopRecording();
        }
    }

    void UpdateBackgroundMusicAttenuation() {
        smooth_bg_music_volume = Mathf.MoveTowards(smooth_bg_music_volume, smooth_bg_music_volume_target, Time.unscaledDeltaTime);
        smooth_bg_music_volume_target = Mathf.Min(1f, smooth_bg_music_volume_target + Time.unscaledDeltaTime / 8f);
        MusicManager_Start_MultiplayerPatch.musicsources_volume = Mathf.Max(0f, smooth_bg_music_volume * MusicManager_Start_MultiplayerPatch.musicvolume);
    }

    void UpdateMicrophoneLoopback() {
        if (ENABLE_LOCAL_LOOPBACK) {
            if (!UIBullshit.IsAnyMenuOpen() && Util.IsWorldGenerated()) {
                ENABLE_LOCAL_LOOPBACK = false;
            }
        } else if (ENABLE_LOCAL_LOOPBACK_VCINSTANCE != null) {
            Destroy(ENABLE_LOCAL_LOOPBACK_VCINSTANCE.gameObject);
            ENABLE_LOCAL_LOOPBACK_VCINSTANCE = null;
        }

        if (UIBullshit.main == null || UIBullshit.main.egg == null || !UIBullshit.main.egg.gameObject.activeInHierarchy) {
            return;
        }

        float newwriteheight = ENABLE_LOCAL_LOOPBACK_VCINSTANCE != null
            ? ENABLE_LOCAL_LOOPBACK_VCINSTANCE.curmaxvol_in_opus_decoder
            : 0f;
        if (!ENABLE_LOCAL_LOOPBACK) {
            newwriteheight = my_output_volume;
        }
        newwriteheight = UIMainMenu.writeX_flipflop ? -Mathf.Abs(newwriteheight) : Mathf.Abs(newwriteheight);

        if (Mathf.Abs(UIBullshit.main.egg.writeHeight) < Mathf.Abs(newwriteheight)) {
            UIBullshit.main.egg.writeHeight = KM.Clamp11(newwriteheight);
        }
    }

    private void Update() {
        system_framesize = CalculateFrameSizeForSamplerate(AudioSettings.outputSampleRate);
        listener_is_mindwiped = ScavPlayer.LocalPlayer != null
            && VCRULE_hearingloss
            && VCRULE_mindwiperule
            && ScavPlayer.LocalPlayer.IsAliveAndMindwiped();

        UpdateMicrophone();
        if (!Multiplayer.DedicatedServerDisableVisuals) {
            UpdateBackgroundMusicAttenuation();
        }
        UpdateMicrophoneLoopback();
    }

    public static void BodyOpenMouthForSpeaking(Body b, float volume) {
        b.eatTime = Math.Max(b.eatTime, volume - 0.05f);
    }

    public static void StartRecording(string micName) {
        StopRecording();
        last_mic_pos = 0;
        current_microphone = micName;

        try {
            mic_clip = Microphone.Start(GetMic(), true, 2, SAMPLE_RATE);
            IS_RECORDING = mic_clip != null;
            if (!IS_RECORDING) {
                log.error($" VOICECHAT: StartRecording(): Failed to start recording: {micName} ");
            }
        } catch (Exception e) {
            Plugin.log.LogInfo($" VOICECHAT: StartRecording(): Failed to set mic to: {micName} - {e}");
            current_microphone = NOMIC;
            IS_RECORDING = false;
        }

        if (!IS_RECORDING) {
            START_FAILURE_COUNT++;
            if (START_FAILURE_COUNT > MAX_FAILURES_UNTIL_DISABLE_VC) {
                Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("voicechat_mic_init_failed"), START_FAILURE_COUNT));
                START_FAILURE_COUNT = 0;
                current_microphone_mode = 0;
            }
        }
    }

    public static string GetMic() {
        return current_microphone == NOMIC ? null : current_microphone;
    }

    public static void StopRecording() {
        my_output_volume = 0f;
        if (IS_RECORDING) {
            SendAllAvailableMicData(true);
            my_mic_data_buffer.Clear();
            IS_RECORDING = false;
            Microphone.End(GetMic());
            Destroy(mic_clip);
            mic_clip = null;
        }
    }

    public static void SetMicrophone(string micName) {
        if (micName != current_microphone && Multiplayer.verbose) {
            Plugin.log.LogInfo($" VOICECHAT: SetMicrophone(): {micName} ");
        }
#if !DEBUG
        SettingsJsonThing.json.SetString(_PREF_MICNAME, micName);
#endif
        StartRecording(micName);
    }

    public static void SetMicrophoneMode(int mode) => SetMicrophoneMode((byte)mode);

    public static void SetMicrophoneMode(byte mode) {
        SettingsJsonThing.json.SetInt(_PREF_MICMODE, mode);
        current_microphone_mode = mode;
    }

    static void UpdateOpusComplexity() {
        if (encoder?.opus_encoder == null) {
            return;
        }
        _complexityupdatetimer += Time.unscaledDeltaTime;
        if (_complexityupdatetimer > 1f) {
            _complexityupdatetimer = 0f;
            int newcomp = (int)KM.RemapClamped(1f / Time.unscaledDeltaTime, 20f, 60f, 0f, 10f);
            if (newcomp != encoder.opus_encoder.GetComplexity()) {
                encoder.opus_encoder.SetComplexity(newcomp);
            }
        }
    }

    private void OnDestroy() {
        encoder?.Dispose();
        encoder = null;
    }

    private void OnApplicationQuit() {
        StopRecording();
        encoder?.Dispose();
        encoder = null;
        current_microphone_mode = 0;
    }
}
