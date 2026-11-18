using CasualtiesTogetherUtils;
using LiteEntitySystem;
using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace Together;

[AttributeUsage(AttributeTargets.Field)]
public class MultiplayerRuleChoiceAttribute : Attribute {
    public string[] choices;
    public MultiplayerRuleChoiceAttribute(string[] choices) {
        this.choices = choices;
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class MultiplayerRuleByteAttribute : Attribute {
    public byte min = 0;
    public byte limit;
    public string postfix;
    public MultiplayerRuleByteAttribute(byte max = 100, string unit = "%") {
        limit = max;
        postfix = unit;
    }
    public MultiplayerRuleByteAttribute(byte min, byte max, string unit = "") {
        // 补齐下界字段赋值，避免规则校验时最小阈值退化为 0
        this.min = min;
        limit = max;
        postfix = unit;
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class MultiplayerRuleFloatAttribute : Attribute {
    public float mi;
    public float ma;
    public string postfix;
    public MultiplayerRuleFloatAttribute(float min, float max, string unit = "") {
        mi = min;
        ma = max;
        postfix = unit;
    }
}

/// <summary>
/// 联机房间规则结构（严禁调整实例字段顺序或增删字段，依赖 <see cref="INetSerializeByMemcpy"/> 原始内存拷贝）。
/// </summary>
[Serializable]
public partial struct MultiplayerGameRules : INetSerializeByMemcpy {

#if !RELEASE
    public bool sv_cheats = true;
    public bool AllowClientCheatCommands = true;
#else
    public bool sv_cheats = false;
    public bool AllowClientCheatCommands = false;
#endif

    [MultiplayerRuleByte(Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT, "")]
#if TESTING
    public byte PLAYER_COUNT_LIMIT = 200;
#else
    public byte PLAYER_COUNT_LIMIT = 6;
#endif

    public bool ShowPlayerDirections = true;
    public bool EnableNametags = true;
    public bool EnableStatusIcons = true;
    public bool UnchippedHideNametags = true;

    public bool EnableChatbox = true;

    public bool OnlyProximityChat = false;
    public bool UnchippedProximityChat = true;

    public bool UnchippedIsIndividual = true; // in pre-run settings, everyone can choose on their own to be chipped or no. if this is "false", everyone is same as host

    public bool ConsentForFeeding = true;

    [MultiplayerRuleByte]
    public byte ScatterMinGroupSize = 0;
    [MultiplayerRuleFloat(0, 1500, "M")]
    public float ScatterPunishDistance = 80; // in meters
    public bool PlayerScatterDisallowed => Multiplayer.rules.ScatterPunishDistance != 0 && Multiplayer.rules.ScatterPunishDistance < 449; // 1499 in tiles to meters

    [MultiplayerRuleByte(101)]
    public byte LayerFinishPlrPercent = 60; // 0 = one player, 50 = half, 100 = everyone alive must be at the end, 101 = disable

    public bool LayerFinishKeepXOffset = true;

    [MultiplayerRuleByte]
    public byte StragglerRadlinePercent = 30;
    public bool RadlineExtraDamage = true;

    public bool NoInventoryLock = false; // steal stuff from other inventory even when theyre conscious

    public bool EnableSleep = false;
    public bool EnableTimeManipulation = false;

    public bool SpeechImpairedChat = true;
    public bool HearingLossChat = true;
    public bool SpeechImpairedVC = true;
    public bool HearingLossVC = true;
    public bool MindwipeDisablesChat = true;
    public bool DeadTextchat = true;
    public bool DeadVoicechat = true;
    public bool SleepingMute = false;
    public bool TrackPlayerDeaths = true;

    public bool Permadeath = false;
    public bool ReviveOnNextLevel = false;
    public bool ReviveFromTrader = true;
    public bool TraderReviveAsSpecies = false;

    public bool RespawnKeepInventory = false;
    public bool RespawnKeepSkills = false;

    public bool AllowSpectatorFreecam = true;
    public bool AllowPush = true;

    public bool AlwaysAllowCarry = false;
    public bool AllowPiggyback => PiggybackMaxStack > 0;
    [MultiplayerRuleByte(254, "")]
#if RELEASE
    public byte PiggybackMaxStack = 1;
#else
    public byte PiggybackMaxStack = 3;
#endif
    [MultiplayerRuleFloat(0, 2, "")]
    public float PiggybackWeightMultiplier = 0.8f;

    [MultiplayerRuleByte(1, 50, "")]
    public byte MinigameMaxPlayers = 50;

    public bool SpectateWhileUnconscious = false;

    public bool EnableMP3Sync = true;
    [MultiplayerRuleByte(4, "")]
    public byte VoicechatQuality = 4;
    public bool VoicechatEnabled = true;

    [MultiplayerRuleFloat(0, 1500, "M")]
    public float ProximityHearDistance = 55; // in meters

    public bool CharacterYapPublic = true;

    public bool Teams = false;
    public bool PVP = false;
    public bool PVPCombatDismember = true;
    public float PVPMoodDebuff = 0.5f;
    public float PVPDamageMultiplier = 1f;

    public bool LateJoinAllowed = true;
    public bool LateJoinSpectate = false;
    public bool SpawnOnPlayer = true;

    public bool AmputateHealthyPlayers = true;

    public float AdditionalBrainRegen = 1f;
    public float AdditionalHealthRegen = 1f;
    public float AdditionalHealthDecay = 1f;
    public bool LastStandAllowed = true;

    // teammate list greys out and stops tracking people past wound-view range
    public bool TeammateListUsesRange = true;

    public float SelfharmWitnessMoodDebuff = 3f;

    public bool DisconnectShouldSaveAnything => SavePlayerState || SavePlayerInventory || SavePlayerPosition;
    public bool SavePlayerState = true;
    public bool SavePlayerInventory = true;
    public bool SavePlayerPosition = true;
    // debug map normally skips mp_save; set true to read/write saves there like a normal run
    public bool DebugWorldSaveSystem = false;

    // DEDICATED SERVER STUFF
    public bool AutoContinue = false;
    public ushort AutoMinPlrsToStart = 2;
    public bool AutoExitWhenAllDied = true;
    public bool AutoExitWhenAllLeft = true;

    // getters
    public bool CanReviveOnNextLevel() {
        return !Permadeath && ReviveOnNextLevel;
    }
    public bool CanReviveFromTrader() {
        return !Permadeath && ReviveFromTrader;
    }
    public bool CanCommunicateWithTheDeadTC() {
        return DeadTextchat || Util.IsTutorialWorld();
    }
    public bool CanCommunicateWithTheDeadVC() {
        return DeadVoicechat || Util.IsTutorialWorld();
    }
    public bool CheckIfLayerContinueIsDisabled() {
        return LayerFinishPlrPercent > 100;
    }
    public bool DisableSleep => !EnableSleep;
    public bool DisableTimeManipulation => !EnableTimeManipulation;

    public Dictionary<string, string> ConvertToSerializableDict() {
        var v = new Dictionary<string, string>();

        var prev = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            foreach (var fld in this.GetType().GetFields()) {
                var ogvalue = fld.GetValue(this);
                v[fld.Name] = ogvalue.ToString();
            }
        } finally {
            CultureInfo.CurrentCulture = prev;
        }

        return v;
    }

    public static MultiplayerGameRules ConvertFromSerializableDict(Dictionary<string, string> r) {
        var v = new MultiplayerGameRules();

        foreach (var item in r) {
            var rule_name = item.Key;
            var rule_value = item.Value;

            var field2 = v.GetType().GetField(rule_name);
            if (field2 == null) {
                log.warn($"RULE DECODER: UNKNOWN RULE: {rule_name} (attempt to set it to: {rule_value})");
                continue;
            }
            object obj4;

            try {
                if (field2.FieldType == typeof(bool)) {
                    obj4 = Con.ParseBool01(rule_value);
                } else {
                    obj4 = System.ComponentModel.TypeDescriptor.GetConverter(field2.FieldType).ConvertFromInvariantString(rule_value);
                }
            } catch (Exception e) {
                log.warn($"RULE DECODER: SET RULE, BUT VALUE IS INVALID:\nINPUT: {rule_name} = {rule_value}\n{e.Message}");
                continue;
            }

            TypedReference rulesref = __makeref(v);
            field2.SetValueDirect(rulesref, obj4);
        }

        return v;
    }

    public void SetDefault() {
    }

    public MultiplayerGameRules() {
    }
}

public class TagPlayerInfo {
    public Multiplayer.IntroductionPlayerInfo intro;
    public knetid plrId;
    public NetPlayer netPlayer;
    public ScavPlayer plr;
    public Dictionary<string, object> data;
}

[AttributeUsage(AttributeTargets.Method)]
public abstract class NetworkMessageReceiverAttribute : Attribute {

    public enum ReceiverType : byte {
        Client,
        Server
    }

    public byte GetMessageId() {
        if (this is ServerReceiverAttribute skra) {
            return (skra.messageId);
        }
        if (this is ClientReceiverAttribute ckra) {
            return (ckra.messageId);
        }
        throw new AccessViolationException();
    }
    public string GetMessageName() {
        return Net.ByteMsgIdToName(GetMessageId());
    }
    public ReceiverType GetReceiverType() {
        if (this is ServerReceiverAttribute) {
            return ReceiverType.Server;
        }
        if (this is ClientReceiverAttribute) {
            return ReceiverType.Client;
        }
        throw new AccessViolationException();
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class CTPostInitAttribute : Attribute { }

[AttributeUsage(AttributeTargets.Method)]
public class ServerReceiverAttribute : NetworkMessageReceiverAttribute {
    public byte messageId { get; private set; }
    public ServerReceiverAttribute(byte name) {
        this.messageId = name;
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class ClientReceiverAttribute : NetworkMessageReceiverAttribute {
    public byte messageId { get; private set; }
    public bool ignore_host { get; private set; }
    public ClientReceiverAttribute(byte name, bool ignore_host = true) {
        this.messageId = name;
        this.ignore_host = ignore_host;
    }
}

abstract public class CasualtiesTogetherSingleton : MonoBehaviour { }
abstract public class CasualtiesTogetherSingletonWorldOnly : MonoBehaviour { }
