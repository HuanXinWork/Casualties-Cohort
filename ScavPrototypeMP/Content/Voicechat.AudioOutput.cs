using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using OpusSharp.Core;
using OpusSharp.Core.Extensions;
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 流式环形缓冲音频播放器基类（承载远端语音与 MP3 音乐的 Opus 解码、低通遮挡滤波、音高追赶与零分配静音擦除）。
/// </summary>
public class StreamedAudioOutput : MonoBehaviour {
    protected bool is_voice = false;
    protected bool speech_impaired = false;
    protected bool is_body_alive = false;
    public Body body = null;

    public float max_hear_distance = 82f;
    public const float PREBUFFER_LATENCY = 0.1f;

    public int MIN_SAMPLECOUNT_PREBUFFERED => (int)(PREBUFFER_LATENCY * clip.frequency);
    internal int SAMPLE_RATE => clip.frequency;
    internal int FRAME_SIZE => Voicechat.CalculateFrameSizeForSamplerate(SAMPLE_RATE);

    public float cur_volume_output = 0f;
    protected float _real_custom_volume = 1f;

    public float custom_volume_set {
        get => _real_custom_volume;
        set => _real_custom_volume = Math.Max(0f, value);
    }

    public AudioLowPassFilter lowpass { get; protected set; }
    public AudioSource audioSource { get; protected set; }
    internal AudioClip clip;

    public int sample_rate { get; private set; }
    public int half_sample_rate => (int)(sample_rate * 0.5f);
    public int quarter_sample_rate => (int)(sample_rate * 0.25f);
    internal float vc_lastsample1;
    internal float vc_lastsample2;
    internal int vc_endpos;
    internal int vc_buffered_amount;

    protected float TimeTheBufferEnded = 0f;
    protected float TimeTheBufferFirstReceivedBlob = 0f;
    internal MaxCapacityQueue<float> vc_buffer = new(Voicechat.DEFAULT_SAMPLE_RATE);

    internal bool force_no_effects = false;

    protected static float[] opus_decode_buffer = new float[2048];
    OpusDecoder opus_decoder = null;

    public bool is_watered = false;
    public float pitchtarget = 1f;

    int _last_timeSamples = 0;
    float[] _playbackSamples;
    float[] _silenceSamples;

    public bool last_blob_was_last = false;
    double last_blob_receive_time;
    bool _warnedTooMuchSamples = false;

    delegate bool ClipWrite(AudioClip clip, float[] data, int numsamples, int samplesOffset);
    static ClipWrite _clipWrite;
    static bool _clipWriteMiss;

    internal void EnsureOpusDecoder() {
        if (opus_decoder == null || opus_decoder.GetSampleRate() != sample_rate) {
            opus_decoder?.Dispose();
            opus_decoder = new OpusDecoder(sample_rate, 1, false);
        }
    }

    public bool OpusDecode(ushort samplerate, byte[] data, out float[] voicedata) {
        SetSamplerate(samplerate);
        EnsureOpusDecoder();

        int decoded_blob_size = opus_decoder.Decode(data, data.Length, opus_decode_buffer, FRAME_SIZE, false);
        if (decoded_blob_size > 0) {
            voicedata = new float[decoded_blob_size];
            Buffer.BlockCopy(opus_decode_buffer, 0, voicedata, 0, decoded_blob_size * sizeof(float));
            return true;
        }
#if !RELEASE
        Plugin.log.LogWarning($" VOICECHAT: OpusDecode: Failed to decode opus??? (data, {data.Length}, opus_decode_buffer, {FRAME_SIZE})");
#endif
        voicedata = Array.Empty<float>();
        return false;
    }

    public override string ToString() {
        if (this is VoicechatOutput vco && vco.plr != null) {
            return $"VC_OUTPUT(plr:{vco.plr}, freq:{sample_rate})";
        }
        return $"STREAMED_AUDIO_OUTPUT({name}, freq:{sample_rate})";
    }

    public float GetFinalVolume() {
        float final_volume = _real_custom_volume;
        if (is_voice) {
            final_volume *= Voicechat.my_listener_volume;
            if (Voicechat.listener_is_mindwiped) {
                return final_volume;
            }
            final_volume *= 1.15f;
        } else {
            final_volume *= Voicechat.mp3_listener_volume;
        }
        if (Util.IsWorldGenerated()) {
            final_volume *= 1.3f;
        }
        return final_volume;
    }

    protected void UpdateLowPassEffects(in bool watered) {
        float muffle_target = 22000f;

        if (is_voice) {
            if (is_body_alive) {
                if (Voicechat.VCRULE_speechimpaired) {
                    if (watered) {
                        muffle_target = 1000f;
                    }
                    if (speech_impaired) {
                        muffle_target = 1200f;
                    }
                }
                if (Voicechat.listener_is_mindwiped) {
                    muffle_target = 300f;
                }
            }
        } else if (watered) {
            muffle_target = 1100f;
        }

        if (Voicechat.VCRULE_hearingloss && (!is_voice || is_body_alive) && Util.TryGetLocalBody(out Body locbody) && locbody.conscious) {
            Vector2 sourcepos = is_voice && is_body_alive
                ? body.GetHead().GetPosition()
                : (Vector2)audioSource.transform.position;

            if (Util.QuickGroundLinecastCheck(locbody.GetHead().GetPosition(), sourcepos)) {
                muffle_target *= 0.6f;
            }
        }

        if (muffle_target < 22000f) {
            lowpass.enabled = true;
            lowpass.cutoffFrequency = watered
                ? muffle_target
                : Mathf.MoveTowards(lowpass.cutoffFrequency, muffle_target, Time.unscaledDeltaTime * 45000f);
            audioSource.maxDistance = max_hear_distance * 0.5f;
            return;
        }

        if (lowpass.cutoffFrequency != 22000f) {
            lowpass.cutoffFrequency = Mathf.MoveTowards(lowpass.cutoffFrequency, 22000f, Time.unscaledDeltaTime * 45000f);
        } else {
            lowpass.enabled = false;
        }
        audioSource.maxDistance = max_hear_distance;
    }

    protected void UpdatePitchAligner() {
        float speed = Time.unscaledDeltaTime * 0.1f;
        if (MIN_SAMPLECOUNT_PREBUFFERED * 0.4f > vc_buffered_amount) {
            pitchtarget = last_blob_was_last ? Mathf.Min(pitchtarget, 0.998f) : Mathf.Min(pitchtarget, 0.94f);
        } else if (vc_buffered_amount > half_sample_rate) {
            pitchtarget = vc_buffered_amount > SAMPLE_RATE ? Mathf.Max(pitchtarget, 1.1f) : Mathf.Max(pitchtarget, 1.02f);
        }
        audioSource.pitch = Mathf.MoveTowards(audioSource.pitch, pitchtarget, speed);
    }

    void EffectsUpdate() {
        if (force_no_effects) {
            audioSource.spatialBlend = 0f;
            return;
        }

        if (Util.IsWorldGenerated()) {
            audioSource.spatialBlend = 1f;
            audioSource.bypassReverbZones = Voicechat.listener_is_mindwiped;
            if (Voicechat.VCRULE_hearingloss && !Voicechat.listener_is_mindwiped) {
                if (audioSource.outputAudioMixerGroup == null) {
                    audioSource.outputAudioMixerGroup = WorldGeneration.world.soundMixerGroup;
                }
            } else if (audioSource.outputAudioMixerGroup != null) {
                audioSource.outputAudioMixerGroup = null;
            }
        } else {
            audioSource.spatialBlend = 0f;
            if (audioSource.outputAudioMixerGroup != null) {
                audioSource.outputAudioMixerGroup = null;
            }
        }

        is_watered = false;
        if (is_voice) {
            if (is_body_alive) {
                is_watered = body.inWater;
                audioSource.dopplerLevel = VoicechatOutput.default_voice_doppler;
            } else {
                audioSource.dopplerLevel = 0f;
            }
        } else {
            is_watered = Mathf.Abs(transform.position.x) < WorldGeneration.world.halfWidth
                && Mathf.Abs(transform.position.y) < WorldGeneration.world.halfHeight
                && FluidManager.main.HasLiquid(WorldGeneration.world.WorldToBlockPos(transform.position));
        }

        UpdateLowPassEffects(is_watered);

        if (Util.IsInMainMenu()) {
            audioSource.maxDistance = max_hear_distance;
        } else if (is_voice) {
            bool is_voicechat = this is VoicechatOutput;
            audioSource.minDistance = audioSource.maxDistance * 0.24f;
            pitchtarget = 1f;

            if (is_voicechat && is_body_alive && Voicechat.VCRULE_speechimpaired) {
                float abshap = Mathf.Abs(body.opiateHappiness / 70f) * 0.5f;
                if (body.brainHealth < 93f) {
                    abshap = Mathf.Clamp01(Mathf.Max(abshap, KM.RemapClamped(body.brainHealth, 93f, 30f, 0f, 0.3f)));
                }
                if (abshap > 0.002f) {
                    pitchtarget += (float)Math.Sin(Time.realtimeSinceStartupAsDouble + (12.0 + abshap * 9.0)) * abshap;
                    pitchtarget += body.opiateHappiness > 0f ? (abshap * 0.01f) : (-abshap * 0.01f);
                }
            }
            UpdatePitchAligner();
            return;
        } else {
            audioSource.maxDistance = max_hear_distance;
            pitchtarget = 1f;
            UpdatePitchAligner();
            return;
        }

        pitchtarget = 1f;
        UpdatePitchAligner();
    }

    public void OnHearabilityRuleUpdate() {
        if ((bool)WorldGeneration.world && Voicechat.VCRULE_hearingloss) {
            audioSource.outputAudioMixerGroup = WorldGeneration.world.soundMixerGroup;
        } else {
            audioSource.outputAudioMixerGroup = null;
        }
    }

    protected virtual void Awake() {
        sample_rate = 24000;
        audioSource = this.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.volume = 1f;
        audioSource.spatialBlend = 1f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 10f;
        audioSource.maxDistance = max_hear_distance;
        audioSource.dopplerLevel = 0.3f;

        lowpass = this.AddComponent<AudioLowPassFilter>();
        lowpass.cutoffFrequency = 22000f;

        OnHearabilityRuleUpdate();
        SetSamplerate(Voicechat.DEFAULT_SAMPLE_RATE);
    }

    void Start() {
        vc_buffer.Clear();
    }

    protected void SetSamplerate(ushort newrate) {
        newrate = Voicechat.GetClosestSamplerate(newrate);
        if (clip != null) {
            if (clip.frequency != newrate) {
                audioSource.Stop();
                audioSource.clip = null;
                Destroy(clip);
                clip = null;
                vc_buffer.Clear();
                vc_buffer = new(newrate);
            } else {
                return;
            }
        }

        sample_rate = newrate;
        clip = AudioClip.Create("VOICE_CHAT_CLIP_" + name, newrate * 2, 1, newrate, false);
        audioSource.clip = clip;
    }

    protected void StartPlayback() {
        if (sample_rate == 0) {
            sample_rate = Voicechat.DEFAULT_SAMPLE_RATE;
        }

        vc_endpos = (vc_buffer.Count + audioSource.timeSamples) % clip.samples;
        vc_buffered_amount = vc_buffer.Count;
        if (_playbackSamples == null || _playbackSamples.Length != sample_rate) {
            _playbackSamples = new float[sample_rate];
        }
        float[] samples = _playbackSamples;

        for (int i = 0; i < samples.Length; i++) {
            if (vc_buffer.Count == 0) {
                samples[i] = vc_lastsample1;
                vc_lastsample1 = Mathf.MoveTowards(vc_lastsample1, 0f, 2.0f / sample_rate);
                continue;
            }
            vc_lastsample1 = vc_buffer.Dequeue();
            samples[i] = vc_lastsample1;
        }

        clip.SetData(samples, audioSource.timeSamples);
        EffectsUpdate();
        last_blob_receive_time = Time.realtimeSinceStartupAsDouble;

        if (Voicechat.VCRULE_speechimpaired && is_voice && body != null && body.alive) {
            audioSource.pitch = Mathf.Clamp(body.brainHealth * 0.012f + 0.08f, 0.7f, 1f);
        } else {
            audioSource.pitch = 1f;
        }

        audioSource.Play();
        _last_timeSamples = audioSource.timeSamples;
    }

    protected void StopPlayback() {
        vc_buffered_amount = 0;
        TimeTheBufferFirstReceivedBlob = 0f;
        audioSource.Stop();
    }

    static ClipWrite ClipWriter() {
        if (_clipWrite != null || _clipWriteMiss) {
            return _clipWrite;
        }
        MethodInfo method = AccessTools.Method(typeof(AudioClip), nameof(AudioClip.SetData), new[] { typeof(AudioClip), typeof(float[]), typeof(int), typeof(int) });
        if (method == null) {
            _clipWriteMiss = true;
            if (OptimizerMain.Log != null) {
                OptimizerMain.Log.LogWarning("[KrokV5Opt] audio silence kept allocating; counted SetData is missing");
            }
            return null;
        }
        var dyn = new DynamicMethod("krok_clip_silence", typeof(bool), new[] { typeof(AudioClip), typeof(float[]), typeof(int), typeof(int) }, typeof(StreamedAudioOutput).Module, true);
        ILGenerator il = dyn.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_2);
        il.Emit(OpCodes.Ldarg_3);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        _clipWrite = (ClipWrite)dyn.CreateDelegate(typeof(ClipWrite));
        return _clipWrite;
    }

    void WriteSilence(int count, int offset) {
        if (count <= 0 || clip == null) {
            return;
        }
        if (_silenceSamples == null || _silenceSamples.Length < count) {
            _silenceSamples = new float[count];
        }
        ClipWrite write = ClipWriter();
        int channels = clip.channels;
        if (write == null || channels <= 0) {
            float[] exact = _silenceSamples.Length == count ? _silenceSamples : new float[count];
            clip.SetData(exact, offset);
            return;
        }
        write(clip, _silenceSamples, count / channels, offset);
    }

    protected virtual void Update() {
        if (SharedMain.LocalWorldIsGenerating) {
            StopPlayback();
            vc_buffer.Clear();
            return;
        }

        if (!audioSource.isPlaying) {
            cur_volume_output = 0f;
        }

        if (vc_buffer.Count > 0) {
            TimeTheBufferFirstReceivedBlob += Time.unscaledDeltaTime;
            if (TimeTheBufferFirstReceivedBlob > PREBUFFER_LATENCY && !audioSource.isPlaying) {
                StartPlayback();
            }
        } else {
            TimeTheBufferEnded += Time.unscaledDeltaTime;
        }

        if (audioSource.isPlaying) {
            EffectsUpdate();

            int travelled_distance = audioSource.timeSamples - _last_timeSamples;
            if (_last_timeSamples > audioSource.timeSamples) {
                travelled_distance = audioSource.timeSamples - (_last_timeSamples - clip.samples);
            }
            vc_buffered_amount -= travelled_distance;

            if (travelled_distance > 0) {
                int silenceOffset = _last_timeSamples - 6;
                if (silenceOffset < 0) {
                    silenceOffset += audioSource.timeSamples;
                }
                WriteSilence(travelled_distance, silenceOffset);
            }

            _last_timeSamples = audioSource.timeSamples;
            if (vc_buffered_amount <= 0) {
                StopPlayback();
            }
        }
    }

    void OnDestroy() {
        opus_decoder?.Dispose();
        opus_decoder = null;

        if (audioSource != null && gameObject != audioSource.gameObject) {
            Destroy(audioSource.gameObject);
        }
        if (lowpass != null) {
            Destroy(lowpass);
        }
        if (audioSource != null) {
            Destroy(audioSource);
        }
        if (clip != null) {
            Destroy(clip);
        }
    }

    public void ReceiveVoiceBlob(ushort freq, in float[] data, in bool is_not_last) {
        SetSamplerate(freq);
        TimeTheBufferEnded = 0f;
        cur_volume_output = Voicechat.GetFrameMaxVol(data);

        float vol = GetFinalVolume();
        if (audioSource.isPlaying) {
            int new_buffered_amount = vc_buffered_amount + data.Length;
            if (new_buffered_amount >= clip.samples) {
                if (log.verbose && !_warnedTooMuchSamples) {
                    _warnedTooMuchSamples = true;
                    log.error($"VOICECHAT: TOO MUCH SAMPLES: {this}: {new_buffered_amount} >= {clip.samples}");
                }
            } else {
                for (int i = 0; i < data.Length; i++) {
                    data[i] = KM.Clamp11(data[i]) * vol;
                }
                if (!is_not_last) {
                    last_blob_was_last = true;
                }

                clip.SetData(data, vc_endpos);
                vc_endpos += data.Length;
                vc_buffered_amount = new_buffered_amount;
                if (vc_endpos >= clip.samples) {
                    vc_endpos -= clip.samples;
                }

                int left_distance = Math.Min(clip.samples - vc_buffered_amount, quarter_sample_rate);
                if (left_distance > 10) {
                    var data2 = new float[left_distance];
                    for (int i = 0; i < left_distance; i++) {
                        data2[i] = data[data.Length - 1] * Mathf.Clamp01(1f - (i / 24) * 3f);
                    }
                    clip.SetData(data2, vc_endpos);
                }
                last_blob_receive_time = Time.unscaledTimeAsDouble;
            }
        } else {
            for (int i = 0; i < data.Length; i++) {
                vc_buffer.Enqueue(KM.Clamp11(data[i]) * vol);
            }
        }

        if (Camera.main != null && KM.DistanceCheck(audioSource.transform.position, Camera.main.transform.position, max_hear_distance * 0.5f)) {
            Voicechat.smooth_bg_music_volume_target = Mathf.Min(
                Voicechat.smooth_bg_music_volume_target,
                1f - Mathf.Clamp01(cur_volume_output * 4f) * Voicechat.bg_music_attenuation);
        }
    }
}

internal class VoicechatOutput : StreamedAudioOutput {
    public static float default_voice_doppler = 0.23f;

    public ScavPlayer plr { get; private set; }
    public NetBody npc => plr.netBody;

    protected override void Awake() {
        is_voice = true;
        plr = GetComponent<ScavPlayer>();
        max_hear_distance = Voicechat.MAX_HEAR_DISTANCE;

        var vc_go = new GameObject("streamedaudiooutput");
        audioSource = vc_go.AddComponent<AudioSource>();
        audioSource.loop = true;
        audioSource.spatialBlend = 1f;
        audioSource.rolloffMode = AudioRolloffMode.Linear;
        audioSource.minDistance = 25f;
        audioSource.dopplerLevel = default_voice_doppler;
        audioSource.volume = 1f;

        lowpass = vc_go.AddComponent<AudioLowPassFilter>();
        lowpass.cutoffFrequency = 22000f;

        DontDestroyOnLoad(vc_go);
        SetSamplerate(Voicechat.DEFAULT_SAMPLE_RATE);
    }

    public Color CalculateVoiceChatIconColor() {
        return CalculateVoiceChatIconColor(out _);
    }

    public Color CalculateVoiceChatIconColor(out float realcurvol) {
        realcurvol = plr.IsLocal ? Voicechat.my_output_volume : cur_volume_output;
        Color c = plr.playerColor;
        return new Color(c.r, c.g, c.b, Mathf.Clamp01(realcurvol + 0.3f));
    }

    void LateUpdate() {
        if (plr != null) {
            is_body_alive = plr.IsAlive();
            if (is_body_alive) {
                body = plr.body;
                Vector2 og = audioSource.transform.position;
                Vector2 headpos = body.GetHead().GetPosition();

                if (!KM.DistanceCheck(og, headpos, 64f)) {
                    audioSource.transform.position = headpos;
                } else {
                    Vector2 newpos = Vector2.Lerp(KM.ClampDistance(og, headpos, 4f), headpos, Time.unscaledDeltaTime * 6f);
                    audioSource.transform.position = Vector2.Lerp(og, newpos, 0.5f);
                }

                Voicechat.BodyOpenMouthForSpeaking(body, cur_volume_output);
            } else if (Util.IsWorldGenerated()) {
                audioSource.transform.position = plr.CameraViewPos;
            } else if (Camera.main != null) {
                audioSource.transform.position = Camera.main.transform.position + Vector3.forward * 4f;
            }

            if (is_body_alive && Voicechat.VCRULE_speechimpaired) {
                if (body.talker.body != null) {
                    speech_impaired = plr.body.talker.impairedSpeech;
                }
            } else {
                speech_impaired = false;
            }
        } else {
            is_body_alive = false;
        }
    }
}

internal class VoicechatOutputDebugVisualiser : VoicechatOutput {
    public float curmaxvol_in_opus_decoder = 0f;

    protected override void Update() {
        base.Update();

        curmaxvol_in_opus_decoder = 0f;
        foreach (float item in opus_decode_buffer) {
            if (Mathf.Abs(curmaxvol_in_opus_decoder) < Mathf.Abs(item)) {
                curmaxvol_in_opus_decoder = item;
            }
        }
    }
}

internal class AudioOpusEncoding : IDisposable {
    public static ushort[] STANDART_FREQUENCIES = {
        8000,
        12000,
        16000,
        24000,
        48000,
    };

    internal byte[] opus_encode_buffer = new byte[1024];
    internal OpusEncoder opus_encoder = null;

    public byte[] Encode(in float[] data, ushort input_samplerate) {
        EnsureOpusEncoder(input_samplerate);
        try {
            int encoded_blob_len = opus_encoder.Encode(data, data.Length, opus_encode_buffer, opus_encode_buffer.Length);
            if (encoded_blob_len > 0) {
                var b = new byte[encoded_blob_len];
                Buffer.BlockCopy(opus_encode_buffer, 0, b, 0, encoded_blob_len);
                return b;
            }
        } catch (Exception e) {
            log.warn($"OPUS ENCODE ERROR: samplerate: {opus_encoder.GetSampleRate()}  data.Length:{data.Length} \n{e} ");
            return Array.Empty<byte>();
        }
        log.warn($"OPUS ENCODE ERROR: samplerate: {opus_encoder.GetSampleRate()}  data.Length:{data.Length} ");
        return Array.Empty<byte>();
    }

    void EnsureOpusEncoder(ushort target_samplerate) {
        if (opus_encoder == null || opus_encoder.GetSampleRate() != target_samplerate) {
            opus_encoder?.Dispose();
            opus_encoder = new OpusEncoder(target_samplerate, 1, OpusPredefinedValues.OPUS_APPLICATION_VOIP);
            opus_encoder.SetVbr(true);
        }
    }

    public void Dispose() {
        if (opus_encoder != null) {
            opus_encoder.Dispose();
            opus_encoder = null;
        }
    }
}
