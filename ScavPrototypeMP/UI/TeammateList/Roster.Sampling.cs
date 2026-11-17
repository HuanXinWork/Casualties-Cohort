using System.Reflection;
using Together.MpConstants;
using UnityEngine;

namespace Together.UI.TeammateList {
    /// <summary>
    /// 队友状态列表分帧采样器：按 0.5 秒步进轮询采样队友血量、生命危险度、意识/饱食度、情绪及全量生理 Moodle 状态图标。
    /// </summary>
    internal sealed partial class Roster {
        private int _rr;
        private int _step;
        private float _nextStep;
        private Slot _job;

        private static FieldInfo _lineField;
        private static bool _lineReady;

        private static FieldInfo _colorField;
        private static bool _colorReady;

        private void StepJob() {
            if (_step == 0) {
                if (Time.unscaledTime >= _nextStep) {
                    _nextStep = Time.unscaledTime + StepGap;
                    _job = NextOccupied();
                    if (_job != null) {
                        _step = 1;
                    }
                }
                return;
            }

            Slot job = _job;
            _job = null;
            int step = _step;
            _step = 0;
            if (job == null || job.Player == null) {
                return;
            }

            BindBody(job);
            if (job.Body == null) {
                return;
            }

            switch (step) {
                case 1:
                    ReadBlood(job);
                    ReadDanger(job);
                    _job = job;
                    _step = 2;
                    break;
                case 2:
                    ReadVitals(job);
                    _job = job;
                    _step = 3;
                    break;
                case 3:
                    ReadMood(job);
                    _job = job;
                    _step = 4;
                    break;
                default:
                    ReadYap(job);
                    break;
            }
        }

        private Slot NextOccupied() {
            int high = _high;
            if (high == 0) {
                return null;
            }
            int start = _rr >= high ? 0 : _rr;
            for (int i = 0; i < high; i++) {
                int idx = start + i;
                if (idx >= high) {
                    idx -= high;
                }
                Slot slot = _slots[idx];
                if (slot.Player != null) {
                    _rr = idx + 1;
                    return slot;
                }
            }
            return null;
        }

        private void ReadBlood(Slot slot) {
            Body body = slot.Body;
            string name = slot.Player.playerName ?? "";
            if (slot.Name != name) {
                slot.Name = name;
            }
            if (!slot.ChipReadable) {
                if (slot.Blood != ChipNotDetected) {
                    slot.Blood = ChipNotDetected;
                }
                return;
            }

            float scale = body.bloodScale;
            float currentLiters = body.bloodToLitersBody(body.bloodVolume) * scale;
            float maxLiters = body.bloodToLitersBody(FullBlood) * scale;
            string bloodText = "Blood: " + FormatLiters(currentLiters) + "L/" + FormatLiters(maxLiters) + "L";
            if (slot.Blood != bloodText) {
                slot.Blood = bloodText;
            }
        }

        private void ReadDanger(Slot slot) {
            Body body = slot.Body;
            // 实验体标准血量 5L * bloodScale（60% 处红色饱和，每分钟失血达总血量 20% 时饱和），其余生理指标跨物种归一化
            float scale = body.bloodScale;
            float full = 5f * scale;
            float liters = body.bloodToLitersBody(body.bloodVolume) * scale;
            float bleed = body.bloodToLiters(body.totalBleedSpeed * scale) * 60f;
            float satiety = (body.hunger + body.thirst) * 0.5f;
            float heartRate = body.heartRate;

            float danger = Mathf.InverseLerp(full, full * 0.6f, liters);
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, full * 0.2f, bleed));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 60f, body.brainHealth));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 50f, body.bloodOxygen));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, -60f, body.totalHappiness));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, 100f, body.averagePain));
            danger = Mathf.Max(danger, Mathf.InverseLerp(100f, 15f, satiety));
            danger = Mathf.Max(danger, Mathf.InverseLerp(0f, 35f, body.septicShock));
            danger = Mathf.Max(danger, Mathf.InverseLerp(60f, 0f, heartRate));
            danger = Mathf.Max(danger, Mathf.InverseLerp(140f, 200f, heartRate));
            slot.Danger = danger;
        }

        private void ReadVitals(Slot slot) {
            Body body = slot.Body;
            if (!slot.ChipReadable) {
                if (slot.Mind != ChipNotDetected) {
                    slot.Mind = ChipNotDetected;
                }
            } else {
                string mindText = "Consciousness: " + Mathf.RoundToInt(body.consciousness) + "%";
                if (slot.Mind != mindText) {
                    slot.Mind = mindText;
                }
            }

            string satietyText = "Satiety: " + Mathf.RoundToInt((body.hunger + body.thirst) * 0.5f);
            if (slot.Satiety != satietyText) {
                slot.Satiety = satietyText;
            }
        }

        private void ReadMood(Slot slot) {
            if (!slot.ChipReadable) {
                if (slot.Mood != ChipNotDetected) {
                    slot.Mood = ChipNotDetected;
                }
            } else {
                string moodText = "Mood: " + Mathf.RoundToInt(slot.Body.totalHappiness) + "%";
                if (slot.Mood != moodText) {
                    slot.Mood = moodText;
                }
            }
            FillMoodles(slot);
        }

        private void ReadYap(Slot slot) {
            Talker talker = slot.Body.talker;
            if (talker != null) {
                NoteYap(slot.Body, CurrentLine(talker));
            }
        }

        private void FillMoodles(Slot slot) {
            slot.MoodleCount = 0;
            Body body = slot.Body;
            MoodleManager mgr = MoodleManager.main;
            if (body == null || !body.alive || mgr == null || mgr.icons == null || WorldGeneration.world == null) {
                return;
            }

            if (body.focusedLevel > 0f) {
                Push(slot, mgr, 8, "focused");
            } else if (body.horrifiedLevel > 0f) {
                Push(slot, mgr, body.horrifiedLevel <= 50f ? 1 : 3, body.horrifiedLevel <= 50f ? "impendingdoom" : "horrified");
            }

            if (TeammateListHost.ViewerUnchipped && !body.inCardiacArrest) {
                if (body.heartRate > 110f) {
                    Push(slot, mgr, body.heartRate > 160f ? (body.heartRate > 200f ? 2 : 1) : 0, "tachycardia");
                } else if (body.heartRate < 60f) {
                    Push(slot, mgr, body.heartRate < 40f ? 1 : 0, "bradycardia");
                }
                if (body.fibrillationProgress > 50f || body.heartRate > 240f) {
                    Push(slot, mgr, 3, "palpitations");
                }
            }

            if (body.lastStandTime > 0f) {
                Push(slot, mgr, 8, "lastleg");
            }

            if (body.brainHealth < 30f) {
                Push(slot, mgr, 3, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 60f) {
                Push(slot, mgr, 2, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 80f) {
                Push(slot, mgr, 1, "braindamage", chippedOnly: true);
            } else if (body.brainHealth < 95f) {
                Push(slot, mgr, 0, "braindamage", chippedOnly: true);
            }

            if (body.strokeAmount > 70f) {
                Push(slot, mgr, 3, "stroke", chippedOnly: true);
            }

            if (body.inCardiacArrest) {
                Push(slot, mgr, 3, "cardiacarrest");
            } else if (body.fibrillationProgress > 75f) {
                Push(slot, mgr, 3, "arrythmia", chippedOnly: true);
            } else if (body.fibrillationProgress > 50f) {
                Push(slot, mgr, 2, "arrythmia", chippedOnly: true);
            } else if (body.fibrillationProgress > 15f) {
                Push(slot, mgr, 1, "arrythmia", chippedOnly: true);
            }

            if (!body.inCardiacArrest) {
                if (body.bloodPressure < 60f) {
                    Push(slot, mgr, 3, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 83f) {
                    Push(slot, mgr, 2, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 96f) {
                    Push(slot, mgr, 1, "hypotension", chippedOnly: true);
                } else if (body.bloodPressure < 110f) {
                    Push(slot, mgr, 0, "hypotension", chippedOnly: true);
                }

                if (body.bloodPressure > 180f) {
                    Push(slot, mgr, 3, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 162f) {
                    Push(slot, mgr, 2, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 145f) {
                    Push(slot, mgr, 1, "hypertension", chippedOnly: true);
                } else if (body.bloodPressure > 130f) {
                    Push(slot, mgr, 0, "hypertension", chippedOnly: true);
                }
            }

            if (!body.breathing) {
                Push(slot, mgr, 3, "cantbreathe");
            } else if (body.respiratoryRate < 50f) {
                Push(slot, mgr, 1, "hypoventilation");
            } else if (body.respiratoryRate < 90f) {
                Push(slot, mgr, 0, "hypoventilation");
            }

            bool hasHead = AnatomyConstants.IsValidLimbIndex(body, (int)LimbIndex.Head);
            bool hasUpperTorso = AnatomyConstants.IsValidLimbIndex(body, (int)LimbIndex.UpperTorso);
            Limb headLimb = hasHead ? body.limbs[(int)LimbIndex.Head] : null;
            Limb torsoLimb = hasUpperTorso ? body.limbs[(int)LimbIndex.UpperTorso] : null;

            if (torsoLimb != null && torsoLimb.muscleHealth < 4f) {
                Push(slot, mgr, 3, "thoraxdestroyed");
            }
            if (body.hemothorax > 40f) {
                Push(slot, mgr, 1, "hemothorax", chippedOnly: true);
            }
            if (body.hasPulmonaryEmbolism) {
                Push(slot, mgr, 3, "pulmonaryembolism", chippedOnly: true);
            }

            if (body.bloodOxygen < 45f) {
                Push(slot, mgr, 3, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 60f) {
                Push(slot, mgr, 2, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 75f) {
                Push(slot, mgr, 1, "oxygen", chippedOnly: true);
            } else if (body.bloodOxygen < 90f) {
                Push(slot, mgr, 0, "oxygen", chippedOnly: true);
            }

            if (body.radiationSickness > 80f) {
                Push(slot, mgr, 3, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 50f) {
                Push(slot, mgr, 2, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 30f) {
                Push(slot, mgr, 1, "irradiated", chippedOnly: true);
            } else if (body.radiationSickness > 10f) {
                Push(slot, mgr, 0, "irradiated", chippedOnly: true);
            }

            if (body.averagePain > 80f) {
                Push(slot, mgr, 3, "pain");
            } else if (body.averagePain > 55f) {
                Push(slot, mgr, 2, "pain");
            } else if (body.averagePain > 30f) {
                Push(slot, mgr, 1, "pain");
            } else if (body.averagePain > 10f) {
                Push(slot, mgr, 0, "pain");
            }

            if (body.painShock > 0.3f) {
                Push(slot, mgr, 3, "shock");
            }

            if (body.TryGetComponent<Painkillers>(out Painkillers painkillers)) {
                float opiate = painkillers.actualOpiateReception;
                if (opiate > 80f) {
                    Push(slot, mgr, 3, "overdose", chippedOnly: true);
                } else if (opiate > 50f) {
                    Push(slot, mgr, 2, "overdose", chippedOnly: true);
                } else if (opiate > 20f) {
                    Push(slot, mgr, 1, "overdose", chippedOnly: true);
                } else if (opiate > 5f) {
                    Push(slot, mgr, 0, "overdose", chippedOnly: true);
                }

                if (opiate < -34f) {
                    Push(slot, mgr, 3, "withdrawal", chippedOnly: true);
                } else if (opiate < -25f) {
                    Push(slot, mgr, 2, "withdrawal", chippedOnly: true);
                } else if (opiate < -15f) {
                    Push(slot, mgr, 1, "withdrawal", chippedOnly: true);
                } else if (opiate < -5f) {
                    Push(slot, mgr, 0, "withdrawal", chippedOnly: true);
                }
            }

            if (body.onHardStimulants) {
                Push(slot, mgr, 6, "stimulants", chippedOnly: true);
            }

            if (headLimb != null && headLimb.muscleHealth < 14.4f) {
                Push(slot, mgr, 3, "concussion", chippedOnly: true);
            } else if (body.consciousness <= 20f) {
                Push(slot, mgr, body.sleeping ? 4 : 3, body.sleeping ? "asleep" : "unconscious");
            } else if (body.consciousness < 30f) {
                Push(slot, mgr, 3, "confused");
            } else if (body.consciousness < 55f) {
                Push(slot, mgr, 2, "confused");
            } else if (body.consciousness < 72f) {
                Push(slot, mgr, 1, "confused");
            } else if (body.consciousness < 90f) {
                Push(slot, mgr, 0, "confused");
            }

            if (body.overdoseIndex > 0) {
                Push(slot, mgr, 3, "drugoverdose", chippedOnly: true);
            }
            if (body.internalBleeding > 5f) {
                Push(slot, mgr, (int)((body.internalBleeding - 1f) / 25f), "internalBleed", chippedOnly: true);
            }

            if (body.totalBleedSpeed > 0.3f) {
                Push(slot, mgr, 3, "maxbleeding");
            } else if (body.totalBleedSpeed > 0.15f) {
                Push(slot, mgr, 2, "heavybleeding");
            } else if (body.totalBleedSpeed > 0.06f) {
                Push(slot, mgr, 1, "bleeding");
            } else if (body.totalBleedSpeed > 0f) {
                Push(slot, mgr, 0, "smallbleeding");
            }

            if (body.stamina <= 15f) {
                Push(slot, mgr, 3, "exertion");
            } else if (body.stamina < 35f) {
                Push(slot, mgr, 2, "exertion");
            } else if (body.stamina < 50f) {
                Push(slot, mgr, 1, "exertion");
            } else if (body.stamina < 70f) {
                Push(slot, mgr, 0, "exertion");
            }

            float maxInfection = 0f;
            float maxBoneTimer = 0f;
            float maxDislocationTimer = 0f;
            if (body.limbs != null) {
                foreach (Limb limb in body.limbs) {
                    if (limb != null && !limb.dismembered) {
                        if (limb.infectionAmount > maxInfection) {
                            maxInfection = limb.infectionAmount;
                        }
                        if (limb.broken && limb.boneHealTimer > maxBoneTimer) {
                            maxBoneTimer = limb.boneHealTimer;
                        }
                        if (limb.dislocated && limb.dislocationTimer > maxDislocationTimer) {
                            maxDislocationTimer = limb.dislocationTimer;
                        }
                    }
                }
            }

            if (maxBoneTimer > 0f) {
                Push(slot, mgr, Mathf.RoundToInt(maxBoneTimer * 0.03f), "brokenbone", chippedOnly: true);
            }
            if (maxDislocationTimer > 0f) {
                Push(slot, mgr, Mathf.RoundToInt(maxDislocationTimer * 0.03f), "dislocation", chippedOnly: true);
            }
            if (headLimb != null && body.limbs.Length > 1 && headLimb.broken) {
                Push(slot, mgr, Mathf.RoundToInt(headLimb.boneHealTimer * 0.03f), "brokenneck", chippedOnly: true);
            }
            if (torsoLimb != null && torsoLimb.broken) {
                Push(slot, mgr, Mathf.RoundToInt(torsoLimb.boneHealTimer * 0.03f), "brokenribs", chippedOnly: true);
            }
            if (headLimb != null && body.limbs.Length > 1 && headLimb.dislocated) {
                Push(slot, mgr, Mathf.RoundToInt(headLimb.dislocationTimer * 0.03f), "dislocatedjaw", chippedOnly: true);
            }
            if (torsoLimb != null && torsoLimb.dislocated) {
                Push(slot, mgr, Mathf.RoundToInt(torsoLimb.dislocationTimer * 0.03f), "dislocatedspine", chippedOnly: true);
            }

            if (maxInfection > 80f) {
                Push(slot, mgr, 3, "infected");
            } else if (maxInfection > 60f) {
                Push(slot, mgr, 2, "infected");
            } else if (maxInfection > 40f) {
                Push(slot, mgr, 1, "infected");
            } else if (maxInfection > 25f) {
                Push(slot, mgr, 0, "infected", chippedOnly: true);
            }

            if (body.septicShock > 80f) {
                Push(slot, mgr, 3, "sepsis", chippedOnly: true);
            } else if (body.septicShock > 50f) {
                Push(slot, mgr, 2, "sepsis", chippedOnly: true);
            } else if (body.septicShock > 10f) {
                Push(slot, mgr, 1, "sepsis", chippedOnly: true);
            }

            if (body.venomCurrent > 90f) {
                Push(slot, mgr, 3, "venom");
            } else if (body.venomCurrent > 55f) {
                Push(slot, mgr, 2, "venom");
            } else if (body.venomCurrent > 25f) {
                Push(slot, mgr, 1, "venom");
            } else if (body.venomCurrent > 2f) {
                Push(slot, mgr, 0, "venom");
            }

            if (body.energy <= 7f) {
                Push(slot, mgr, 3, "tired");
            } else if (body.energy < 15f) {
                Push(slot, mgr, 2, "tired");
            } else if (body.energy < 25f) {
                Push(slot, mgr, 1, "tired");
            } else if (body.energy < 35f) {
                Push(slot, mgr, 0, "tired");
            }

            float hungerMult = Mathf.Clamp01(1f + body.happiness * 0.009f);
            if (body.hunger <= 15f * hungerMult) {
                Push(slot, mgr, 3, "hunger");
            } else if (body.hunger < 35f * hungerMult) {
                Push(slot, mgr, 2, "hunger");
            } else if (body.hunger < 50f * hungerMult) {
                Push(slot, mgr, 1, "hunger");
            } else if (body.hunger < 75f * hungerMult) {
                Push(slot, mgr, 0, "hunger");
            }
            if (body.hunger > 120f) {
                Push(slot, mgr, 5, "hunger");
            } else if (body.hunger > 100f) {
                Push(slot, mgr, 4, "hunger");
            }

            if (body.thirst <= 20f) {
                Push(slot, mgr, 3, "thirst");
            } else if (body.thirst < 35f) {
                Push(slot, mgr, 2, "thirst");
            } else if (body.thirst < 55f) {
                Push(slot, mgr, 1, "thirst");
            } else if (body.thirst < 75f) {
                Push(slot, mgr, 0, "thirst");
            }
            if (body.thirst > 175f) {
                Push(slot, mgr, 3, "overhydrated");
            } else if (body.thirst > 125f) {
                Push(slot, mgr, 1, "overhydrated");
            } else if (body.thirst > 100f) {
                Push(slot, mgr, 0, "overhydrated");
            }

            if (body.sicknessAmount > 75f) {
                Push(slot, mgr, 3, "sick");
            } else if (body.sicknessAmount > 50f) {
                Push(slot, mgr, 2, "sick");
            } else if (body.sicknessAmount > 30f) {
                Push(slot, mgr, 1, "sick");
            } else if (body.sicknessAmount > 10f) {
                Push(slot, mgr, 0, "sick");
            }

            if (body.temperature > 41.5f) {
                Push(slot, mgr, 3, "hot");
            } else if (body.temperature > 40.25f) {
                Push(slot, mgr, 2, "hot");
            } else if (body.temperature > 39f) {
                Push(slot, mgr, 1, "hot");
            } else if (body.temperature > 38f) {
                Push(slot, mgr, 0, "hot");
            }

            if (body.temperature < 28f) {
                Push(slot, mgr, 3, "cold");
            } else if (body.temperature < 32.5f) {
                Push(slot, mgr, 2, "cold");
            } else if (body.temperature < 34f) {
                Push(slot, mgr, 1, "cold");
            } else if (body.temperature < 35.5f) {
                Push(slot, mgr, 0, "cold");
            }

            if (body.totalHappiness <= -75f) {
                Push(slot, mgr, 3, "miserable");
            } else if (body.totalHappiness < -50f) {
                Push(slot, mgr, 2, "depression");
            } else if (body.totalHappiness < -30f) {
                Push(slot, mgr, 1, "gloomy");
            } else if (body.totalHappiness < -10f) {
                Push(slot, mgr, 0, "sad");
            }

            if (body.totalHappiness >= 75f) {
                Push(slot, mgr, 7, "happy");
            } else if (body.totalHappiness > 50f) {
                Push(slot, mgr, 6, "happy");
            } else if (body.totalHappiness > 30f) {
                Push(slot, mgr, 5, "happy");
            } else if (body.totalHappiness > 10f) {
                Push(slot, mgr, 4, "happy");
            }

            if (body.clawHealth < 25f) {
                Push(slot, mgr, 1, "clawdamage");
            } else if (body.clawHealth < 60f) {
                Push(slot, mgr, 0, "clawdamage");
            }

            if (body.hearingLoss > 70f) {
                Push(slot, mgr, 2, "hearingloss");
            } else if (body.hearingLoss > 40f) {
                Push(slot, mgr, 1, "hearingloss");
            } else if (body.hearingLoss > 15f) {
                Push(slot, mgr, 0, "hearingloss");
            }

            if (body.dirtyness > 80f) {
                Push(slot, mgr, 1, "dirty");
            } else if (body.dirtyness > 50f) {
                Push(slot, mgr, 0, "dirty");
            }

            if (body.overEncumberance > 0.85f) {
                Push(slot, mgr, 3, "encumbered");
            } else if (body.overEncumberance > 0.55f) {
                Push(slot, mgr, 2, "encumbered");
            } else if (body.overEncumberance > 0.3f) {
                Push(slot, mgr, 1, "encumbered");
            } else if (body.overEncumberance > 0f) {
                Push(slot, mgr, 0, "encumbered");
            }

            if (body.wetness > 70f) {
                Push(slot, mgr, 3, "wet");
            } else if (body.wetness > 45f) {
                Push(slot, mgr, 2, "wet");
            } else if (body.wetness > 20f) {
                Push(slot, mgr, 1, "wet");
            } else if (body.wetness > 5f) {
                Push(slot, mgr, 0, "wet");
            }
        }

        private static void Push(Slot slot, MoodleManager mgr, int intensity, string icon, bool chippedOnly = false) {
            if (slot.MoodleCount >= 10 || (chippedOnly && !slot.ChipReadable) || !mgr.icons.TryGetValue(icon, out Sprite sprite) || sprite == null) {
                return;
            }
            Sprite back = null;
            Sprite[] backgroundIcons = mgr.backgroundIcons;
            if (backgroundIcons != null && backgroundIcons.Length != 0) {
                intensity = Mathf.Clamp(intensity, 0, backgroundIcons.Length - 1);
                back = backgroundIcons[intensity];
            }
            int idx = slot.MoodleCount;
            slot.MoodleBack[idx] = back;
            slot.MoodleIcon[idx] = sprite;
            slot.MoodleCount = idx + 1;
        }

        private static string FormatLiters(float value) {
            float rounded = Mathf.Round(value * 10f) / 10f;
            float whole = Mathf.Round(rounded);
            return Mathf.Abs(rounded - whole) < 0.001f ? whole.ToString("0") : rounded.ToString("0.0");
        }

        public static string CurrentLine(Talker talker) {
            if (!_lineReady) {
                _lineReady = true;
                _lineField = typeof(Talker).GetField("currentString", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            }
            if (_lineField == null || talker == null) {
                return null;
            }
            return _lineField.GetValue(talker) as string;
        }

        private static Color NameColor(ScavPlayer plr) {
            if (!_colorReady) {
                _colorReady = true;
                _colorField = typeof(ScavPlayer).GetField("playerColor", BindingFlags.Instance | BindingFlags.Public)
                    ?? typeof(ScavPlayer).GetField("plrcolor", BindingFlags.Instance | BindingFlags.Public);
            }
            if (_colorField == null || plr == null) {
                return Color.white;
            }
            object value = _colorField.GetValue(plr);
            if (value is Color24 c24) {
                Color result = c24;
                result.a = 1f;
                return result;
            }
            if (value is Color c) {
                c.a = 1f;
                return c;
            }
            return Color.white;
        }

        private static string Cap(string line) {
            int length = line.Length;
            int take = 0;
            for (int i = 0; i < length; i++) {
                if (take >= 30 || line[i] == '\n' || line[i] == '\r') {
                    break;
                }
                take++;
            }
            return take == length ? line : line.Substring(0, take);
        }
    }
}
