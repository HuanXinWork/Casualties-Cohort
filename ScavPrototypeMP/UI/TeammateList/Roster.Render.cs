using HarmonyLib;
using System;
using System.Reflection;
using UnityEngine;

namespace Together.UI.TeammateList {
    /// <summary>
    /// 队友状态列表 IMGUI 绘制器：贴靠原版深度计左侧边缘布局，复用 <see cref="GUIContent"/> 测量文本并按图集批次合批绘制 Moodle 图标。
    /// </summary>
    internal sealed partial class Roster {
        private Rect _panel;
        private string _pageLabel = "";
        private int _pageLabelKey = -1;

        private Texture2D _px;
        private Texture2D _frame;
        private int _frameW;
        private int _frameH;

        private GUIStyle _left;
        private GUIStyle _right;
        private GUIStyle _digit;
        private GUIStyle _small;
        private GUIStyle _smallRight;

        private int _styleH = -1;
        private int _fontSize = -1;
        private Font _font;

        private GameObject _meterObj;
        private RectTransform _meterRt;
        private Canvas _meterCanvas;
        private Vector3[] _corners;

        // 复用单一 GUIContent 测量文本宽高，消除 Repaint 阶段每槽位 new GUIContent 堆分配
        private readonly GUIContent _measureContent = new GUIContent();

        private static FieldInfo _gameFontField;
        private static bool _gameFontReady;

        public void Draw() {
            Event current = Event.current;
            if (current == null || _collapsed || _high == 0 || Covered()) {
                _panel = default;
                for (int i = 0; i < PageSize; i++) {
                    _hit[i] = default;
                }
                return;
            }
            if (current.type == EventType.MouseDown && current.button == 1) {
                RightClick(current);
                return;
            }

            // 仅在滚轮翻页与 Repaint 阶段进入绘制计算，保留上一次 Repaint 的 _hit 供帧间拖拽检测
            bool scroll = current.type == EventType.ScrollWheel;
            bool repaint = current.type == EventType.Repaint;
            if (!scroll && !repaint) {
                return;
            }

            float scale = Mathf.Max(0.75f, Screen.height / 1080f);
            EnsureStyles(scale);

            int pageCount = (_high + PageSize - 1) / PageSize;
            _page = Mathf.Clamp(_page, 0, Mathf.Max(0, pageCount - 1));

            float cardW = 360f * scale;
            float cardH = 112f * scale;
            float gapY = 8f * scale;
            float panelH = PageSize * cardH + (PageSize - 1) * gapY;
            float panelX = MeterLeft() - cardW - 3f;
            float panelY = (Screen.height - panelH) * 0.5f;
            _panel = new Rect(panelX, panelY, cardW, panelH);

            if (pageCount > 1 && scroll && _panel.Contains(current.mousePosition)) {
                if (current.delta.y > 0f) {
                    _page++;
                } else if (current.delta.y < 0f) {
                    _page--;
                }
                _page = Mathf.Clamp(_page, 0, pageCount - 1);
                current.Use();
            }

            if (!repaint) {
                return;
            }

            float hintH = 14f * scale;
            Rect hint = new Rect(panelX, panelY - hintH - 2f * scale, cardW, hintH);
            if (pageCount > 1) {
                int labelKey = _page * 16 + pageCount;
                if (labelKey != _pageLabelKey) {
                    _pageLabelKey = labelKey;
                    _pageLabel = (_page + 1) + "/" + pageCount;
                }
                GUI.Label(new Rect(hint.x, hint.y, hint.width * 0.4f, hint.height), _pageLabel, _small);
            }
            GUI.Label(hint, Bind.HideText(), _smallRight);

            int pageStart = _page * PageSize;
            for (int j = 0; j < PageSize; j++) {
                Rect cardRect = new Rect(panelX, panelY + j * (cardH + gapY), cardW, cardH);
                _hit[j] = cardRect;
                Slot slot = (pageStart + j < _high) ? _slots[pageStart + j] : null;
                bool valid = slot != null && slot.Player != null;
                DrawSlot(cardRect, scale, j + 1, valid ? slot : null);
            }
        }

        private static Font GameFont() {
            if (!_gameFontReady) {
                _gameFontReady = true;
                Type type = AccessTools.TypeByName("Together.CoopModAssets")
                    ?? AccessTools.TypeByName("KrokoshaCasualtiesMP.CoopModAssets");
                if (type != null) {
                    _gameFontField = type.GetField("gamefont", BindingFlags.Static | BindingFlags.Public);
                }
            }
            if (_gameFontField == null) {
                return null;
            }
            return _gameFontField.GetValue(null) as Font;
        }

        private Vector2 MeasureText(GUIStyle style, string text) {
            _measureContent.text = text ?? "";
            return style.CalcSize(_measureContent);
        }

        private void EnsureStyles(float s) {
            int height = Screen.height;
            Font font = GameFont();
            if (font == null && GUI.skin != null) {
                font = GUI.skin.font;
            }
            int fontSize = Mathf.Max(12, font != null ? UIBullshit.GetCurFontSize(0.5f) : Mathf.RoundToInt(15f * s));

            if (_left == null || _styleH != height || _px == null || _font != font || _fontSize != fontSize) {
                _styleH = height;
                _font = font;
                _fontSize = fontSize;

                if (_px == null) {
                    _px = new Texture2D(1, 1, TextureFormat.RGBA32, false);
                    _px.SetPixel(0, 0, Color.white);
                    _px.Apply(false, true);
                    _px.hideFlags = HideFlags.HideAndDontSave;
                }

                _left = new GUIStyle(GUI.skin.label) {
                    font = font,
                    fontSize = fontSize,
                    alignment = TextAnchor.MiddleLeft,
                    wordWrap = false,
                    clipping = TextClipping.Clip
                };
                _left.normal.textColor = Color.white;

                _right = new GUIStyle(_left) {
                    alignment = TextAnchor.MiddleRight
                };

                _digit = new GUIStyle(_left) {
                    fontSize = Mathf.Max(12, fontSize - 2),
                    alignment = TextAnchor.MiddleCenter
                };
                _digit.normal.textColor = new Color(0.82f, 0.86f, 0.9f, 1f);

                int smallSize = Mathf.Max(10, Mathf.RoundToInt(fontSize * 0.72f));
                _small = new GUIStyle(_left) {
                    fontSize = smallSize,
                    alignment = TextAnchor.MiddleLeft
                };
                _small.normal.textColor = _digit.normal.textColor;

                _smallRight = new GUIStyle(_small) {
                    alignment = TextAnchor.MiddleRight
                };
            }
        }

        private float MeterLeft() {
            PlayerCamera cam = PlayerCamera.main;
            GameObject meterObj = cam != null ? cam.depthMeter : null;
            if (meterObj == null) {
                return Screen.width;
            }
            if (meterObj != _meterObj) {
                _meterObj = meterObj;
                _meterRt = meterObj.GetComponent<RectTransform>();
                _meterCanvas = _meterRt != null ? _meterRt.GetComponentInParent<Canvas>() : null;
            }
            if (_meterRt == null) {
                return Screen.width;
            }
            _corners ??= new Vector3[4];
            _meterRt.GetWorldCorners(_corners);
            float minX = CornerX(_corners[0]);
            for (int i = 1; i < 4; i++) {
                float cx = CornerX(_corners[i]);
                if (cx < minX) {
                    minX = cx;
                }
            }
            return minX;
        }

        private float CornerX(Vector3 world) {
            if (_meterCanvas == null || _meterCanvas.renderMode == RenderMode.ScreenSpaceOverlay) {
                return world.x;
            }
            Camera cam = _meterCanvas.worldCamera;
            if (cam == null && PlayerCamera.main != null) {
                cam = PlayerCamera.main.GetComponent<Camera>();
            }
            return cam == null ? world.x : cam.WorldToScreenPoint(world).x;
        }

        private void DrawSlot(Rect rect, float s, int digit, Slot slot) {
            Color prevColor = GUI.color;
            bool lost = slot != null && !slot.InRange;

            EnsureFrame(Mathf.RoundToInt(rect.width), Mathf.RoundToInt(rect.height));
            GUI.color = lost ? new Color(0.55f, 0.55f, 0.55f, 1f) : Color.white;
            GUI.DrawTexture(rect, _frame);

            Rect inner = Expand(rect, -6f);
            GUI.color = lost
                ? new Color(0.35f, 0.35f, 0.35f, 0.55f)
                : new Color(slot?.Danger ?? 0f, 0f, 0f, 0.25f);
            GUI.DrawTexture(inner, _px);

            GUI.color = lost ? new Color(0.75f, 0.75f, 0.75f, 1f) : Color.white;
            float padY = 4f * s;
            float rowH = (inner.height - padY * 2f) / 4f;
            float topY = inner.y + padY;
            float digitW = 18f * s;
            float iconSize = rowH * 1.2f;
            if (_left != null) {
                float textH = MeasureText(_left, "Ay").y;
                if (textH >= 1f && textH < rowH) {
                    iconSize = textH * 1.2f;
                }
            }

            GUI.Label(new Rect(inner.x, inner.y, digitW, inner.height), digit.ToString(), _digit);
            if (slot == null) {
                GUI.color = prevColor;
                return;
            }

            if (slot.Head == null) {
                SpeciesIcon.Assign(slot, slot.Species);
            }
            float gutter = digitW;
            if (slot.Head != null && iconSize >= 1f) {
                float iconX = inner.x + digitW;
                float iconY = inner.y + (inner.height - iconSize) * 0.5f;
                Rect iconRect = new Rect(iconX, iconY, iconSize, iconSize);
                DrawSprite(iconRect, slot.Head);
                DrawSprite(iconRect, slot.HeadDetail);
                gutter += iconSize + 2f * s;
            }

            float contentX = inner.x + gutter;
            float contentW = Mathf.Max(1f, inner.xMax - contentX - 4f * s);
            float halfW = contentW * 0.5f;
            bool dead = slot.Body != null && !slot.Body.alive;

            float statusW = Mathf.Min(MeasureText(_right, Terminated).x + 4f * s, contentW * 0.55f);
            Color mutedGray = new Color(0.75f, 0.75f, 0.75f, 1f);

            _left.normal.textColor = lost ? mutedGray : slot.NameColor;
            Rect nameRect = new Rect(contentX, topY, contentW - statusW, rowH);
            Label(nameRect, slot.Name, _left);
            if (dead) {
                Strike(nameRect, slot.Name, lost ? mutedGray : slot.NameColor, s);
            }

            _right.normal.textColor = lost ? mutedGray : _digit.normal.textColor;
            Label(new Rect(contentX + contentW - statusW, topY, statusW, rowH), dead ? Terminated : Bind.OpenText(digit), _right);

            _left.normal.textColor = lost ? mutedGray : Color.white;
            _right.normal.textColor = _left.normal.textColor;

            if (lost) {
                Label(new Rect(contentX, topY + rowH, contentW - 4f, rowH), LostConnection, _left);
                Label(new Rect(contentX, topY + rowH * 2f, contentW - 4f, rowH), LostConnection, _left);
                Label(new Rect(contentX, topY + rowH * 3f, contentW - 4f, rowH), LostConnection, _left);
            } else {
                DrawMoodles(new Rect(contentX, topY + rowH, halfW - 4f, rowH), slot);
                Label(new Rect(contentX + halfW, topY + rowH, halfW - 4f, rowH), slot.Blood, _right);
                Label(new Rect(contentX, topY + rowH * 2f, halfW - 4f, rowH), slot.Mood, _left);
                _right.clipping = TextClipping.Overflow;
                Label(new Rect(contentX, topY + rowH * 2f, contentW - 4f, rowH), slot.Mind, _right);
                _right.clipping = TextClipping.Clip;
                Label(new Rect(contentX, topY + rowH * 3f, halfW - 4f, rowH), slot.Yap, _left);
                Label(new Rect(contentX + halfW, topY + rowH * 3f, halfW - 4f, rowH), slot.Satiety, _right);
            }
            GUI.color = prevColor;
        }

        private void DrawMoodles(Rect area, Slot slot) {
            int count = Mathf.Clamp(slot.MoodleCount, 0, 10);
            if (count <= 0) {
                return;
            }
            float iconSize = Mathf.Min(area.height, area.width / count);
            if (iconSize < 1f) {
                return;
            }
            float iconY = area.y + (area.height - iconSize) * 0.5f;
            // 背景与前景分别共享各自图集，分两趟绘制可避免逐图标切换纹理导致的 DrawCall 断裂
            for (int i = 0; i < count; i++) {
                DrawSprite(new Rect(area.x + i * iconSize, iconY, iconSize, iconSize), slot.MoodleBack[i]);
            }
            for (int i = 0; i < count; i++) {
                DrawSprite(new Rect(area.x + i * iconSize, iconY, iconSize, iconSize), slot.MoodleIcon[i]);
            }
        }

        private static void DrawSprite(Rect r, Sprite sprite) {
            if (sprite == null) {
                return;
            }
            Texture tex = sprite.texture;
            if (tex == null || tex.width <= 0 || tex.height <= 0) {
                return;
            }
            Rect tr = sprite.textureRect;
            float w = tex.width;
            float h = tex.height;
            GUI.DrawTextureWithTexCoords(r, tex, new Rect(tr.x / w, tr.y / h, tr.width / w, tr.height / h));
        }

        private static Rect Expand(Rect r, float n) {
            r.xMin -= n;
            r.yMin -= n;
            r.xMax += n;
            r.yMax += n;
            return r;
        }

        private void Strike(Rect row, string text, Color color, float s) {
            if (_px == null || _left == null || string.IsNullOrEmpty(text)) {
                return;
            }
            float width = Mathf.Min(MeasureText(_left, text).x, row.width);
            if (width < 1f) {
                return;
            }
            float thick = Mathf.Max(1f, s);
            float y = row.y + (row.height - thick) * 0.5f;
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(row.x, y, width, thick), _px);
            GUI.color = previous;
        }

        private static void Label(Rect r, string text, GUIStyle style) {
            if (!string.IsNullOrEmpty(text)) {
                GUI.Label(r, text, style);
            }
        }

        private void EnsureFrame(int w, int h) {
            w = Mathf.Max(8, w);
            h = Mathf.Max(8, h);
            if (_frame != null && _frameW == w && _frameH == h) {
                return;
            }
            if (_frame != null) {
                UnityEngine.Object.Destroy(_frame);
            }

            Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            Color32[] pixels = new Color32[w * h];
            Color32 white = new Color32(255, 255, 255, 255);
            Color32 gray = new Color32(107, 107, 107, 255);
            Color32 black = new Color32(20, 20, 20, 255);
            Color32 clear = new Color32(0, 0, 0, 0);

            for (int y = 0; y < h; y++) {
                int yEdge = y < (h - 1 - y) ? y : (h - 1 - y);
                for (int x = 0; x < w; x++) {
                    int xEdge = x < (w - 1 - x) ? x : (w - 1 - x);
                    int band = xEdge < yEdge ? xEdge : yEdge;
                    Color32 c = clear;
                    if (band < 2) {
                        c = white;
                    } else if (band < 4) {
                        c = gray;
                    } else if (band < 6) {
                        c = black;
                    }
                    pixels[y * w + x] = c;
                }
            }

            tex.SetPixels32(pixels);
            tex.Apply(false, true);
            tex.filterMode = FilterMode.Point;
            tex.hideFlags = HideFlags.HideAndDontSave;
            _frame = tex;
            _frameW = w;
            _frameH = h;
        }
    }
}
