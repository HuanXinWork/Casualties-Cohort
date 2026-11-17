using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using static WorldGeneration;

namespace Together {

    public struct knetid : IEquatable<knetid> {
        public ushort id;

        public knetid(ushort id) {
            this.id = id;
        }
        public static implicit operator ushort(knetid id) {
            return id.id;
        }
        public static implicit operator knetid(ushort id) {
            return new knetid(id);
        }
        public override string ToString() {
            return id.ToString();
        }

        // dictionary lookups boxed this. the generic comparer uses these two and does not box.
        public bool Equals(knetid other) => id == other.id;

        public override bool Equals(object other) => other is knetid value && value.id == id;

        public override int GetHashCode() => id;
    }

    // to store info that isnt really useful for querying searches
    public struct SteamServerInfoInLobbyExtraData {
        public int deathcounter;
        public MultiplayerGameRules rules;

        public SteamServerInfoInLobbyExtraData(NetPublicServerInfo info) {
            rules = info.rules;
        }

        public void Read(NetPublicServerInfo info) {
            info.rules = rules;

            {
                Dictionary<string, string> defaults = (new MultiplayerGameRules()).ConvertToSerializableDict();
                Dictionary<string, string> thisrules = rules.ConvertToSerializableDict();

                IEnumerable<KeyValuePair<string, string>> diffrules = thisrules.Where(x => !defaults[x.Key].Equals(x.Value));

                if (diffrules.Count() > 0) {
                    info.rules_as_string = Lang.Get("ruleschanged");
                    foreach (KeyValuePair<string, string> fld in diffrules) {
                        info.rules_as_string += $"\n{fld.Key}: {fld.Value}";
                    }
                } else {
                    info.rules_as_string = Lang.Get("rulesdefault");
                }
            }
        }
    }

    public class NetPublicServerInfo : INetSerializable {
        public bool dedicated;
        public bool haspassword;
        public string version = "6.7.6.7";
        public string name = "";
        public string gamemode = "vanilla";
        public string description = "";

        public KSteam.Lobby steam_lobby_info = null; // used by steam transport and server browser
        public MultiplayerGameRules rules;

        public int plr_living_count;
        public int plr_count;
        public int plr_max = 8;

        public int cur_layer = -1; // -1 is mainmenu
        public int cur_depth;
        public string lang;

        public int avg_happiness; // inspire from PlayerCamera.HandleDeathScreen

        public string[] modlist = new string[0];
        public bool latejoinspectate => rules.LateJoinSpectate;

        public NetPublicServerInfo SetValues() {
            dedicated = Net.IsDedicatedServer;
            haspassword = !String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD);
            plr_max = Multiplayer.rules.PLAYER_COUNT_LIMIT;
            midjoin_lock_active = Net.IsMyServerMidjoinLocked();
            rules = Multiplayer.rules;

            bool isinworld = Util.IsInWorld();
            if (isinworld) {
                plr_count = ScavPlayer.ClientIdToPlayerDict.Count;
                plr_living_count = ScavPlayer.AllLivingPlayers.Count;
                cur_layer = WorldGeneration.world.biomeDepth;

                if (ScavPlayer.AllLivingPlayers.Count > 0) {
                    avg_happiness = (int)ScavPlayer.AllLivingPlayers.Average(x => (int)x.body.happiness);
                }
            } else {
                plr_count = ScavPlayer.ClientIdToPlayerDict.Count;
                plr_living_count = plr_count;
                cur_layer = -1;
                avg_happiness = 100;
            }
            if (Util.IsGeneratingWorld()) {
                gamemode = "loading";
            } else if (Util.IsTutorialWorld()) {
                gamemode = "tutorial";
            } else {
                GamemodeBase gm = GamemodeManager.GetGamemode();
                if (gm == null) {
                    if (!isinworld) {
                        gamemode = "mainmenu";
                    } else {
                        gamemode = WorldGeneration.world.biomeOverride == OverrideSceneType.Debug ? "debug_world" : "vanilla";
                    }
                } else {
                    gamemode = GamemodeManager.GetGamemode().GetType().Name;
                }
            }

            return this;
        }

        // DONT SERIALIZE THIS !!!
        public bool enforcemodlist_locked;
        public bool midjoin_lock_active; // this actually means that the game is mid-game and latejoin is not allowed
        public string friends_tooltip = null;
        public string rules_as_string = null;
        public List<ulong> steamfriends = new();
        // real RTT from browser ConnectP2P probe; -1 = not sampled / unknown
        public int pingMs = -1;

        public bool IsJoinable() {
            return !enforcemodlist_locked && !midjoin_lock_active && Multiplayer.FULL_VERSION_TAG == this.version;
        }

        public bool HasPing => pingMs >= 0;

        public void Deserialize(NetDataReader reader) {
            reader.Get(out version);
            reader.Get(out dedicated);
            reader.Get(out haspassword);
            reader.Get(out name);
            reader.Get(out description);
            reader.Get(out gamemode);
            reader.Get(out plr_living_count);
            reader.Get(out plr_count);
            reader.Get(out plr_max);
            reader.Get(out cur_layer);
            reader.Get(out cur_depth);
            reader.Get(out avg_happiness);
            reader.Get(out rules);
        }

        public void Serialize(NetDataWriter writer) {
            writer.Put(version);
            writer.Put(dedicated);
            writer.Put(haspassword);
            writer.Put(name);
            writer.Put(description);
            writer.Put(gamemode);
            writer.Put(plr_living_count);
            writer.Put(plr_count);
            writer.Put(plr_max);
            writer.Put(cur_layer);
            writer.Put(cur_depth);
            writer.Put(avg_happiness);
            writer.Put(rules);
        }
    }

    public interface INetSerializeByMemcpy {
    }
}
