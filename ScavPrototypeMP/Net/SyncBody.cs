using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 角色与生物躯体同步实体（SyncVar 声明与实体生命周期部分）。
    /// 注意：LiteEntitySystem 按反射元数据声明顺序建立 SyncVar 索引，本类所有 SyncVar 必须且只能集中声明在本文件中，严禁调整顺序或在分部文件中增删 SyncVar。
    /// </summary>
    [EntityFlags(EntityFlags.UpdateOnClient)]
    public partial class SyncBody : PredictableEntityLogic {
        public NetBody nb;
        public SyncVar<byte> characterSelected;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated | SyncFlags.AlwaysRollback)]
        internal SyncVar<Vector2> _position;
        private SyncVar<Vector2_4byte_200> _velocity;

        private SyncVar<SByteScalarVec2> _moveDir;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.LagCompensated)]
        private SyncVar<Vector2> _targetLookPos;
        private SyncVar<bool> _isRight;
        private SyncVar<bool> _crouching;

        internal SyncVar<bool> _standing;
        internal SyncVar<bool> _isPiggyback;
        private SyncVar<knetid> _piggyback;

        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<int> drng_attackCounter; // melee, Guns
        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<int> drng_useCounter; // useItem
        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<int> drng_damageCounter; // limb Impacts and Spider bites

        public SyncVar<bool> Unchipped;

        #region RagdollThing

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb0_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb0_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb0_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb0_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb1_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb1_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb1_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb1_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb2_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb2_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb2_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb2_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb3_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb3_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb3_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb3_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb4_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb4_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb4_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb4_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb5_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb5_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb5_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb5_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb6_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb6_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb6_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb6_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb7_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb7_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb7_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb7_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb8_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb8_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb8_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb8_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb9_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb9_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb9_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb9_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb10_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb10_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb10_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb10_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb11_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb11_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb11_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb11_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb12_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb12_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb12_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb12_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb13_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb13_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb13_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb13_angvel;

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_512> _ragdoll_limb14_pos;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<FloatAngle> _ragdoll_limb14_rot;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<Vector2_4byte_200> _ragdoll_limb14_vel;
        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.SyncGroup3)]
        private SyncVar<short> _ragdoll_limb14_angvel;

        #endregion

        internal bool readingRagdollData;

        public SyncBody(EntityParams entityParams) : base(entityParams) {
            _standing.Value = true;
        }

        protected override void OnConstructed() {
            CreateCharacter();
#if !RELEASE
            log.l($"CREATED SyncBody: {nb} owner:{OwnerId}");
#endif
            // LES 复用实体 ID 时会递增 Version，强制刷新关联容器三元组防止客户端卡在旧版本号上
            if (Net.IsServer) {
                KrokV5Optimization.FixInvSync.NoteContainerVersion(Id, Version);
            }
        }

        private bool CreateCharacter() {
            if (nb != null) {
                return true;
            }
            if (Util.IsInMainMenu()) {
                return false;
            }

            BasePlayer parent = GetParent<BasePlayer>();
            if (parent != null && parent.player != null) {
                if (parent.player.IsLocal) {
                    ScavPlayer.TryGetLocalNetBody(out nb);
                    SyncBody sb = this;
                    NetBody._Internal_RegisterLiteNetLib(nb, parent.player, ref sb);
#if FULLGAME
                    if (IsServer && nb != null && nb.body != null) {
                        byte want = (byte)CharacterSpawn.ClampChar(parent.player.selectedCharacter);
                        if (nb.body.charType != want) {
                            Together.log.warn($"[CharSelect] local body charType={nb.body.charType} want={want} — correcting charType (mesh may lag until respawn)");
                        }
                        CharacterSpawn.ApplySelectedToSyncBody(this, parent.player);
                        CharacterSpawn.EnsureBodySpecies(nb.body);
                    }
#endif
                } else {
#if FULLGAME
                    if (IsServer) {
                        CharacterSpawn.ApplySelectedToSyncBody(this, parent.player);
                    } else if (parent.player != null) {
                        parent.player.selectedCharacter = (byte)CharacterSpawn.ClampChar(characterSelected.Value);
                    }
#endif
                    nb = NetBody.CreateNewPlayerCharacter(parent.player, this);
                }
            } else {
                nb = NetBody.CreateNewNPC(_position.Value, null, this);
            }

            if (nb != null) {
                nb.syncBody = this;
                return true;
            }
            return false;
        }

        protected override void RegisterRPC(ref RPCRegistrator r) {
            base.RegisterRPC(ref r);

            r.BindOnChange(this, ref _velocity, _ => OnVelocityUpdate(), BindOnChangeFlags.ExecuteOnSync);
            r.BindOnChange(this, ref _moveDir, _ => OnMoveDirUpdate(), BindOnChangeFlags.ExecuteOnSync);
            r.BindOnChange(this, ref Unchipped, _ => OnUnchippedChange(), BindOnChangeFlags.ExecuteOnSync);
#if FULLGAME
            r.BindOnChange(this, ref characterSelected, _ => OnCharacterSelectedChanged(), BindOnChangeFlags.ExecuteOnSync);
#endif
        }

#if FULLGAME
        private void OnCharacterSelectedChanged() {
            byte chr = (byte)CharacterSpawn.ClampChar(characterSelected.Value);
            BasePlayer parent = GetParent<BasePlayer>();
            if (parent != null && parent.player != null) {
                parent.player.selectedCharacter = chr;
            }
            if (Util.IsInMainMenu()) {
                return;
            }
            ScavPlayer plr = parent != null ? parent.player : null;
            if (plr != null && Net.IsServer) {
                CharacterSpawn.ServerRebuildBodyIfSpeciesMismatch(plr);
                nb = plr.netBody;
                return;
            }
            // 客户端检测到远端物种不一致时重建角色躯体
            if (nb != null && nb.body != null && nb.body.charType != chr && !nb.IsBodyLocal()) {
                Together.log.warn($"[CharSelect] client recreate body id={Id} was={nb.body.charType} now={chr}");
                NetBody.DestroyNPC(nb);
                nb = null;
                if (plr != null) {
                    plr.body = null;
                    plr.selectedCharacter = chr;
                    nb = NetBody.CreateNewPlayerCharacter(plr, this);
                }
                if (nb != null) {
                    nb.syncBody = this;
                }
            } else if (nb == null) {
                CreateCharacter();
            }
        }
#endif

        private void OnUnchippedChange() {
            if (nb != null) {
                WorldMP.RunUnchippedLock.ApplyingSynced = true;
                try {
                    nb.SetUnchipped(Unchipped.Value);
                } finally {
                    WorldMP.RunUnchippedLock.ApplyingSynced = false;
                }
            }
        }

        protected override void OnDestroy() {
#if !RELEASE
            log.l($"OnDestroy SyncBody: {nb?.ToString()} owner:{OwnerId}");
#endif
            NetBody.DestroyNPC(nb);
        }

        protected override void Update() {
            base.Update();

            if (IsServer && CreateCharacter() && nb.IsLocal) {
                WriteBodyData();
            }
        }

        protected override void VisualUpdate() {
            if (CreateCharacter() && !nb.IsBodyLocal()) {
                ApplyDataToBody();
            }
        }

        internal void WriteBodyData() {
            Vector2 p = nb.GetPosition();
            _position.Value = p;
            _velocity.Value = nb.rb.velocity;

            if (nb.IsBodyLocal()) {
                _moveDir.Value = (SByteScalarVec2)nb.moveDir;
            }
            _targetLookPos.Value = nb.body.targetLookPos;
            _isRight.Value = nb.body.isRight;
            _crouching.Value = nb.body.crouching;
            _standing.Value = nb.body.standing;
            // 仅在角色非站立（布娃娃倒地）且未被背负时写入 15 肢体 × 4 个 SyncGroup3 字段，省去站立态每 Tick 60 次跨层物理属性读取与标脏
            if (nb?.body != null && !nb.body.standing && !nb.IsPiggybacking()) {
                WriteRagdollData();
            }

            _isPiggyback.Value = nb.IsPiggybacking();
            if (_isPiggyback.Value) {
                _piggyback.Value = nb.piggybacking_on.netId;
            }
        }

        public override string ToString() {
            if (nb != null) {
                return nb.ToString();
            }
            return $"SyncBody(Id: {Id})";
        }
    }
}
