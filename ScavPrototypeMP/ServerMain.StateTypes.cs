using CasualtiesTogetherUtils;
using LiteEntitySystem;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

public struct PlayerSavedState {
    public bool unchipped;
    public int savedonlayer;
    public Vector2 position;

    public Color24 plrcolor;

    // species for mid-run rejoin (skip char-select menu)
    public byte charType;

    public CharacterHealthPainkillerStateSyncPacket painkiller;
    public CharacterHealthStateSyncPacket health;

    public List<int> RecipesHasCrafterBefore = null;
    public Dictionary<string, string> MindMemory = null;

    public GameObject invholder;
    public Item[] inventory;

    public PlayerSavedState(Body body) {
        position = body.transform.position;
        savedonlayer = WorldGeneration.world.biomeDepth;
        charType = (byte)Mathf.Clamp(body != null ? body.charType : 0, 0, 2);

        health = new CharacterHealthStateSyncPacket();
        health.Write(body);
        if (body.TryGetComponent(out Painkillers _)) {
            painkiller = new CharacterHealthPainkillerStateSyncPacket(body);
        }

        if (body.TryGetNetBody(out var nb)) {
            unchipped = nb.unchipped;
            if (nb.IsPlayer) {
                RecipesHasCrafterBefore = new(nb.RecipesHasCrafterBefore);
                MindMemory = new(nb.MindMemory);
                plrcolor = nb.plr.playerColor;
            }
        }

        // inventory park is host-only. a client SAVESTATE_ left syncinfos live and made leave ghosts.
        if (Net.IsServer && Multiplayer.rules.SavePlayerInventory) {
            invholder = new GameObject($"SAVESTATE_{nb}");
            inventory = new Item[8];

            var l = new List<Item>(8);
            for (int i = 0; i < body.slots.Length; i++) {
                var t = body.GetItem(i);
                if (t != null) {
                    l.Add(t);
                    inventory[i] = t;
                }
            }
            l.AddRange(body.GetAllWearables());

            foreach (var item in l) {
                item.transform.SetParent(invholder.transform);
            }

            // host keeps the meshes for rejoin Apply, but SyncInfo must die or
            // later clients spawn floaters at the leave spot.
            Item[] parked = invholder.GetComponentsInChildren<Item>(true);
            for (int i = 0; i < parked.Length; i++) {
                if (parked[i] != null) {
                    NetObjectRegistry.DetachSyncKeepMesh(parked[i].gameObject);
                }
            }

            invholder.SetActive(false);
        }
    }

    public void Apply(Body body) {
        // is on the same layer
        if (Multiplayer.rules.SavePlayerPosition && savedonlayer == WorldGeneration.world.biomeDepth) {
            if (body.TryGetNetBody(out var nb)) {
                if (nb.IsPlayer) {
                    nb.plr.ServerPlayerState.did_give_spawn_location_from_a_save = true;
                }
            }
            body.transform.position = position;
        }
        if (Multiplayer.rules.SavePlayerState) {
            health.Apply(body);
            painkiller.Apply(body);
            if (body.TryGetNetBody(out var nb)) {
                nb.SetUnchipped(unchipped);
                if (nb.IsPlayer) {
                    if (MindMemory != null) {
                        nb.MindMemory = MindMemory;
                    }
                    if (RecipesHasCrafterBefore != null) {
                        nb.RecipesHasCrafterBefore = RecipesHasCrafterBefore;
                        PlayerCheckpoint.SendCrafted(nb);
                    }
                    nb.plr.playerColor = plrcolor;
                }
            }
        }
        if (Multiplayer.rules.SavePlayerInventory && invholder != null && inventory != null) {
            invholder.SetActive(true);

            for (int i = 0; i < inventory.Length; i++) {
                var item = inventory[i];
                if (item != null) {
                    item.transform.parent = null;
                    body.PickUpItem(item, i, true);
                }
            }

            var l = new List<Item>(8);
            foreach (Transform t in invholder.transform) {
                if (t != null && t.TryGetComponent(out Item item)) {
                    l.Add(item);
                }
            }

            foreach (var item in l) { // wear the wearables
                body.AutoPickUpItem(item);
            }
        }
        // because the state gets delete instantly just delete the thing
        if (invholder != null) {
            GameObject.Destroy(invholder);
        }
    }

    // the holder still owns the items. used when a later layer loads the file instead.
    public void Discard() {
        if (invholder != null) {
            GameObject.Destroy(invholder);
            invholder = null;
        }
    }
}

public struct LastBeforeGenerationState : INetSerializeByMemcpy {
    public MultiplayerGameRules rules;

    public UnityEngine.Random.State randomstate;// its 16 bytes
    public byte biomeOverride;
    public bool radlinedisable;
    public bool unchipped;
    public byte biomeDepth;
    public float lootRarityMultiplier;
    public float trapRarityMultiplier;
    public int totalTraveled;

    public bool censorMood;
    public LastBeforeGenerationState() {
        this.rules = Multiplayer.rules;

        this.randomstate = UnityEngine.Random.state;

        biomeOverride = (byte)WorldGeneration.world.biomeOverride;
        biomeDepth = (byte)WorldGeneration.world.biomeDepth;
        lootRarityMultiplier = WorldGeneration.world.lootRarityMultiplier;
        trapRarityMultiplier = WorldGeneration.world.trapRarityMultiplier;
        totalTraveled = WorldGeneration.world.totalTraveled;

        censorMood = Body.censorMood;

        this.unchipped = WorldGeneration.unchipped;
    }

    // CALL ONLY RIGHT BEFORE GENERATION
    public void Apply() {
        UnityEngine.Random.state = this.randomstate;

        Multiplayer.rules = this.rules;

        WorldGeneration.world.biomeOverride = (WorldGeneration.OverrideSceneType)biomeOverride;
        WorldGeneration.world.biomeDepth = biomeDepth;
        WorldGeneration.world.lootRarityMultiplier = lootRarityMultiplier;
        WorldGeneration.world.trapRarityMultiplier = trapRarityMultiplier;
        WorldGeneration.world.totalTraveled = totalTraveled;

        Body.censorMood = censorMood;

        if (!Multiplayer.rules.UnchippedIsIndividual) {
            WorldGeneration.world.SetUnchipped(this.unchipped);
        }
    }

    public static int GetByteSize() {
        return System.Runtime.InteropServices.Marshal.SizeOf(typeof(LastBeforeGenerationState));
    }
}

public struct SerializableRandomState : INetSerializeByMemcpy { // its 16 bytes
    public UnityEngine.Random.State State;
}

abstract public class BaseComponentTracker<T> : MonoBehaviour where T : MonoBehaviour {
    public T og { get; private set; }
    public SyncInfoGameObjectTracker tracker => GetComponent<SyncInfoGameObjectTracker>();

    protected abstract void TrackerAwake();
    void Awake() {
        og = GetComponent<T>();
        TrackerAwake();
    }
}

/// <summary>
/// 世界环境状态全局同步单例（严禁改变 <see cref="SyncVar{T}"/> 声明顺序，保持 LES 字段偏移兼容）。
/// </summary>
[EntityFlags(EntityFlags.UpdateOnClient)]
public class ServerWorldStateSingleton : SingletonEntityLogic {

    public SyncVar<Vector2_4byte_512> shuttleDoorPosition;

    public SyncVar<float> timeScale;

    public SyncVar<bool> server_is_generating_world;
    public SyncVar<bool> radlineactive;
    public SyncVar<float> radlinestate;
    public SyncVar<float> layerTimeSpent;
    public SyncVar<float> earthquakeDelay;
    public SyncVar<float> earthquakeTime;
    public SyncVar<bool> has_surface_bg;
    public SyncVar<Color32> surface_color;
    public SyncVar<float> rainintensity;
    public SyncVar<float> ambientLightIntensity;

    public void Server_Serialize() {
        server_is_generating_world.Value = SharedMain.LocalWorldIsGenerating;

        {
            if (RadiationLine.line != null) {
                radlineactive.Value = (RadiationLine.line.active);
                radlinestate.Value = (RadiationLine_Update_MultiplayerPatch.timeGone);
            } else {
                radlineactive.Value = false;
            }
            if (WorldGeneration.world != null) {
                layerTimeSpent.Value = (WorldGeneration.world.layerTimeSpent);
                earthquakeDelay.Value = (WorldGeneration.world.earthquakeDelay);
                earthquakeTime.Value = (WorldGeneration.world.earthquakeTime);

                has_surface_bg.Value = WorldMP.HasSkyBackground(out var skyinfo);
                if (has_surface_bg) {
                    surface_color.Value = (skyinfo.Item1);
                    rainintensity.Value = (skyinfo.Item2);
                }

                if (Time.timeScale.IsFinite())
                    timeScale.Value = Time.timeScale;
            } else {
                layerTimeSpent.Value = 0;
                has_surface_bg.Value = false;
                timeScale.Value = 1;
            }
        }
    }

    public void Deserialize() {
        if (WorldGeneration.world != null) {
            if (has_surface_bg.Value != WorldMP.HasSkyBackground()) {
                if (has_surface_bg.Value) {
                    WorldMP.CreateSkyBackground(surface_color.Value, rainintensity.Value);
                } else {
                    WorldMP.RemoveSkyBackground();
                }
            }

            if (earthquakeDelay.Value < -1) { // when host is asleep, without this, it causes the clients restart earthquake timer while host actually doesnt have any earthquake happennnin
                WorldGeneration.world.earthquakeDelay = 999999;
            } else {
                WorldGeneration.world.earthquakeDelay = earthquakeDelay.Value;
            }

            WorldGeneration.world.earthquakeTime = earthquakeTime.Value;
            WorldGeneration.world.layerTimeSpent = layerTimeSpent.Value;
        }

        if (RadiationLine.line != null && RadiationLine.line.active != radlineactive.Value) {
            if (radlineactive) {
                RadiationLine.line.Activate();
            } else {
                RadiationLine.line.Deactivate();
            }
        }

        if (Util.IsInWorld() && timeScale.Value.IsFinite()) {
            Time.timeScale = timeScale.Value;
        }

        RadiationLine_Update_MultiplayerPatch.timeGone = radlinestate.Value;
    }

    protected override void OnConstructed() {
        ambientLightIntensity.Value = 0.12f;
        base.OnConstructed();
    }

    public ServerWorldStateSingleton(EntityParams entityParams) : base(entityParams) {
        shuttleDoorPosition.Value = new Vector2(0, 500);
        timeScale.Value = 1;
    }

    protected override void Update() {
        if (IsServer) {
            Server_Serialize();
        } else if (IsClient) {
            Deserialize();
        }
    }

    protected override void VisualUpdate() {
    }
}
