using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 队列式滑动选择提示框系统（喂食/吸血同意确认、投票弹窗等），支持防误触悬停减速滑入。
/// </summary>
public class UIChoicePrompt : CasualtiesTogetherSingleton {
    public static float ScreenHeight => Screen.height;
    public static float ScreenWidth => Screen.width;

    public static UIChoicePrompt instance { get; private set; }

    public UIChoicePrompt() {
        instance = this;
    }

    public static void ClientDoNetPrompt(uint key, string promptid, string title, string text) {
        Lang.MsgTryParse(ref title);
        Lang.MsgTryParse(ref text);

        // 沙丘鼠吸血请求：若本地角色已昏迷或入睡无法主动确认，直接自动回发同意以契合生理状态
        if (promptid == "DUNE_BITE") {
            Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
            if (local == null && ScavPlayer.LocalPlayer != null) {
                local = ScavPlayer.LocalPlayer.body;
            }
            if (local != null && NetBody.DuneBiteSessionCannotConsent(local)) {
                NetDataWriter auto = Net.CreateWriter(NetmsgId.SERVER_CustomPromptAnswer);
                auto.Put(key);
                auto.Put(promptid);
                auto.Put((byte)0);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, auto);
                return;
            }
        }

        string yes = promptid == "DUNE_BITE" ? Lang.Get("yes") : Lang.Get("accept");
        string no = promptid == "DUNE_BITE" ? Lang.Get("no") : Lang.Get("decline");

        ShowPrompt(new Prompt {
            text = text,
            name = title,
            timeout = 12,
            pos = new Vector2(0.5f, 0.98f),
            size = new Vector2(0.43f, 0.18f),
            choices = new[] {
                new Prompt.PromptChoice {
                    text = yes,
                    action = () => {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_CustomPromptAnswer);
                        writer.Put(key);
                        writer.Put(promptid);
                        writer.Put((byte)0);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
                new Prompt.PromptChoice {
                    text = no,
                    action = () => {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_CustomPromptAnswer);
                        writer.Put(key);
                        writer.Put(promptid);
                        writer.Put((byte)1);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
            },
        });
    }

    public class Prompt {
        public class PromptChoice {
            public Dictionary<string, object> data;
            public string text;
            public Action action;
        }

        public readonly Dictionary<string, object> data = new();
        public string name;
        public string text;
        public float timeout = 5;
        public Texture2D texture;
        public Vector2 pos = new Vector2(0.2f, 0f);
        public Vector2 size = new Vector2(0.35f, 0.16f);
        public bool immidiate;
        public int defaultchoice = -1;
        public bool defaultcanignore;
        public byte chosenchoice;
        public bool chosenignore;
        public PromptChoice[] choices;

        public void CalculateRectTransform(RectTransform rt, bool use_progress) {
            Vector2 realPromptSize = size * UIManager.ReferenceResolution;
            Vector2 invertedPos = new Vector2(pos.x, 1f - pos.y);

            rt.pivot = invertedPos;
            Vector2 realPos = invertedPos;
            if (use_progress) {
                if (invertedPos.y < 0.5f) {
                    rt.pivot = Vector2.LerpUnclamped(new Vector2(rt.pivot.x, 1f), rt.pivot, prompt_show_progress);
                    realPos = Vector2.LerpUnclamped(new Vector2(realPos.x, 0f), realPos, prompt_show_progress);
                } else {
                    rt.pivot = Vector2.LerpUnclamped(new Vector2(rt.pivot.x, 0f), rt.pivot, prompt_show_progress);
                    realPos = Vector2.LerpUnclamped(new Vector2(realPos.x, 1f), realPos, prompt_show_progress);
                }
            }

            rt.anchorMin = realPos;
            rt.anchorMax = realPos;
            rt.SetSizeAndPos(realPromptSize, Vector2.one * 0.5f);
        }

        public Rect CalculateRect(bool use_progress) {
            float shortSide = Mathf.Min(ScreenHeight, ScreenWidth);
            float areaWidth = shortSide * size.x;
            float areaHeight = shortSide * size.y;

            float realPosX = (ScreenWidth - areaWidth) * pos.x;
            float realPosY = (ScreenHeight - areaHeight) * pos.y;
            if (use_progress) {
                realPosY = pos.y < 0.5f
                    ? Mathf.Lerp(-areaHeight, realPosY, prompt_show_progress)
                    : Mathf.Lerp(ScreenHeight, realPosY, prompt_show_progress);
            }

            return new Rect(realPosX, realPosY, areaWidth, areaHeight);
        }
    }

    private readonly Queue<Prompt> prompt_queue = new();
    public static float prompt_show_progress;
    public static bool mouse_is_near_area;

    public static void ShowPrompt(Prompt prompt) {
        if (instance != null && prompt != null) {
            instance.prompt_queue.Enqueue(prompt);
        }
    }

    private void Update() {
        mouse_is_near_area = false;
        if (prompt_queue.Count > 0) {
            Prompt firstPrompt = prompt_queue.Peek();
            if (firstPrompt.timeout <= 0) {
                prompt_show_progress = Mathf.MoveTowards(prompt_show_progress, 0, Time.deltaTime * 4f);
                if (prompt_show_progress == 0) {
                    prompt_queue.Dequeue();
                }
            } else if (prompt_show_progress < 1) {
                if (firstPrompt.immidiate) {
                    prompt_show_progress = 1;
                } else {
                    Rect guardRect = firstPrompt.CalculateRect(false).ShrinkBorder(-20 * UIMainMenu.GetMenuUIScale());
                    mouse_is_near_area = UIBullshit.IsCursorInGUIRect(guardRect);

                    float speed = (!mouse_is_near_area || prompt_show_progress == 0) ? 3f : 0.2f;
                    prompt_show_progress = Mathf.MoveTowards(prompt_show_progress, 1, Time.deltaTime * speed);

                    if (prompt_show_progress >= 1) {
                        Util.PlayUISound(PlayerCamera.UISoundType.Click);
                    }
                }
            } else if (firstPrompt.timeout > 0) {
                firstPrompt.timeout -= Time.deltaTime;
                if (firstPrompt.timeout <= 0) {
                    if (firstPrompt.defaultcanignore) {
                        firstPrompt.chosenignore = true;
                    } else {
                        PromptUse(firstPrompt, firstPrompt.defaultchoice);
                    }
                    Util.PlayUISound(PlayerCamera.UISoundType.Close);
                }
            }
        } else {
            prompt_show_progress = 0;
        }
    }

    public static void PromptUse(Prompt prompt, int choice) {
        byte ch = (byte)Mathf.Max(0, choice);
        if (prompt.choices != null && prompt.choices.Length > 0) {
            if (ch >= prompt.choices.Length) {
                ch = (byte)(prompt.choices.Length - 1);
            }
            SafeActionInvoke(prompt.choices[ch].action);
        }

        prompt.chosenchoice = ch;
        prompt.timeout = 0;
    }

    public static void SafeActionInvoke(Action action) {
        if (action == null) {
            return;
        }
        try {
            action.Invoke();
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "UIChoicePrompt.SafeActionInvoke", ex);
        }
    }

    public static IEnumerator DoPromptCoroutine(Prompt prompt) {
        ShowPrompt(prompt);
        while (instance != null && instance.prompt_queue.Contains(prompt)) {
            yield return null;
        }
    }

    public static Task DoPromptAsync(Prompt prompt) {
        return Util.StartCoroutineAsync(DoPromptCoroutine(prompt));
    }

    internal void _GUI_RenderUIChoicePrompt() {
        FixChoicePrompt.Begin();
        try {
            if (prompt_show_progress == 0 || prompt_queue.Count == 0) {
                return;
            }

            Color ogColor = GUI.color;
            bool ogWrap = GUI.skin.label.wordWrap;
            bool ogRich = GUI.skin.label.richText;
            TextAnchor ogAlignment = GUI.skin.label.alignment;

            Prompt firstPrompt = prompt_queue.Peek();
            if (prompt_show_progress < 1 && mouse_is_near_area) {
                float pulse = Mathf.Sin(Time.realtimeSinceStartup * 6f);
                GUI.color = new Color(pulse, pulse, pulse, 0.7f + pulse * 0.3f);
                Rect shadowRect = firstPrompt.CalculateRect(false);
                GUI.Label(shadowRect, "", GUI.skin.box);

                GUI.color = ogColor;
                GUI.skin.label.alignment = TextAnchor.LowerCenter;
                GUI.Label(shadowRect, Lang.Get("movemouse"));
                UIBullshit._GUI_SetTooltip(Lang.Get("movemouse"), "");
            }

            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            Rect rect = firstPrompt.CalculateRect(true).ShrinkBorder(3 * UIMainMenu.GetMenuUIScale());
            UIBullshit.CheckCursorOverlap(rect);

            GUILayout.BeginArea(rect, "", GUI.skin.box);
            try {
                GUI.skin.label.richText = true;
                UIBullshit._GUI_BiggerLabel(firstPrompt.name, 1.4f);

                GUI.skin.label.wordWrap = true;
                GUILayout.Label(firstPrompt.text);
                GUI.skin.label.wordWrap = ogWrap;

                if (firstPrompt.timeout > 0 && firstPrompt.choices != null) {
                    GUILayout.BeginVertical();
                    GUILayout.FlexibleSpace();
                    GUILayout.EndVertical();

                    bool ogEnabled = GUI.enabled;
                    if (prompt_show_progress < 1 && mouse_is_near_area) {
                        GUI.enabled = false;
                        float pulse = Mathf.Sin(Time.realtimeSinceStartup * 6f);
                        GUI.color = new Color(1, 1, 1, 0.2f + pulse * 0.15f);
                    }

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    for (int i = 0; i < firstPrompt.choices.Length; i++) {
                        Prompt.PromptChoice item = firstPrompt.choices[i];
                        if (GUILayout.Button(item.text)) {
                            PromptUse(firstPrompt, i);
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                        GUILayout.FlexibleSpace();
                    }
                    GUILayout.EndHorizontal();

                    GUI.enabled = ogEnabled;
                    GUI.color = ogColor;
                }
            } finally {
                GUILayout.EndArea();
                GUI.skin.label.alignment = ogAlignment;
                GUI.skin.label.richText = ogRich;
                GUI.skin.label.wordWrap = ogWrap;
                GUI.color = ogColor;
            }
        } finally {
            FixChoicePrompt.End();
        }
    }
}

/// <summary>
/// 多人房间踢人 / 语音禁言 / 文字禁言投票管理单例。
/// </summary>
public class VoteSystem : CasualtiesTogetherSingleton {
    public delegate void VoteEndAction(HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore);

    private static ushort _voteCounter;

    public class Server_VoteState {
        public ushort id;
        public float time;
        public VoteEndAction endAction;
        public HashSet<ScavPlayer> voters;
        public HashSet<ScavPlayer> votedPositive = new();
        public HashSet<ScavPlayer> votedNegative = new();
    }

    public static Server_VoteState Server_ActiveVote;

    private void Start() {
        ServerMain.ServerCustomCommandsDict["vote"] = (plr, cmd, argv) => {
            // 严格校验指令参数个数与当前是否存在进行中的投票，防止越界或重复发起抛异常
            if (argv == null || argv.Length < 3 || Server_ActiveVote != null) {
                return;
            }

            Lang.MessageArgument callerName = Lang.NestedMsg(Lang.MarkMsgAsLocaleKey("vote_caller_server"));
            if (plr != null) {
                callerName = plr.playerName;
            }
            string actionType = argv[1];
            string targetName = argv[2];

            if (actionType == "kick") {
                Con.PerformActionOnPlayersByName(targetName, oplr => {
                    if (!oplr.IsLocal && Server_ActiveVote == null) {
                        Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_kick_announce", callerName, oplr.playerName));
                        Server_VoteKick(oplr, plr);
                    }
                }, false, plr, false, true);
            } else if (actionType == "mutevc") {
                Con.PerformActionOnPlayersByName(targetName, oplr => {
                    if (!oplr.IsLocal && Server_ActiveVote == null) {
                        Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_mute_vc_announce", callerName, oplr.playerName));
                        Server_VoteMuteVC(oplr, plr);
                    }
                }, false, plr, false, true);
            } else if (actionType == "mutetc") {
                Con.PerformActionOnPlayersByName(targetName, oplr => {
                    if (!oplr.IsLocal && Server_ActiveVote == null) {
                        Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_mute_tc_announce", callerName, oplr.playerName));
                        Server_VoteMuteTC(oplr, plr);
                    }
                }, false, plr, false, true);
            }
        };
    }

    public static void Server_VoteKick(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        string txt = Lang.FormatMsg("vote_kick_body", plr.playerName);
        Server_AnnounceVote(Lang.MarkMsgAsLocaleKey("vote_kick_title"), txt, 10, (votedYes, votedNo, votedIgnore) => {
            votedIgnore.Remove(plr);
            float total = votedYes.Count + votedNo.Count + votedIgnore.Count;
            if (total > 0) {
                float perc = votedYes.Count / total;
                string percentStr = Mathf.RoundToInt(perc * 100f).ToString();
                Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_kick_result", percentStr));
                if (perc > 0.5f && plr != null) {
                    plr.Server_Kick(Lang.FormatMsg("vote_kick_kicked_reason", percentStr));
                }
            }
        });
    }

    public static void Server_VoteMuteVC(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        string txt = Lang.FormatMsg("vote_mute_vc_body", plr.playerName);
        Server_AnnounceVote(Lang.MarkMsgAsLocaleKey("vote_mute_vc_title"), txt, 10, (votedYes, votedNo, votedIgnore) => {
            votedIgnore.Remove(plr);
            float total = votedYes.Count + votedNo.Count + votedIgnore.Count;
            if (total > 0) {
                float perc = votedYes.Count / total;
                Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_mute_vc_result", Mathf.RoundToInt(perc * 100f).ToString()));
                if (perc > 0.5f && plr != null) {
                    plr.server_mute_vc = true;
                }
            }
        });
    }

    public static void Server_VoteMuteTC(ScavPlayer plr, ScavPlayer who_called_the_vote = null) {
        string txt = Lang.FormatMsg("vote_mute_tc_body", plr.playerName);
        Server_AnnounceVote(Lang.MarkMsgAsLocaleKey("vote_mute_tc_title"), txt, 10, (votedYes, votedNo, votedIgnore) => {
            votedIgnore.Remove(plr);
            float total = votedYes.Count + votedNo.Count + votedIgnore.Count;
            if (total > 0) {
                float perc = votedYes.Count / total;
                Chat.Server_ChatAnnouncement(Lang.FormatMsg("vote_mute_tc_result", Mathf.RoundToInt(perc * 100f).ToString()));
                if (perc > 0.5f && plr != null) {
                    plr.server_mute_tc = true;
                }
            }
        });
    }

    public static void Server_AnnounceVote(in string title, in string message, in float timetovote, in VoteEndAction action, IReadOnlyList<ScavPlayer> voters = null) {
        if (Net.IsClient) {
            throw new InvalidOperationException("Client can't start server votes.");
        }
        if (Server_ActiveVote != null) {
            throw new InvalidOperationException("Already voting");
        }

        voters ??= ScavPlayer.AllPlayers.ToList();
        if (voters.Count > 0) {
            Server_ActiveVote = new Server_VoteState {
                id = _voteCounter++,
                time = timetovote + 1,
                endAction = action,
                voters = new HashSet<ScavPlayer>(voters),
            };

            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceVote);
            writer.Put(Server_ActiveVote.id);
            writer.Put(title);
            writer.Put(message);
            writer.Put(timetovote);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, voters);
        } else {
            action.Invoke(null, null, null);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_AnnounceVote, false)]
    private static void ClientReceiver__AnnounceVote(knetid _, NetDataReader reader) {
        ushort voteId = reader.GetUShort();
        string title = reader.GetString();
        string message = reader.GetString();
        float timeToVote = reader.GetFloat();

        Lang.MsgTryParse(ref title);
        Lang.MsgTryParse(ref message);

        UIChoicePrompt.ShowPrompt(new UIChoicePrompt.Prompt {
            text = message,
            name = title,
            timeout = timeToVote,
            defaultcanignore = true,
            choices = new[] {
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("yes"),
                    action = () => {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_Vote);
                        writer.Put(voteId);
                        writer.Put((byte)0);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
                new UIChoicePrompt.Prompt.PromptChoice {
                    text = Lang.Get("no"),
                    action = () => {
                        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_Vote);
                        writer.Put(voteId);
                        writer.Put((byte)1);
                        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                    }
                },
            },
        });
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Vote)]
    private static void ServerReceiver__Vote(knetid clientId, NetDataReader reader) {
        ushort voteId = reader.GetUShort();
        byte voteNumber = reader.GetByte();
        if (Server_ActiveVote == null || Server_ActiveVote.id != voteId) {
            return;
        }
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)
            && Server_ActiveVote.voters.Contains(plr)
            && !Server_ActiveVote.votedPositive.Contains(plr)
            && !Server_ActiveVote.votedNegative.Contains(plr)) {
            if (voteNumber == 0) {
                Server_ActiveVote.votedPositive.Add(plr);
            } else {
                Server_ActiveVote.votedNegative.Add(plr);
            }
        }
    }

    private void Update() {
        if (!Net.IsServer || Server_ActiveVote == null) {
            return;
        }

        Server_ActiveVote.time -= Time.deltaTime;

        Server_ActiveVote.voters.RemoveWhere(x => x == null);
        Server_ActiveVote.votedPositive.RemoveWhere(x => x == null);
        Server_ActiveVote.votedNegative.RemoveWhere(x => x == null);

        HashSet<ScavPlayer> ignorers = new HashSet<ScavPlayer>(Server_ActiveVote.voters);
        ignorers.ExceptWith(Server_ActiveVote.votedPositive);
        ignorers.ExceptWith(Server_ActiveVote.votedNegative);

        if (Server_ActiveVote.time < 0 || ignorers.Count == 0) {
            try {
                Server_ActiveVote.endAction?.Invoke(Server_ActiveVote.votedPositive, Server_ActiveVote.votedNegative, ignorers);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "VoteSystem.EndVote", ex);
            }
            Server_ActiveVote = null;
        }
    }
}
