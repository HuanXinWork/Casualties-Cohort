using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Linq;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机文字聊天框主组件（负责输入焦点管理、伤残语音失真实时预览与 IMGUI 历史滚动渲染）。
/// </summary>
public partial class Chat : CasualtiesTogetherSingleton {
    public static event Action<ScavPlayer, string> OnPlayerChatMessage;

    [SettingDeclarerThingyFloat(0, 1, "setting_chatposx")]
    public static float SETTING_posX = 1f;

    [SettingDeclarerThingyFloat(0, 1, "setting_chatposy")]
    public static float SETTING_posY = 1f;

    [SettingDeclarerThingyFloat(0, 3, "setting_chatsizex")]
    public static float SETTING_sizeX = 1f;

    [SettingDeclarerThingyFloat(0, 3, "setting_chatsizey")]
    public static float SETTING_sizeY = 1f;

    [SettingDeclarerThingyFloat(0, 2, "setting_chatscale")]
    public static float SETTING_scale = 1f;

    [SettingDeclarerThingyBool("setting_chatpreview")]
    public static bool _DEV_ENABLE_PREVIEW = true;

    [SettingDeclarerThingyBool("setting_chatpreviewonside")]
    public static bool _DEV_PREVIEW_ON_SIDE = false;

    public static bool ALLOW_ME = false;

    public static class DefaultChatColors {
        public static Color24 Server = Color24.FromHex("#FFC90E");
        public static Color24 Kill = Color24.FromHex("#F15058");
    }

    public static string AddColorTags(Color24 color, string text) {
        return $"<color={color.ToHex()}>{text}</color>";
    }

    public static bool _DEV_LOG_CHAT = false;
    public static bool SHOULD_LOG_CHAT => _DEV_LOG_CHAT || log.verbose || Con._DEV_CHATSPY;

    public static float DEV_CHAT_FULL_WIDTH = 500f;
    public static float DEV_CHAT_HISTORY_HEIGHT = 250f;
    public static float DEV_CHAT_TEXTFIELD_HEIGHT = 40f;

    const string CHAT_TEXTFIELD_CONTROLNAME = "ChatField";

    static bool scrollbar_is_being_interacted = false;
    static Rect? scrollbar_area = null;
    static bool mouse_is_in_chat_area = false;
    static double focustime = 0.0;

    static bool _chatinput_changed = false;
    static float _chatinput_timer = 0f;
    static string _preview_distorted_message = "";

    static Vector2 _GUI_SizeOfSpace = Vector2.one;
    public static int _GUI_RealChatFontsize => Mathf.RoundToInt(UIBullshit.GetCurFontSize() * 0.82f * SETTING_scale);

    internal static MaxCapacityQueue<string> MyMessageLog = new(20);
    internal static int MyMessageLog_Index = 0;

    internal const int MAX_MESSAGES_HISTORY_COUNT = 100;
    private static MaxCapacityQueue<ChatMsgContainer> CHAT_LOG = new(MAX_MESSAGES_HISTORY_COUNT);
    private static string CHAT_current_input = "";
    private static float CHATScrollPosition;
    public static bool CHAT_textbox_input_focused = false;
    public static bool CHATforceinputfocus = false;

    public const string NAMELABEL_LOCALSYSTEM = "*SYSTEM*";
    public const string NAMELABEL_SERVER = "*SERVER*";
    const ushort MAX_MESSAGE_LENGTH = 150;

    public static void ShowChat() {
        focustime = Time.realtimeSinceStartupAsDouble;
    }

    void Start() {
        ShowChat();
    }

    void Update() {
        if (!Net.IsRunning) {
            CHATforceinputfocus = false;
            CHAT_textbox_input_focused = false;
            return;
        }

        if (scrollbar_area != null && Input.GetKeyDown(KeyCode.Mouse0) && UIBullshit.IsCursorInGUIRect((Rect)scrollbar_area)) {
            scrollbar_is_being_interacted = true;
        }
        if (Input.GetKeyUp(KeyCode.Mouse0)) {
            scrollbar_is_being_interacted = false;
        }

        if (Con.IsConsoleOpen()) {
            CHATforceinputfocus = false;
            CHAT_textbox_input_focused = false;
        } else if (!CHAT_textbox_input_focused) {
            _preview_distorted_message = "";
            if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.chat))) {
                if (PlayerCamera.main && ConsoleScript.instance.active) {
                    return;
                }
                focustime = Time.realtimeSinceStartupAsDouble;
                UpdateMessageDistortPreview();
                CHAT_textbox_input_focused = true;
                CHATforceinputfocus = true;
                _chatinput_timer = 2f;
            }
            return;
        }

        if (Util.IsLocalBodyAlive()
            && CHAT_textbox_input_focused
            && Multiplayer.rules.SpeechImpairedChat
            && !string.IsNullOrWhiteSpace(CHAT_current_input)) {
            _chatinput_timer += Time.deltaTime;
            if (_chatinput_changed || _chatinput_timer > 1f) {
                UpdateMessageDistortPreview();
            }
        }
    }

    public static void UpdateMessageDistortPreview() {
        _chatinput_timer = 0f;
        _chatinput_changed = false;

        if (Util.TryGetLocalBody(out Body locbody)) {
            _preview_distorted_message = locbody.talker.DistortString(CHAT_current_input);
            if (locbody.TryGetComponent(out NetBody nb)) {
                string dummyTag = "test";
                nb.HearinglossDistortMessage(ScavPlayer.LocalPlayer.netBody, ref _preview_distorted_message, ref dummyTag);
            }
        } else {
            _preview_distorted_message = "";
        }
    }

    public static bool MouseIsInteractingWithScrollbar() {
        return scrollbar_area != null && scrollbar_is_being_interacted;
    }

    static float ChatIconStrip(ScavPlayer plr) {
        if (plr == null) return 0f;
        float step = _GUI_RealChatFontsize + (int)(8f * UIBullshit.uiScale);
        float w = 0f;
        foreach (var _ in plr.KnownUserTagIcons) {
            w += step;
        }
        if (plr.IconAnySmallToLarge != null) {
            w += step;
        }
        return w;
    }

    static void MeasureChatLine(ChatMsgContainer text, float columnWidth) {
        text.namesize = GUI.skin.label.CalcSize(FixGuiContent.Content(text.result_nametag, 9));
        float icons = ChatIconStrip(text.plr);
        float drawColumn = Mathf.Max(1f, columnWidth - icons);
        float nametag = text.namesize.x + 1f + icons;
        int spaces = Mathf.Max(0, Mathf.CeilToInt((nametag - drawColumn * 0.1f) / _GUI_SizeOfSpace.x));
        text.msg2 = new string(' ', spaces) + text.msg;
        text.height = GUI.skin.label.CalcHeight(FixGuiContent.Content(text.msg2, 10), drawColumn * 0.91f);
    }

    static float MeasureHistory(float columnWidth) {
        float total = 0f;
        foreach (ChatMsgContainer text in CHAT_LOG.JustGiveTheQueue()) {
            MeasureChatLine(text, columnWidth);
            total += text.height;
        }
        return total;
    }

    static void _GUI_ChatDrawMessage(ChatMsgContainer textinfo, float real_pos_X, ref float this_y, float chathistory_area_width, float texthiehgt) {
        float space_used = 0f;
        chathistory_area_width -= ChatIconStrip(textinfo.plr);

        if (textinfo.plr != null) {
            int pic_size_on_axis = _GUI_RealChatFontsize;
            var rect = new Rect(real_pos_X + space_used, this_y + 1f, pic_size_on_axis, pic_size_on_axis);
            int spacing_gap = (int)(8f * UIBullshit.uiScale);
            float full_tag_size_x = pic_size_on_axis + spacing_gap;

            foreach (var tagimage in textinfo.plr.KnownUserTagIcons) {
                rect.x = real_pos_X + space_used;
                GUI.DrawTexture(rect, tagimage.texture);
                space_used += full_tag_size_x;
            }

            Texture2D profilepic = textinfo.plr.IconAnySmallToLarge;
            if (profilepic != null) {
                rect.x = real_pos_X + space_used;
                GUI.DrawTexture(rect, profilepic);
                space_used += full_tag_size_x;
            }
        }

        GUI.skin.label.richText = true;
        GUI.Label(new Rect(real_pos_X + space_used, this_y, textinfo.namesize.x, textinfo.namesize.y), textinfo.result_nametag);
        space_used += textinfo.namesize.x;

        GUI.skin.label.richText = textinfo.rich;
        GUI.skin.label.wordWrap = true;
        GUI.Label(new Rect(real_pos_X + chathistory_area_width * 0.1f, this_y, chathistory_area_width * 0.91f, texthiehgt + textinfo.namesize.y * 2f), textinfo.msg2);
    }

    internal static bool WantsGui() {
        bool forceSettings = !Net.IsRunning && UIMainMenu.IsInSettings() && UIMainMenu.cur_settings_tab == UIMainMenu._chat_setting_tab;
        if ((!forceSettings && (!Net.IsRunning || Con._DEV_HIDEHUD))
            || SETTING_scale <= 0f || SETTING_sizeY <= 0f || SETTING_sizeX <= 0f) {
            return false;
        }

        if (CHATforceinputfocus || CHAT_textbox_input_focused) return true;
        if (Time.realtimeSinceStartupAsDouble - focustime < 5.0) return true;

        float finalScale = SETTING_scale * UIBullshit.uiScale;
        float areaWidth = DEV_CHAT_FULL_WIDTH * finalScale * SETTING_sizeX;
        float areaHeight = Mathf.Max(0f, DEV_CHAT_HISTORY_HEIGHT * finalScale * SETTING_sizeY);
        float textHeight = DEV_CHAT_TEXTFIELD_HEIGHT * finalScale;
        float x = (Screen.width - areaWidth) * SETTING_posX;
        float y = (Screen.height - areaHeight - textHeight) * SETTING_posY;
        var window = new Rect(x, y, areaWidth, areaHeight + textHeight);
        window.xMin -= 50f;
        window.xMax += 50f;
        window.yMin -= 50f;
        window.yMax += 50f;
        mouse_is_in_chat_area = UIBullshit.IsCursorInGUIRect(window);
        return mouse_is_in_chat_area;
    }

    internal static void _GUI_DrawChat() {
        bool is_forceshow_in_settings = !Net.IsRunning && UIMainMenu.IsInSettings() && UIMainMenu.cur_settings_tab == UIMainMenu._chat_setting_tab;
        if ((!is_forceshow_in_settings && (!Net.IsRunning || Con._DEV_HIDEHUD))
            || SETTING_scale <= 0f || SETTING_sizeY <= 0f || SETTING_sizeX <= 0f) {
            CHATforceinputfocus = false;
            CHAT_textbox_input_focused = false;
            return;
        }

        if (!WantsGui()) return;

        bool ogwordwrap = GUI.skin.label.wordWrap;
        int ogfontsize = GUI.skin.label.fontSize;
        int ogboxfontsize = GUI.skin.box.fontSize;
        int ogtextfieldfontsize = GUI.skin.textField.fontSize;
        bool pinnedfont = UIBullshit.PushFocusedChatFont(out var oglabelfont, out var ogboxfont, out var ogfieldfont);

        try {
            bool unchipped_hide_chat_history = Util.IsWorldGenerated()
                && !Con._DEV_CHATSPY
                && Util.IsLocalBodyAlive()
                && !Multiplayer.rules.EnableChatbox;

            int real_chat_fontsize = _GUI_RealChatFontsize;
            float final_scale = SETTING_scale * UIBullshit.uiScale;

            GUI.skin.label.fontSize = real_chat_fontsize;
            GUI.skin.box.fontSize = real_chat_fontsize;
            GUI.skin.textField.fontSize = real_chat_fontsize;

            float areawidth = DEV_CHAT_FULL_WIDTH * final_scale * SETTING_sizeX;
            float areaheight = Mathf.Max(0f, DEV_CHAT_HISTORY_HEIGHT * final_scale * SETTING_sizeY);
            float textareaheight = DEV_CHAT_TEXTFIELD_HEIGHT * final_scale;

            float real_pos_X = (Screen.width - areawidth) * SETTING_posX;
            float real_pos_Y = (Screen.height - areaheight - textareaheight) * SETTING_posY;

            var chathistory_area = new Rect(real_pos_X, real_pos_Y, areawidth, areaheight);
            var chatinputrect = new Rect(real_pos_X, real_pos_Y + areaheight, areawidth, textareaheight);
            float chatareawidth = areawidth - UIBullshit.uiBlockNanoBorderSize;
            bool should_show_it_all = Time.realtimeSinceStartupAsDouble - focustime < 5f;

            var chatinputrectfocustest = new Rect(chatinputrect);
            chatinputrectfocustest.xMin -= 50f;
            chatinputrectfocustest.xMax += 50f;
            chatinputrectfocustest.yMin -= 50f;
            chatinputrectfocustest.yMax += 50f;

            if (should_show_it_all || CHATforceinputfocus || CHAT_textbox_input_focused || mouse_is_in_chat_area || UIBullshit.IsCursorInGUIRect(chatinputrectfocustest)) {
                if (CHATforceinputfocus) {
                    GUI.FocusControl(CHAT_TEXTFIELD_CONTROLNAME);
                    CHAT_textbox_input_focused = true;
                    CHATforceinputfocus = false;
                }

                if (CHAT_textbox_input_focused) {
                    KeyCode curkeycode = Event.current.keyCode;
                    if (Event.current.type == EventType.KeyDown && (curkeycode == KeyCode.UpArrow || curkeycode == KeyCode.DownArrow)) {
                        int historytraverse = curkeycode == KeyCode.UpArrow ? -1 : 1;
                        if (MyMessageLog.Count != 0) {
                            MyMessageLog_Index += historytraverse;
                            if (MyMessageLog_Index < 0) {
                                MyMessageLog_Index = 0;
                            } else if (MyMessageLog_Index >= MyMessageLog.Count) {
                                MyMessageLog_Index = MyMessageLog.Count - 1;
                            } else {
                                if (!string.IsNullOrWhiteSpace(CHAT_current_input) && !MyMessageLog.Contains(CHAT_current_input)) {
                                    MyMessageLog.Enqueue(CHAT_current_input);
                                }
                                CHAT_current_input = MyMessageLog.ElementAt(MyMessageLog_Index);
                                _chatinput_changed = true;
                                CHATforceinputfocus = true;
                            }
                        }
                    }

                    GUI.SetNextControlName(CHAT_TEXTFIELD_CONTROLNAME);
                    string old = CHAT_current_input;
                    CHAT_current_input = GUI.TextField(chatinputrect, CHAT_current_input);
                    if (old != CHAT_current_input) {
                        _chatinput_changed = true;
                        CHATforceinputfocus = true;
                    }
                    GUI.SetNextControlName("NONE");
                } else {
                    UIBullshit._GUI_9SlicePanel(chatinputrect, 1f, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
                    var smoler = new Rect(chatinputrect);
                    smoler.xMin += UIBullshit.uiBlockNanoBorderSize;

                    GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                    GUI.Label(
                        smoler,
                        string.IsNullOrEmpty(CHAT_current_input)
                            ? string.Format(Lang.Get("chat_keybind"), KeyBinds.GetBindName(CoopKeybinds.chat))
                            : CHAT_current_input);
                    GUI.skin.label.alignment = TextAnchor.UpperLeft;
                }

                if (CHAT_textbox_input_focused) {
                    if (Event.current.type == EventType.MouseDown
                        && !chatinputrect.Contains(Event.current.mousePosition)
                        && (scrollbar_area == null || !scrollbar_area.Value.Contains(Event.current.mousePosition))) {
                        CHATforceinputfocus = false;
                        CHAT_textbox_input_focused = false;
                        GUI.FocusControl("");
                    } else if (Event.current.keyCode == KeyCode.Escape) {
                        CHATforceinputfocus = false;
                        CHAT_textbox_input_focused = false;
                        GUI.FocusControl("");
                        Event.current.keyCode = KeyCode.None;
                    } else if (Event.current.keyCode == KeyCode.Return) {
                        CHATforceinputfocus = false;
                        CHAT_textbox_input_focused = false;
                        GUI.FocusControl("");
                        _chatinput_changed = true;
                        if (!string.IsNullOrEmpty(CHAT_current_input)) {
                            OnEnteredUserMessage();
                        }
                        Event.current.keyCode = KeyCode.None;
                    } else {
                        focustime = Time.realtimeSinceStartupAsDouble;
                    }

                    if (CHAT_current_input.Length > MAX_MESSAGE_LENGTH) {
                        CHAT_current_input = CHAT_current_input.Substring(0, MAX_MESSAGE_LENGTH);
                    }
                }

                CHAT_textbox_input_focused = GUI.GetNameOfFocusedControl() == CHAT_TEXTFIELD_CONTROLNAME;
            }

            GUI.SetNextControlName("NONE");
            if (_DEV_ENABLE_PREVIEW) {
                try {
                    if (Util.IsLocalBodyAlive()
                        && CHAT_textbox_input_focused
                        && Multiplayer.rules.SpeechImpairedChat
                        && !string.IsNullOrWhiteSpace(CHAT_current_input)
                        && !CHAT_current_input.StartsWith("/")
                        && !string.IsNullOrWhiteSpace(_preview_distorted_message)
                        && _preview_distorted_message != CHAT_current_input) {
                        string torender = Lang.Get("chat_preview") + _preview_distorted_message;
                        float previewareaheight = Mathf.Max(textareaheight, GUI.skin.label.CalcHeight(FixGuiContent.Content(torender, 7), areawidth));

                        var chatpreviewrect = new Rect(chatinputrect);
                        if (!_DEV_PREVIEW_ON_SIDE) {
                            chatpreviewrect.y -= previewareaheight;
                            chatpreviewrect.height = previewareaheight;
                            chathistory_area.height -= previewareaheight;
                        } else {
                            chatpreviewrect.y = chatinputrect.yMax - previewareaheight;
                            chatpreviewrect.x += SETTING_posX > 0.5f ? -chatpreviewrect.width : chatpreviewrect.width;
                            chatpreviewrect.height = previewareaheight;
                        }

                        GUI.skin.label.wordWrap = true;
                        UIBullshit._GUI_9SlicePanel(chatpreviewrect, 1f, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
                        var chatpreviewrect_label = new Rect(chatpreviewrect);
                        chatpreviewrect_label.xMin += UIBullshit.uiBlockNanoBorderSize;
                        chatpreviewrect_label.xMax -= UIBullshit.uiBlockNanoBorderSize;
                        GUI.Label(chatpreviewrect_label, torender);
                        GUI.skin.label.wordWrap = ogwordwrap;
                    }
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.UI, "CHAT PREVIEW RENDER ERROR", e);
                }
            }

            var full_rect_of_entire_window = new Rect(real_pos_X, chathistory_area.y, areawidth, chatinputrect.yMax - chathistory_area.y);
            mouse_is_in_chat_area = UIBullshit.IsCursorInGUIRect(full_rect_of_entire_window);
            if (mouse_is_in_chat_area) {
                focustime = Time.realtimeSinceStartupAsDouble;
            }

            if (areaheight > 0f && areawidth > 0f && Time.realtimeSinceStartupAsDouble - focustime < 2f && !unchipped_hide_chat_history) {
                UIBullshit._GUI_9SlicePanel(chathistory_area, 1f, 0.65f, false, UIBullshit.unscaled_uiBlockNano);
            }

            if (should_show_it_all && !unchipped_hide_chat_history && areaheight > 0f) {
                GUI.SetNextControlName("NONE");
                GUI.skin.label.richText = true;
                GUI.skin.label.wordWrap = true;

                float textpos_x = real_pos_X + UIBullshit.uiBlockNanoBorderSize;
                _GUI_SizeOfSpace = GUI.skin.label.CalcSize(FixGuiContent.Content(" ", 8));
                if (_GUI_SizeOfSpace.x <= 0f) {
                    _GUI_SizeOfSpace = Vector2.one;
                }

                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                float full_text_height = MeasureHistory(chatareawidth);

                if (full_text_height > areaheight) {
                    float ogwidth = GUI.skin.verticalSlider.fixedWidth;
                    GUI.skin.verticalSlider.fixedWidth = GUI.skin.verticalSlider.fixedWidth / UIBullshit.uiScale * final_scale;
                    float scrollbarwidth = GUI.skin.verticalSlider.fixedWidth;
                    chatareawidth = Mathf.Max(1f, chatareawidth - scrollbarwidth * 1.05f);
                    full_text_height = MeasureHistory(chatareawidth);

                    var scrollbarrect = new Rect(chathistory_area.xMax - scrollbarwidth, chathistory_area.y, scrollbarwidth, chathistory_area.height);
                    scrollbar_area = scrollbarrect;
                    CHATScrollPosition = GUI.VerticalScrollbar(scrollbarrect, CHATScrollPosition, areaheight, full_text_height, 0f);
                    GUI.skin.verticalSlider.fixedWidth = ogwidth;
                } else {
                    CHATScrollPosition = 0f;
                    scrollbar_area = null;
                }

                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                float this_y = chathistory_area.yMax + CHATScrollPosition;

                for (int i = CHAT_LOG.Count - 1; i >= 0; i--) {
                    ChatMsgContainer textinfo = CHAT_LOG.ElementAt(i);
                    float texthiehgt = textinfo.height;
                    this_y -= texthiehgt;

                    if (this_y <= chatinputrect.y - texthiehgt * 0.8f) {
                        if (this_y < chathistory_area.y - texthiehgt) {
                            break;
                        }
                        _GUI_ChatDrawMessage(textinfo, textpos_x, ref this_y, chatareawidth, texthiehgt);
                    }
                }
                GUI.skin.label.richText = false;
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.UI, "CHAT RENDER ERROR", e);
        }

        GUI.skin.label.wordWrap = ogwordwrap;
        GUI.skin.label.fontSize = ogfontsize;
        GUI.skin.box.fontSize = ogboxfontsize;
        GUI.skin.textField.fontSize = ogtextfieldfontsize;
        UIBullshit.PopFocusedChatFont(pinnedfont, oglabelfont, ogboxfont, ogfieldfont);
    }

    public class ChatMsgContainer {
        public ScavPlayer plr;
        public string name;
        public string tag;
        public string msg;
        public string msg2;
        public string result_nametag;
        public string result_nametag_sanitized;
        public bool rich;
        public Vector2 namesize;
        public float height;

        public ChatMsgContainer(in ScavPlayer plr, in string tag, string s, in bool rich = false) {
            this.plr = plr;
            this.rich = rich;
            msg = s;
            this.tag = tag;
            name = plr.playerName;
            Compile();
        }

        public ChatMsgContainer(in string name, in string tag, string s, in bool rich = false) {
            this.rich = rich;
            msg = s;
            this.tag = tag;
            this.name = name;
            Compile();
        }

        public void Compile() {
            string taggedName = plr != null
                ? TagName($"<color=#{ColorUtility.ToHtmlStringRGB(plr.playerColor)}>{name}</color>", tag, true)
                : TagName(name, tag, true);
            result_nametag = $"[{taggedName}]: ";
            result_nametag_sanitized = Multiplayer.SanitizeRichText(result_nametag);
        }
    }
}
