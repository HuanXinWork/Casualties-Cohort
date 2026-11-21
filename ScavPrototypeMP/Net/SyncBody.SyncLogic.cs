using KrokV5Optimization;
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 角色与生物躯体同步实体的布娃娃读写、瞬移应用与物理插值驱动分部类。
    /// 注意：严禁在本分部文件中声明任何 SyncVar / SyncableField，以保护 SyncBody.cs 中的反射字段顺序。
    /// </summary>
    public partial class SyncBody {
        private const float _RAGDOLL_ANGVEL_QUANTIZE = 100f;

        internal void WriteRagdollData() {
            Limb[] limbs = nb.body.limbs;
            Limb limb;

            limb = limbs[0];
            _ragdoll_limb0_pos.Value = limb.GetPosition();
            _ragdoll_limb0_rot.Value = limb.rb.rotation;
            _ragdoll_limb0_vel.Value = limb.rb.velocity;
            _ragdoll_limb0_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[1];
            _ragdoll_limb1_pos.Value = limb.GetPosition();
            _ragdoll_limb1_rot.Value = limb.rb.rotation;
            _ragdoll_limb1_vel.Value = limb.rb.velocity;
            _ragdoll_limb1_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[2];
            _ragdoll_limb2_pos.Value = limb.GetPosition();
            _ragdoll_limb2_rot.Value = limb.rb.rotation;
            _ragdoll_limb2_vel.Value = limb.rb.velocity;
            _ragdoll_limb2_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[3];
            _ragdoll_limb3_pos.Value = limb.GetPosition();
            _ragdoll_limb3_rot.Value = limb.rb.rotation;
            _ragdoll_limb3_vel.Value = limb.rb.velocity;
            _ragdoll_limb3_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[4];
            _ragdoll_limb4_pos.Value = limb.GetPosition();
            _ragdoll_limb4_rot.Value = limb.rb.rotation;
            _ragdoll_limb4_vel.Value = limb.rb.velocity;
            _ragdoll_limb4_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[5];
            _ragdoll_limb5_pos.Value = limb.GetPosition();
            _ragdoll_limb5_rot.Value = limb.rb.rotation;
            _ragdoll_limb5_vel.Value = limb.rb.velocity;
            _ragdoll_limb5_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[6];
            _ragdoll_limb6_pos.Value = limb.GetPosition();
            _ragdoll_limb6_rot.Value = limb.rb.rotation;
            _ragdoll_limb6_vel.Value = limb.rb.velocity;
            _ragdoll_limb6_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[7];
            _ragdoll_limb7_pos.Value = limb.GetPosition();
            _ragdoll_limb7_rot.Value = limb.rb.rotation;
            _ragdoll_limb7_vel.Value = limb.rb.velocity;
            _ragdoll_limb7_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[8];
            _ragdoll_limb8_pos.Value = limb.GetPosition();
            _ragdoll_limb8_rot.Value = limb.rb.rotation;
            _ragdoll_limb8_vel.Value = limb.rb.velocity;
            _ragdoll_limb8_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[9];
            _ragdoll_limb9_pos.Value = limb.GetPosition();
            _ragdoll_limb9_rot.Value = limb.rb.rotation;
            _ragdoll_limb9_vel.Value = limb.rb.velocity;
            _ragdoll_limb9_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[10];
            _ragdoll_limb10_pos.Value = limb.GetPosition();
            _ragdoll_limb10_rot.Value = limb.rb.rotation;
            _ragdoll_limb10_vel.Value = limb.rb.velocity;
            _ragdoll_limb10_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[11];
            _ragdoll_limb11_pos.Value = limb.GetPosition();
            _ragdoll_limb11_rot.Value = limb.rb.rotation;
            _ragdoll_limb11_vel.Value = limb.rb.velocity;
            _ragdoll_limb11_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[12];
            _ragdoll_limb12_pos.Value = limb.GetPosition();
            _ragdoll_limb12_rot.Value = limb.rb.rotation;
            _ragdoll_limb12_vel.Value = limb.rb.velocity;
            _ragdoll_limb12_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[13];
            _ragdoll_limb13_pos.Value = limb.GetPosition();
            _ragdoll_limb13_rot.Value = limb.rb.rotation;
            _ragdoll_limb13_vel.Value = limb.rb.velocity;
            _ragdoll_limb13_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);

            limb = limbs[14];
            _ragdoll_limb14_pos.Value = limb.GetPosition();
            _ragdoll_limb14_rot.Value = limb.rb.rotation;
            _ragdoll_limb14_vel.Value = limb.rb.velocity;
            _ragdoll_limb14_angvel.Value = Util_MPExtensions.QuantizeFloatToShort(limb.rb.angularVelocity, _RAGDOLL_ANGVEL_QUANTIZE);
        }

        private bool _RagdollReadIndividualLimb(Limb limb, ref SyncVar<Vector2_4byte_512> syncpos, ref SyncVar<FloatAngle> syncrot, ref SyncVar<Vector2_4byte_200> syncvel, ref SyncVar<short> syncangvel) {
            if (limb == null || limb.dismembered || limb.rb == null) {
                return true;
            }
            // Group3 刚启用时插值器初值为 0，直接读最新快照避免短暂摔倒在插值追近前已结束导致姿态丢失
            Vector2 at = syncpos.Value;
            Vector2 root = nb.body.transform.position;
            if ((at - root).sqrMagnitude > 400f) {
                return true;
            }
            // 远端肢体置为运动学快照受控态，防止本地物理求解器与报文姿态互相拉扯
            Rigidbody2D rb = limb.rb;
            if (rb.interpolation != RigidbodyInterpolation2D.None) {
                rb.interpolation = RigidbodyInterpolation2D.None;
            }
            if (!rb.isKinematic) {
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
                rb.isKinematic = true;
            }
            rb.position = at;
            rb.rotation = syncrot.Value;
            return true;
        }

        private static void ReleaseRemoteLimbs(Body body) {
            if (body == null || body.limbs == null) {
                return;
            }
            Limb[] limbs = body.limbs;
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb != null && limb.rb != null && limb.rb.isKinematic) {
                    limb.rb.isKinematic = false;
                }
            }
            if (body.rb != null && body.rb.isKinematic) {
                body.rb.isKinematic = false;
            }
        }

        internal void ReadRagdollData() {
            Limb[] limbs = nb.body.limbs;
            if (!_RagdollReadIndividualLimb(limbs[0], ref _ragdoll_limb0_pos, ref _ragdoll_limb0_rot, ref _ragdoll_limb0_vel, ref _ragdoll_limb0_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[1], ref _ragdoll_limb1_pos, ref _ragdoll_limb1_rot, ref _ragdoll_limb1_vel, ref _ragdoll_limb1_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[2], ref _ragdoll_limb2_pos, ref _ragdoll_limb2_rot, ref _ragdoll_limb2_vel, ref _ragdoll_limb2_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[3], ref _ragdoll_limb3_pos, ref _ragdoll_limb3_rot, ref _ragdoll_limb3_vel, ref _ragdoll_limb3_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[4], ref _ragdoll_limb4_pos, ref _ragdoll_limb4_rot, ref _ragdoll_limb4_vel, ref _ragdoll_limb4_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[5], ref _ragdoll_limb5_pos, ref _ragdoll_limb5_rot, ref _ragdoll_limb5_vel, ref _ragdoll_limb5_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[6], ref _ragdoll_limb6_pos, ref _ragdoll_limb6_rot, ref _ragdoll_limb6_vel, ref _ragdoll_limb6_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[7], ref _ragdoll_limb7_pos, ref _ragdoll_limb7_rot, ref _ragdoll_limb7_vel, ref _ragdoll_limb7_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[8], ref _ragdoll_limb8_pos, ref _ragdoll_limb8_rot, ref _ragdoll_limb8_vel, ref _ragdoll_limb8_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[9], ref _ragdoll_limb9_pos, ref _ragdoll_limb9_rot, ref _ragdoll_limb9_vel, ref _ragdoll_limb9_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[10], ref _ragdoll_limb10_pos, ref _ragdoll_limb10_rot, ref _ragdoll_limb10_vel, ref _ragdoll_limb10_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[11], ref _ragdoll_limb11_pos, ref _ragdoll_limb11_rot, ref _ragdoll_limb11_vel, ref _ragdoll_limb11_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[12], ref _ragdoll_limb12_pos, ref _ragdoll_limb12_rot, ref _ragdoll_limb12_vel, ref _ragdoll_limb12_angvel)) return;
            if (!_RagdollReadIndividualLimb(limbs[13], ref _ragdoll_limb13_pos, ref _ragdoll_limb13_rot, ref _ragdoll_limb13_vel, ref _ragdoll_limb13_angvel)) return;
            _RagdollReadIndividualLimb(limbs[14], ref _ragdoll_limb14_pos, ref _ragdoll_limb14_rot, ref _ragdoll_limb14_vel, ref _ragdoll_limb14_angvel);
        }

        public void ApplyBodyTeleportPacket(BodyTeleportPacket data) {
            if (!CreateCharacter()) {
                return;
            }
#if DEBUG
            if (log.verbose) {
                log.devevent($"BodyTeleportPacket SB {nb}", data.Position);
            }
#endif
            _position.Value = data.Position;
            _velocity.Value = data.Velocity;
            _isRight.Value = data.IsRight;
            _isPiggyback.Value = data.IsPiggyback;
            _piggyback.Value = data.Piggyback;

            ApplyBodyTeleportPacket(nb, data);
        }

        public static void ApplyBodyTeleportPacket(NetBody nb, BodyTeleportPacket data) {
            log.devevent($"BodyTeleportPacket NB {nb}", data.Position);

            if (data.RagdollTime <= 0f) {
                nb.body.ForceStand();
            }

            nb.SetBodyPosition(data.Position);
            nb.body.SetVelocity(data.Velocity);
            nb.body.shock = data.RagdollTime;

            if (nb.body.standing) {
                nb.body.ForceSwitchDir(data.IsRight);
            }

            if (data.IsPiggyback) {
                if (NetBody.TryGetNetBodyFromId(data.Piggyback, out NetBody target_nb)) {
                    nb.StartPiggyback(target_nb, false, true);
                }
            } else if (nb.piggybacking_on != null) {
                nb.StopPiggyback();
            }
        }

        private void OnVelocityUpdate() {
            if (Util.IsWorldGenerated() && CreateCharacter() && !nb.IsLocal && nb.body.standing) {
                nb.body.rb.velocity = Vector2.Lerp(nb.body.GetVelocity(), _velocity.Value, 0.7f);
            }
        }

        private void OnMoveDirUpdate() {
        }

        internal void ApplyDataToBody() {
            Body body = nb.body;

            body.targetLookPos = _targetLookPos.InterpolatedValue;
            body.moveDir = (Vector2)_moveDir.Value;
            body.crouching = _crouching.Value;
            body.handSlot = _offHand.Value ? 1 : 0;

            if (_isPiggyback.Value) {
                if (NetBody.TryGetNetBodyFromId(_piggyback.Value, out NetBody target_nb)) {
                    nb.StartPiggyback(target_nb, false, true);
                }
            } else if (nb.IsPiggybacking() && Time.unscaledTime - nb.piggybackStartedUnscaled < 2f) {
                // 背负发起后的 2 秒宽限期内忽略服务端尚未就位的地面姿态快照，防止刚背起又被拉回地面
            } else {
                if (nb.IsPiggybacking()) {
                    nb.StopPiggyback();
                }

                if (Net.IsServer || SharedMain.PositionIsInSimulatedChunk(nb.position)) {
                    if (nb.body.standing) {
                        Vector2 interp = _position.InterpolatedValue;
                        nb.SetBodyPosition(interp);
                        EnemyMoveProbe.Note(this, EnemyMoveProbe.Interp, interp);
                        body.ForceSwitchDir(_isRight.Value);
                    } else {
                        if (!KM.DistanceCheck(body.transform.position, _position.Value, 10f)) {
                            Vector2 raw = _position.Value;
                            nb.SetBodyPosition(raw);
                            EnemyMoveProbe.Note(this, EnemyMoveProbe.RagdollSnap, raw);
                        } else {
                            EnemyMoveProbe.Note(this, EnemyMoveProbe.RagdollHold, body.transform.position);
                        }
                    }

                    if (body.IsBodyLocal() && body.alive) {
                        ConsoleScript.instance.noClipPos = body.transform.position;
                        if (Util.IsGeneratingWorld() && !Con._DEV_FREECAM) {
                            PlayerCamera.main.transform.position = body.transform.position;
                        }
                    }

                    body.shock = _standing.Value ? 0f : 100f;
                    if (body.standing != _standing.Value) {
                        if (_standing.Value) {
                            ReleaseRemoteLimbs(body);
                            body.ForceStand();
                        } else {
                            body.Ragdoll();
                        }
                    }

                    // 攀爬绳索时的预测平滑以抑制远端绳索抖动
                    if (body.currentClimbable != null && !body.grounded) {
                        ClimbableGrabInfo grabInfo = body.currentClimbable.GetGrabInfo(body.transform.position);
                        if (grabInfo.distanceToPlayer < 1f) {
                            float predictedProgress = body.climbableProgress + (_velocity.Value.y * 0.1f);
                            if (predictedProgress >= body.currentClimbable.totalLength) {
                                body.climbableProgress = body.currentClimbable.totalLength - 0.0001f;
                                body.climbVelocity = 0f;
                            } else if (predictedProgress <= 0f) {
                                body.climbVelocity = 0f;
                            } else {
                                body.climbVelocity = _velocity.Value.y;
                                body.climbableProgress = Mathf.Clamp01(grabInfo.pathDistance);
                            }
                        } else if (grabInfo.distanceToPlayer > 2f) {
                            body.StopClimbing();
                        }
                    }
                } else {
                    Vector2 raw = _position.Value;
                    ReleaseRemoteLimbs(nb.body);
                    nb.body.ForceStand();
                    nb.SetBodyPosition(raw);
                    EnemyMoveProbe.Note(this, EnemyMoveProbe.SimOut, raw);
                }
            }

            // 仅客户端应用 SyncGroup3 布娃娃快照（宿主本身负责物理模拟），同时冻结根刚体并暂停 Animator 防止覆盖骨骼姿态
            readingRagdollData = !Net.IsServer && IsSyncGroupEnabled(SyncGroup.SyncGroup3) && !nb.body.standing && !nb.IsPiggybacking();
            if (readingRagdollData) {
                Rigidbody2D rootRb = body.rb;
                if (rootRb != null && !rootRb.isKinematic) {
                    rootRb.velocity = Vector2.zero;
                    rootRb.angularVelocity = 0f;
                    rootRb.interpolation = RigidbodyInterpolation2D.None;
                    rootRb.isKinematic = true;
                }
                ReadRagdollData();
            }
            bool animate = !readingRagdollData;
            if (body.bodyAnimator != null && body.bodyAnimator.enabled != animate) {
                body.bodyAnimator.enabled = animate;
            }
            if (body.armsAnimator != null && body.armsAnimator.enabled != animate) {
                body.armsAnimator.enabled = animate;
            }

            if (!body.IsBodyLocal() && body.sleeping && body.moveDir != Vector2.zero) {
                body.sleeping = false;
            }
        }
    }
}
