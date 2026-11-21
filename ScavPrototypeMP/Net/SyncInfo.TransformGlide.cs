using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {
    /// <summary>
    /// SyncInfo 的客户端物理变换应用、平滑插值、水晶怪突进平滑与本地散落物重力接管分部类。
    /// 注意：严禁在本分部文件中声明任何 SyncVar / SyncString / SyncList，以保护 SyncInfo.cs 中的反射字段顺序。
    /// </summary>
    public partial class SyncInfo {
        private void UpdateTransform() {
            byte data = container_data1.Value;
            bool released = data == ContainerEncoding.WorldByte && poseSeen && posePrevData != ContainerEncoding.WorldByte;
            if (data != ContainerEncoding.WorldByte) {
                poseHold = false;
                poseHoldUntil = 0f;
            } else if (released && !poseFresh) {
                // 原地垂直丢弃时若量化坐标与入包前完全一致，服务端可能不下发新 _position；设超时自动解冻防止掉落物永久悬空
                poseHold = true;
                poseHoldUntil = Time.unscaledTime + NetTimingConstants.DroppedItemPoseHoldTimeout;
            } else if (poseHold && Time.unscaledTime >= poseHoldUntil) {
                poseHold = false;
            }
            poseSeen = true;
            posePrevData = data;
            poseFresh = false;

            if (data != ContainerEncoding.WorldByte) {
                return;
            }
            if (IsItem() && go.transform.parent != null) {
                return;
            }
            if (IsIgnored() || poseHold) {
                return;
            }

            ApplyTransform();
        }

        public bool shouldApplyPos;
        public bool shouldApplyAng;
        private readonly RaycastHit2D[] raycastHitBuffer = new RaycastHit2D[8];
        private static int _groundMask = int.MinValue;

        private bool GlidePose(out Vector2 pos, out float angle) {
            Vector2 raw = _position.Value;
            float rawAng = _rotation.Value.Degrees;
            if (Net.IsServer) {
                pos = raw;
                angle = rawAng;
                return false;
            }

            float now = Time.fixedTime;
            ushort tick = Net.ServerTick;
            bool moved = _poseGlide
                && ((raw - _poseTo).sqrMagnitude > 0.0001f || Mathf.Abs(Mathf.DeltaAngle(_poseToAng, rawAng)) > 0.5f);
            if (!_poseGlide) {
                _poseGlide = true;
                _poseFrom = _poseTo = raw;
                _poseFromAng = _poseToAng = rawAng;
                _poseTick = tick;
                _poseStart = now;
                _poseDur = 0f;
            } else if (moved) {
                float prevT = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
                _poseFrom = Vector2.Lerp(_poseFrom, _poseTo, prevT);
                _poseFromAng = Mathf.LerpAngle(_poseFromAng, _poseToAng, prevT);
                _poseTo = raw;
                _poseToAng = rawAng;

                int span = Mathf.Max(1, LiteEntitySystem.Utils.SequenceDiff(tick, _poseTick));
                float rate = Mathf.Max(1f, Net.ServerEntitySendRate);
                float dur = span / rate;
                float cap = Mathf.Max(Time.fixedDeltaTime * 12f, 1f / rate);
                if (dur > cap) {
                    dur = cap;
                }

                bool snap = (_poseTo - _poseFrom).sqrMagnitude > 48f * 48f;
                if (snap) {
                    _poseFrom = _poseTo;
                    _poseFromAng = _poseToAng;
                    dur = 0f;
                }
                _poseStart = now;
                _poseDur = dur;
                _poseTick = tick;
                if (snap) {
                    pos = raw;
                    angle = rawAng;
                    return true;
                }
            }

            float t = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
            pos = Vector2.Lerp(_poseFrom, _poseTo, t);
            angle = Mathf.LerpAngle(_poseFromAng, _poseToAng, t);
            return false;
        }

        // 远距休眠区块在宿主的采样常维持静止零速，若客户端站在散落物旁本地悬空下落，避免被每步宿主零速快照拽回单步重力
        private bool LocalOwnsLoose(bool isRigid, Rigidbody2D rb) {
            if (!Net.IsClient || !isRigid || rb == null) {
                return false;
            }
            if (rb.bodyType != RigidbodyType2D.Dynamic || !rb.simulated) {
                return false;
            }
            if (go.transform.parent != null) {
                return false;
            }
            if (!SharedMain.WorldInSimRange(rb.position)) {
                return false;
            }
            Vector2 hostVel = _velocity.Value;
            if (hostVel.y < -0.5f || hostVel.sqrMagnitude > 4f) {
                return false;
            }
            Vector2 host = _position.Value;
            Vector2 here = rb.position;
            if (Mathf.Abs(host.x - here.x) > 0.5f) {
                return false;
            }
            if (host.y > here.y + 0.15f) {
                return true;
            }
            if (_groundMask == int.MinValue) {
                _groundMask = LayerMask.GetMask("Ground");
            }
            return Physics2D.Raycast(here, Vector2.down, 0.8f, _groundMask).collider == null;
        }

        // 水晶怪快照按 30 Hz 到达，客户端按实测收包间隔平滑插值渲染，消除高速突进时的跳帧阶梯感
        internal void ClientCrystalPose() {
            if (go == null || Net.IsServer) {
                return;
            }
            Vector2 raw = _position.Value;
            float rawAng = _rotation.Value.Degrees;
            float now = Time.time;
            bool moved = _poseGlide
                && ((raw - _poseTo).sqrMagnitude > 0.0004f || Mathf.Abs(Mathf.DeltaAngle(_poseToAng, rawAng)) > 0.5f);
            if (!_poseGlide) {
                _poseGlide = true;
                _poseFrom = _poseTo = raw;
                _poseFromAng = _poseToAng = rawAng;
                _poseStart = now;
                _poseDur = 0f;
                _poseSample = now;
            } else if (moved) {
                float prevT = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
                _poseFrom = Vector2.Lerp(_poseFrom, _poseTo, prevT);
                _poseFromAng = Mathf.LerpAngle(_poseFromAng, _poseToAng, prevT);
                _poseTo = raw;
                _poseToAng = rawAng;
                float gap = now - _poseSample;
                _poseSample = now;
                if (gap < Time.deltaTime) {
                    gap = Time.deltaTime;
                }
                if (gap > 0.2f) {
                    gap = 0.2f;
                }
                if ((_poseTo - _poseFrom).sqrMagnitude > 48f * 48f) {
                    _poseFrom = _poseTo;
                    _poseFromAng = _poseToAng;
                    gap = 0f;
                }
                _poseStart = now;
                _poseDur = gap;
            }
            float t = _poseDur > 0f ? Mathf.Clamp01((now - _poseStart) / _poseDur) : 1f;
            if (go.TryGetComponent(out Rigidbody2D rb) && rb != null) {
                rb.interpolation = RigidbodyInterpolation2D.None;
                rb.velocity = Vector2.zero;
                rb.angularVelocity = 0f;
            }
            go.transform.SetPositionAndRotation(Vector2.Lerp(_poseFrom, _poseTo, t), Quaternion.Euler(0f, 0f, Mathf.LerpAngle(_poseFromAng, _poseToAng, t)));
        }

        private void ApplyTransform() {
            if (!Net.IsServer && go != null && go.TryGetComponent(out CrystalEnemy _)) {
                ClientCrystalPose();
                return;
            }

            Transform trans = go.transform;
            Vector2 currentPos = trans.position;
            float currentAngle = trans.rotation.eulerAngles.z;
            Vector2 scale = _scale.Value;
            bool isRigid = go.TryGetComponent(out Rigidbody2D rb);

            if (scale.x != trans.localScale.x || scale.y != trans.localScale.y) {
                trans.localScale = new Vector3(scale.x, scale.y, trans.localScale.z);
            }

            if (LocalOwnsLoose(isRigid, rb)) {
                return;
            }

            bool poseSnap = GlidePose(out Vector2 pos, out float angle);

            shouldApplyPos = true;
            shouldApplyAng = true;

            bool prevRelax = relaxed_combat_sync;
            if (tracker.isVisible && isRigid) {
                RigidbodyType2D newRigidType = rb_dynamic.Value ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;
                if (rb.bodyType != newRigidType) {
                    rb.bodyType = newRigidType;
                }
                if (rb_dynamic.Value && itemLock.Value) {
                    rb.GetOrAddComponent<ItemLock>();
                }

                if (IsContactDangerous() && Util.TryGetLocalBody(out Body body)) {
                    if (shouldApplyPos) {
                        Vector2 direction = KM.ClampLength(pos - currentPos, 10f);
                        int hitCount = rb.Cast(direction, raycastHitBuffer, 10f);

                        bool didHitLocalBody = false;
                        for (int i = 0; i < hitCount; i++) {
                            Transform hitTrans = raycastHitBuffer[i].transform;
                            if (hitTrans != null && Util.TryGetBodyFromObject(hitTrans.gameObject, out Body hitBody) && hitBody == body) {
                                didHitLocalBody = true;
                                break;
                            }
                        }
                        relaxed_combat_sync = didHitLocalBody;
                    } else if (shouldApplyAng && relaxed_combat_sync) {
                        angle = Mathf.LerpAngle(currentAngle, angle, 0.1f);
                    }
                }
            }

            if (relaxed_combat_sync || prevRelax) {
                pos = Vector2.Lerp(currentPos, pos, 0.2f);
                angle = Mathf.LerpAngle(currentAngle, angle, 0.2f);
            } else if (isRigid && rb.bodyType == RigidbodyType2D.Dynamic && rb.simulated) {
                rb.velocity = _velocity.Value;
                rb.angularVelocity = angvel;
            }

            EnemyMoveProbe.NoteObject(this, pos, currentPos, poseSnap);
            if (shouldApplyPos) {
                trans.position = pos;
                shouldApplyPos = false;
            }
            if (shouldApplyAng) {
                trans.rotation = Quaternion.Euler(0f, 0f, angle);
                shouldApplyAng = false;
            }
        }

        public bool IsContactDangerous() {
            return go.TryGetComponent(out SpiderHandler _) || go.TryGetComponent(out DamagingCrate _);
        }
    }
}
