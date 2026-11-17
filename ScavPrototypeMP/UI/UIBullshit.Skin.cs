using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using UnityEngine;

namespace Together {
    /// <summary>
    /// <see cref="UIBullshit"/> 皮肤与纹理分部：负责九宫格源纹理近邻缩放、亮度/透明度派生纹理缓存与回收、聊天像素字体固定及原版 <see cref="GUISkin"/> 备份还原。
    /// </summary>
    public partial class UIBullshit {
        private static readonly Dictionary<Texture2D, Dictionary<Vector2, Texture2D>> cached_modified_textures = new();
        private static readonly Dictionary<Texture2D, Texture2D> cached_scaled_textures = new();

        /// <summary>
        /// 皮肤强制重算标记。注意：<see cref="FixSkinSkip"/> 依赖此字段名，严禁重命名。
        /// </summary>
        internal static bool _recalculate_skin = false;

        // 保留在程序集内供构建版本识别与输入框字体固定判定
        internal static readonly string ChatFontPinMarker = "ct-chatfont-pin";

        private static RectOffset _zeroPadding;

        /// <summary>HUD 专用零内边距实例，不与 border 或 overflow 共享以避免原位篡改。</summary>
        internal static RectOffset ZeroPadding {
            get {
                if (_zeroPadding == null) {
                    _zeroPadding = new RectOffset(0, 0, 0, 0);
                }
                return _zeroPadding;
            }
        }

        internal static int _og_fontsize = 13;

        private static GUISkin new_skin;
        private static GUISkin _og_skin;
        private static GUISkin _og_skin_cloned;

        private static float _og_verticalscrollbar_fixedWidth;
        private static float _og_gorizontalscrollbar_fixedHeight;

        private static Texture2D _b_n_b;
        private static Texture2D _b_a_b;
        private static Texture2D _b_f_b;
        private static Texture2D _b_h_b;
        private static Texture2D _box_bg;
        private static Color _box_txcolor;
        private static Font _b_ogfont;
        private static RectOffset _og_border = new RectOffset(6, 6, 6, 4);

        public static Texture2D unscaled_uiBlockNano => UIManager.uiBlockNano != null ? UIManager.uiBlockNano.texture : null;
        public static Texture2D unscaled_uiBlockSmall => UIManager.uiBlockSmall != null ? UIManager.uiBlockSmall.texture : null;
        public static Texture2D tex_toggle_normal => GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f) ?? _b_n_b;
        public static Texture2D tex_button_normal_nano => GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f) ?? _b_n_b;
        public static Texture2D tex_button_active_nano => GetChangedTex(unscaled_uiBlockNano, 0.4f, 0.7f) ?? _b_a_b;
        public static Texture2D tex_button_focus_nano => GetChangedTex(unscaled_uiBlockNano, 0.4f, 0.7f) ?? _b_f_b;
        public static Texture2D tex_button_hover_nano => GetChangedTex(unscaled_uiBlockNano, 0.65f, 0.7f) ?? _b_h_b;
        public static Texture2D tex_button_normal => GetChangedTex(unscaled_uiBlockSmall, 1f, 0.8f) ?? _b_n_b;
        public static Texture2D tex_button_active => GetChangedTex(unscaled_uiBlockSmall, 0.4f, 0.7f) ?? _b_a_b;
        public static Texture2D tex_button_focus => GetChangedTex(unscaled_uiBlockSmall, 0.4f, 0.7f) ?? _b_f_b;
        public static Texture2D tex_button_hover => GetChangedTex(unscaled_uiBlockSmall, 0.65f, 0.7f) ?? _b_h_b;

        private static Texture2D TexDarkener(Texture2D t, float m, float alpha) {
            Texture2D copy = new Texture2D(
                t.width,
                t.height,
                TextureFormat.RGBA32,
                t.mipmapCount > 1);
            copy.filterMode = FilterMode.Point;

            Color[] pixels = t.GetPixels();
            for (int i = 0; i < pixels.Length; i++) {
                Color c = pixels[i];
                float origAlpha = c.a;
                c *= m;
                c.a = origAlpha * alpha;
                pixels[i] = c;
            }

            copy.SetPixels(pixels);
            copy.Apply();
            return copy;
        }

        private static Texture2D TexScaler(Texture2D t, float s) {
            int scaledW = Mathf.Max(1, (int)Mathf.Ceil(t.width * s));
            int scaledH = Mathf.Max(1, (int)Mathf.Ceil(t.height * s));
            Texture2D scaled = new Texture2D(
                scaledW,
                scaledH,
                TextureFormat.RGBA32,
                t.mipmapCount > 1);
            scaled.filterMode = FilterMode.Point;

            float ratioX = t.width / (float)scaled.width;
            float ratioY = t.height / (float)scaled.height;

            Color[] ogPixels = t.GetPixels();
            Color[] pixels = new Color[scaled.width * scaled.height];
            for (int x = 0; x < scaled.width; x++) {
                int srcX = Mathf.Clamp(Mathf.FloorToInt(x * ratioX), 0, t.width - 1);
                for (int y = 0; y < scaled.height; y++) {
                    int srcY = Mathf.Clamp(Mathf.FloorToInt(y * ratioY), 0, t.height - 1);
                    pixels[x + y * scaled.width] = ogPixels[srcX + srcY * t.width];
                }
            }

            scaled.SetPixels(pixels);
            scaled.Apply();
            return scaled;
        }

        public static Texture2D GetChangedTex(Texture2D og, float brightness, float alpha) {
            if (og == null) {
                return null;
            }
            Texture2D scaled = GetScaledTex(og);
            if (scaled == null) {
                return null;
            }
            Vector2 key = new Vector2(brightness, alpha);

            if (!cached_modified_textures.TryGetValue(og, out Dictionary<Vector2, Texture2D> bucket)) {
                bucket = new Dictionary<Vector2, Texture2D>();
                cached_modified_textures[og] = bucket;
            }
            if (!bucket.TryGetValue(key, out Texture2D result) || result == null) {
                result = TexDarkener(scaled, brightness, alpha);
                bucket[key] = result;
            }
            return result;
        }

        public static Texture2D GetScaledTex(Texture2D original) {
            if (original == null) {
                return null;
            }
            if (!cached_scaled_textures.TryGetValue(original, out Texture2D result) || result == null) {
                result = TexScaler(original, uiScaleForMostUI);
                cached_scaled_textures[original] = result;
            }
            return result;
        }

        public static void ResizeGUITextures() {
            // 释放动态生成的缩放与变暗 Texture2D，防止频繁切换分辨率/UI 缩放导致非托管显存泄漏
            foreach (KeyValuePair<Texture2D, Dictionary<Vector2, Texture2D>> kvp in cached_modified_textures) {
                if (kvp.Value == null) {
                    continue;
                }
                foreach (KeyValuePair<Vector2, Texture2D> inner in kvp.Value) {
                    if (inner.Value != null && !ReferenceEquals(inner.Value, kvp.Key)) {
                        Destroy(inner.Value);
                    }
                }
            }
            foreach (KeyValuePair<Texture2D, Texture2D> kvp in cached_scaled_textures) {
                if (kvp.Value != null && !ReferenceEquals(kvp.Value, kvp.Key)) {
                    Destroy(kvp.Value);
                }
            }

            cached_modified_textures.Clear();
            cached_scaled_textures.Clear();

            last_screensize = Util.GetResolution();
            _recalculate_skin = true;
        }

        public static int GetCurFontSize(float additional_scale = 1f) {
            if (USE_GAME_FONT) {
                return Mathf.RoundToInt(DEFAULT_RETROGAME_FONTSIZE * uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT * additional_scale);
            }
            return Mathf.RoundToInt(_og_fontsize * 2f * uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT * additional_scale);
        }

        private static void ApplySliderSkin(GUIStyle slider, GUIStyle thumb) {
            thumb.normal.background = GetChangedTex(unscaled_uiBlockNano, 1f, 1f);
            thumb.focused.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 1f);
            thumb.hover.background = thumb.focused.background;
            thumb.active.background = GetChangedTex(unscaled_uiBlockNano, 0.8f, 1f);
            slider.normal.background = GetChangedTex(unscaled_uiBlockNano, 1f, 1f);
            slider.overflow = new RectOffset(0, 0, 0, 0);
            thumb.overflow = new RectOffset(0, 0, 0, 0);
            slider.padding = new RectOffset(0, 0, 0, 0);

            slider.border = new RectOffset(uiBlockNanoBorderSize, uiBlockNanoBorderSize, uiBlockNanoBorderSize, uiBlockNanoBorderSize);
            thumb.border = slider.border;

            float barThickness = 20f * uiScale;
            if (thumb.fixedHeight != 0f) {
                thumb.fixedHeight = barThickness;
            }
            if (thumb.fixedWidth != 0f) {
                thumb.fixedWidth = barThickness;
            }
        }

        private static void ApplyFontToStyle(GUIStyle style) {
            if (USE_GAME_FONT) {
                // 直接将字体挂载到具体 GUIStyle，防止输入框失焦后回退为 Unity 默认皮肤字体
                if (CoopModAssets.gamefont != null) {
                    style.font = CoopModAssets.gamefont;
                }
                style.fontSize = GetCurFontSize();
            } else {
                style.font = null;
                style.fontSize = _og_fontsize;
            }
        }

        internal static Font FocusedChatFont() {
            if (ChatFontPinMarker == null || !USE_GAME_FONT) {
                return null;
            }
            return CoopModAssets.gamefont;
        }

        internal static bool PushFocusedChatFont(out Font label, out Font box, out Font field) {
            label = null;
            box = null;
            field = null;
            Font font = FocusedChatFont();
            if (font == null || GUI.skin == null) {
                return false;
            }

            label = GUI.skin.label.font;
            box = GUI.skin.box.font;
            field = GUI.skin.textField.font;
            GUI.skin.label.font = font;
            GUI.skin.box.font = font;
            GUI.skin.textField.font = font;
            return true;
        }

        internal static void PopFocusedChatFont(bool pushed, Font label, Font box, Font field) {
            if (!pushed || GUI.skin == null) {
                return;
            }
            GUI.skin.label.font = label;
            GUI.skin.box.font = box;
            GUI.skin.textField.font = field;
        }

        public static RectOffset RectOffset(int n) {
            return new RectOffset(n, n, n, n);
        }

        /// <summary>仅当存在菜单、聊天框、投票弹窗、交互轮盘或角色失能面板时才启用联机 IMGUI 皮肤。</summary>
        internal static bool WantsMpChrome() {
            bool console = Con.IsConsoleOpen();
            if (!console) {
                if (UIMainMenu.mainmenu_open || IsAnyMenuOpen()) {
                    return true;
                }
                if (UIChoicePrompt.prompt_show_progress != 0f) {
                    return true;
                }
                if (UIInGame.interaction_menu_target_body != null) {
                    return true;
                }
                if (UIInGame.SpectatorModeActive || Con._DEV_FREECAM) {
                    return true;
                }
            }
            if (Chat.WantsGui()) {
                return true;
            }
            if (console) {
                return false;
            }
            if (!Multiplayer.IsNetworkActiveAndIsInGame()) {
                return false;
            }

            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me == null || me.body == null || !me.body.alive || me.body.sleeping) {
                return true;
            }
            return me.netBody != null && (me.netBody.piggybacking_on != null || me.netBody.carrying_person != null);
        }

        public static void _GUI_SetButtonSkinTexture(GUIStyle style, bool small = true) {
            if (small) {
                style.normal.background = tex_button_normal;
                style.active.background = tex_button_active;
                style.focused.background = tex_button_focus;
                style.hover.background = tex_button_hover;
                style.border = RectOffset(uiBlockSmallBorderSize);
                style.onNormal.background = GetChangedTex(unscaled_uiBlockSmall, 1.05f, 1f);
            } else {
                style.normal.background = tex_button_normal_nano;
                style.active.background = tex_button_active_nano;
                style.focused.background = tex_button_focus_nano;
                style.hover.background = tex_button_hover_nano;
                style.border = RectOffset(uiBlockNanoBorderSize);
                style.onNormal.background = GetChangedTex(unscaled_uiBlockNano, 1.05f, 1f);
            }

            style.onActive.background = style.active.background;
            style.onFocused.background = style.focused.background;
            style.onHover.background = style.hover.background;
            style.padding = style.border;
        }

        internal static void _GUI_MPUISetSkinValues() {
            if (!FixSkinSkip.Allow()) {
                return;
            }

            _GUI_SetButtonSkinTexture(GUI.skin.button, true);
            GUI.skin.box.normal.background = tex_button_hover_nano;

            GUI.skin.toggle.normal.background = GetChangedTex(CoopModAssets.checkbox.texture, 1f, 1f);
            if (GUI.skin.toggle.normal.background != null) {
                GUI.skin.toggle.border = new RectOffset(GUI.skin.toggle.normal.background.width, 1, GUI.skin.toggle.normal.background.height, 1);
                GUI.skin.toggle.padding.left = GUI.skin.toggle.normal.background.width;
                GUI.skin.toggle.padding.top = (int)(GUI.skin.toggle.normal.background.height * 0.21f);
            }
            GUI.skin.toggle.focused.background = GetChangedTex(CoopModAssets.checkbox.texture, 0.9f, 1f);
            GUI.skin.toggle.hover.background = GUI.skin.toggle.focused.background;
            GUI.skin.toggle.active.background = GetChangedTex(CoopModAssets.checkbox.texture, 0.8f, 1f);
            GUI.skin.toggle.onNormal.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 1f, 1f);
            GUI.skin.toggle.onHover.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 0.9f, 1f);
            GUI.skin.toggle.onFocused.background = GUI.skin.toggle.onHover.background;
            GUI.skin.toggle.onActive.background = GetChangedTex(CoopModAssets.checkbox_ticked.texture, 0.8f, 1f);

            ApplySliderSkin(GUI.skin.horizontalSlider, GUI.skin.horizontalSliderThumb);
            ApplySliderSkin(GUI.skin.horizontalScrollbar, GUI.skin.horizontalScrollbarThumb);
            ApplySliderSkin(GUI.skin.verticalSlider, GUI.skin.verticalSliderThumb);
            ApplySliderSkin(GUI.skin.verticalScrollbar, GUI.skin.verticalScrollbarThumb);

            float barThickness = 20f * uiScale;
            GUI.skin.verticalScrollbar.fixedWidth = barThickness;
            GUI.skin.horizontalScrollbar.fixedHeight = barThickness;
            GUI.skin.verticalSlider.fixedWidth = barThickness;
            GUI.skin.horizontalSlider.fixedHeight = barThickness;

            GUI.skin.textField.normal.background = GetChangedTex(unscaled_uiBlockNano, 0.8f, 0.8f);
            GUI.skin.textField.hover.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 0.8f);
            GUI.skin.textField.focused.background = GetChangedTex(unscaled_uiBlockNano, 1f, 0.8f);
            GUI.skin.textField.onNormal.background = GetChangedTex(unscaled_uiBlockNano, 0.9f, 0.8f);

            GUI.skin.textArea.normal.background = GUI.skin.textField.normal.background;
            GUI.skin.textArea.hover.background = GUI.skin.textField.hover.background;
            GUI.skin.textArea.focused.background = GUI.skin.textField.focused.background;
            GUI.skin.textArea.onNormal.background = GUI.skin.textField.onNormal.background;
            GUI.skin.textArea.onNormal.textColor = Color.white;
            GUI.skin.textArea.focused.textColor = Color.white;

            if (CoopModAssets.gamefont != null) {
                GUI.skin.font = USE_GAME_FONT ? CoopModAssets.gamefont : _b_ogfont;
                ApplyFontToStyle(GUI.skin.button);
                ApplyFontToStyle(GUI.skin.textField);
                ApplyFontToStyle(GUI.skin.textArea);
                ApplyFontToStyle(GUI.skin.label);
                ApplyFontToStyle(GUI.skin.box);
                ApplyFontToStyle(GUI.skin.toggle);
            }
        }

        internal static void _GUI_DoTexturesForMPUserInterface() {
            try {
                if (_og_skin_cloned == null) {
                    _og_skin_cloned = Instantiate(GUI.skin);
                }
                if (new_skin == null) {
                    new_skin = Instantiate(GUI.skin);
                }
                GUI.skin = new_skin;

                // 先执行皮肤刷新让 FixSkinSkip.Allow() 观测到 _recalculate_skin 置位，刷新后再清零
                _GUI_MPUISetSkinValues();
                _recalculate_skin = false;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIBullshit._GUI_DoTexturesForMPUserInterface", ex);
            }
        }

        internal static void _GUI_CaptureOGUITextures() {
            if (_og_skin == null) {
                _og_skin = GUI.skin;
                _og_verticalscrollbar_fixedWidth = GUI.skin.verticalScrollbar.fixedWidth;
                _og_gorizontalscrollbar_fixedHeight = GUI.skin.horizontalScrollbar.fixedHeight;
            }
            if (_b_n_b == null) {
                _og_fontsize = GUI.skin.button.fontSize;
                _b_n_b = GUI.skin.button.normal.background;
                _b_a_b = GUI.skin.button.active.background;
                _b_f_b = GUI.skin.button.focused.background;
                _b_h_b = GUI.skin.button.hover.background;

                if (_og_border == null) {
                    _og_border = GUI.skin.button.border;
                }

                _box_bg = GUI.skin.box.normal.background;
                _box_txcolor = GUI.skin.box.normal.textColor;
                _b_ogfont = GUI.skin.font;
            }
        }

        internal static void _GUI_ResetToOGUITextures() {
            if (_og_skin != null) {
                GUI.skin = _og_skin;
            }
        }
    }
}
