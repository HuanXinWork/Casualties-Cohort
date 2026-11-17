#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;
using UnityEngine.TextCore;

namespace Together;

/// <summary>
/// 角色头顶气泡（<see cref="Talker"/>）物种专属字体绑定、缺失标点回退链与大写咆哮判定。
/// </summary>
public static class TalkerSpeciesFont {
    static float _experimentTalkSize = -1f;
    static TMP_FontAsset _expTalkFont;
    static readonly HashSet<int> _fontsLogged = new HashSet<int>();
    static readonly HashSet<int> _fontsFallbackReady = new HashSet<int>();
    static bool _missingFontLogged;

    const string ProbePunctuation = ".,!?;:'\"-";

    public static bool IsPlayerTalker(Talker talker) {
        if (talker == null) {
            return false;
        }
        Body body = talker.body != null ? talker.body : talker.GetComponentInParent<Body>();
        if (body == null) {
            return false;
        }
        if (body.TryGetNetBody(out NetBody nb)) {
            return nb.IsPlayer;
        }
        return ScavPlayer.BodyToPlayerDict.ContainsKey(body);
    }

    public static bool UseDefaultTalkerFonts() {
        return UIBullshit.USE_DEFAULT_TALKER_FONT;
    }

    public static bool CustomFontsOn() {
        return UIBullshit.USE_TALKER_SPECIES_FONT && !UseDefaultTalkerFonts();
    }

    public static void RefreshAll() {
        if (!CustomFontsOn()) {
            TalkerPixelExempt.Teardown();
            return;
        }
        if (!Net.IsRunning) {
            return;
        }
        Talker[] talkers = Object.FindObjectsOfType<Talker>();
        for (int i = 0; i < talkers.Length; i++) {
            Apply(talkers[i]);
        }
    }

    public static void Apply(Talker talker) {
        if (!Net.IsRunning || talker == null || talker.text == null) {
            return;
        }
        Body body = talker.body != null ? talker.body : talker.GetComponent<Body>();
        if (body == null) {
            return;
        }

        if (!CustomFontsOn()) {
            TalkerPixelExempt.Teardown();
            return;
        }

        CharacterSpawn.EnsureBodySpecies(body);
        EnsureExperimentTalkSize();

        int chr = CharacterSpawn.ClampChar(body.charType);
        TMP_FontAsset font = CharacterSpawn.GetNoteFont(chr);
        if (font == null) {
            if (!_missingFontLogged) {
                _missingFontLogged = true;
                log.warn("[TalkerFont] note fonts missing on this peer; talker is on the default font");
            }
        } else {
            WireNoteFontFallbacks(font, chr);
            AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();
            if (talker.text.font != null && talker.text.font != font) {
                data.GetOrAdd("DefaultTalkerFont", talker.text.font);
                data.GetOrAdd("DefaultTalkerFontSize", talker.text.fontSize);
            }
            if (talker.text.font != font) {
                talker.text.font = font;
            }
        }

        talker.text.richText = true;
        talker.text.enableAutoSizing = false;
        talker.text.extraPadding = true;

        TalkerPixelExempt.Exempt(talker);
    }

    static void EnsureExperimentTalkSize() {
        if (_experimentTalkSize > 0f && _expTalkFont != null) {
            return;
        }

        GameObject prefab = Resources.Load<GameObject>("Special/ExpTalkText") ?? Resources.Load<GameObject>("ExpTalkText");
        if (prefab != null) {
            TextMeshPro tmp = prefab.GetComponent<TextMeshPro>() ?? prefab.GetComponentInChildren<TextMeshPro>(true);
            if (tmp != null) {
                if (_expTalkFont == null) {
                    _expTalkFont = tmp.font;
                }
                if (_experimentTalkSize <= 0f && tmp.fontSize > 0f) {
                    _experimentTalkSize = tmp.fontSize;
                }
            }
        }

        if (_experimentTalkSize <= 0f) {
            _experimentTalkSize = 3f;
        }
    }

    static void WireNoteFontFallbacks(TMP_FontAsset noteFont, int charType) {
        if (noteFont == null) {
            return;
        }
        int id = noteFont.GetInstanceID();

        if (!_fontsLogged.Contains(id)) {
            _fontsLogged.Add(id);
            var missing = new List<string>();
            var present = new List<string>();
            for (int i = 0; i < ProbePunctuation.Length; i++) {
                char c = ProbePunctuation[i];
                string label = c == '"' ? "quot" : c.ToString();
                if (noteFont.HasCharacter(c, false, false)) {
                    present.Add(label);
                } else {
                    missing.Add(label);
                }
            }
            log.warn($"[TalkerFont] charType={charType} font=\"{noteFont.name}\" native=[{string.Join(" ", present)}] missing=[{string.Join(" ", missing)}]");
        }

        if (_fontsFallbackReady.Contains(id)) {
            return;
        }
        _fontsFallbackReady.Add(id);

        var chain = new List<TMP_FontAsset>();
        if (noteFont.fallbackFontAssetTable != null) {
            for (int i = 0; i < noteFont.fallbackFontAssetTable.Count; i++) {
                TMP_FontAsset existing = noteFont.fallbackFontAssetTable[i];
                if (existing != null && existing != noteFont && !chain.Contains(existing)) {
                    chain.Add(existing);
                }
            }
        }
        TMP_FontAsset expNote = CharacterSpawn.GetNoteFont(0);
        TMP_FontAsset milkyNote = CharacterSpawn.GetNoteFont(1);
        if (expNote != null && expNote != noteFont && !chain.Contains(expNote)) {
            chain.Add(expNote);
        }
        if (milkyNote != null && milkyNote != noteFont && !chain.Contains(milkyNote)) {
            chain.Add(milkyNote);
        }
        if (_expTalkFont != null && _expTalkFont != noteFont && !chain.Contains(_expTalkFont)) {
            chain.Add(_expTalkFont);
        }
        noteFont.fallbackFontAssetTable = chain;
    }

    public static float ExperimentTalkSize => _experimentTalkSize > 0f ? _experimentTalkSize : 3f;
    public static TMP_FontAsset ExperimentTalkFont => _expTalkFont;

    public static bool IsYelling(string msg) {
        if (string.IsNullOrEmpty(msg)) {
            return false;
        }
        bool anyLetter = false;
        bool inTag = false;
        for (int i = 0; i < msg.Length; i++) {
            char c = msg[i];
            if (c == '<') {
                inTag = true;
                continue;
            }
            if (inTag) {
                if (c == '>') {
                    inTag = false;
                }
                continue;
            }
            if (!char.IsLetter(c)) {
                continue;
            }
            anyLetter = true;
            if (!char.IsUpper(c)) {
                return false;
            }
        }
        return anyLetter;
    }
}

/// <summary>
/// 屏幕空间 UI 文本镜像层：将头顶 <see cref="Talker"/> 投影到主 Canvas 上并绘制 8 向黑色描边，避免受像素完美相机缩放糊化。
/// </summary>
public static class TalkerPixelExempt {
    class Mirror {
        public Talker talker;
        public RectTransform root;
        public TextMeshProUGUI ui;
        public TextMeshProUGUI[] outline;
        public bool yelling;
    }

    static readonly Vector2[] OutlineOffsets = {
        new Vector2(-1f, 0f), new Vector2(1f, 0f), new Vector2(0f, -1f), new Vector2(0f, 1f),
        new Vector2(-1f, -1f), new Vector2(1f, -1f), new Vector2(-1f, 1f), new Vector2(1f, 1f),
    };

    static RectTransform _canvas;
    static readonly List<Mirror> _mirrors = new List<Mirror>();
    static bool _sceneHooked;

    public static void Exempt(Talker talker) {
        if (talker == null || talker.text == null) {
            return;
        }
        if (!TalkerSpeciesFont.CustomFontsOn()) {
            Teardown();
            return;
        }
        EnsureCanvas();
        Mirror existing = _mirrors.FirstOrDefault(x => x.talker == talker);
        if (existing != null) {
            return;
        }

        var rootGo = new GameObject("MP_TalkMirror", typeof(RectTransform));
        rootGo.transform.SetParent(_canvas.transform, false);
        RectTransform root = rootGo.GetComponent<RectTransform>();

        var outline = new TextMeshProUGUI[OutlineOffsets.Length];
        for (int i = 0; i < OutlineOffsets.Length; i++) {
            outline[i] = CreateMirrorText(root, "outline", Color.black);
        }

        TextMeshProUGUI ui = CreateMirrorText(root, "face", Color.white);
        rootGo.SetActive(false);

        _mirrors.Add(new Mirror { talker = talker, root = root, ui = ui, outline = outline, yelling = false });
    }

    static TextMeshProUGUI CreateMirrorText(RectTransform parent, string name, Color color) {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        TextMeshProUGUI ui = go.AddComponent<TextMeshProUGUI>();
        ui.raycastTarget = false;
        ui.enableWordWrapping = true;
        ui.overflowMode = TextOverflowModes.Overflow;
        ui.alignment = TextAlignmentOptions.Center;
        ui.color = color;
        ui.extraPadding = true;
        return ui;
    }

    public static void SetYelling(Talker talker, string fullLine) {
        if (talker == null) {
            return;
        }
        Exempt(talker);
        Mirror m = Find(talker);
        if (m != null) {
            m.yelling = TalkerSpeciesFont.IsYelling(fullLine);
        }
    }

    public static void Remove(Talker talker) {
        for (int i = _mirrors.Count - 1; i >= 0; i--) {
            Mirror m = _mirrors[i];
            if (m.talker != null && m.talker != talker) {
                continue;
            }
            DestroyMirror(m);
            _mirrors.RemoveAt(i);
        }
        if (_mirrors.Count == 0 && _canvas != null) {
            Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }
    }

    public static void SyncOne(Talker talker) {
        if (talker == null || talker.text == null) {
            return;
        }
        if (!Net.IsRunning || Util.IsInMainMenu() || PlayerCamera.main == null || !TalkerSpeciesFont.CustomFontsOn()) {
            Teardown();
            return;
        }

        Mirror mirror = Find(talker);
        if (mirror == null || mirror.ui == null) {
            return;
        }

        Camera cam = ResolveMainCamera();
        if (cam == null) {
            HideMirror(mirror);
            return;
        }

        Renderer worldRend = talker.text.renderer;
        if (worldRend != null) {
            worldRend.enabled = false;
        }

        string msg = talker.text.text;
        if (string.IsNullOrEmpty(msg)) {
            HideMirror(mirror);
            return;
        }

        Vector3 screen = cam.WorldToScreenPoint(talker.text.rectTransform.position);
        if (screen.z < 0f) {
            HideMirror(mirror);
            return;
        }

        AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();
        if (mirror.root != null) {
            mirror.root.gameObject.SetActive(true);
        } else {
            mirror.ui.gameObject.SetActive(true);
        }

        Body bodyForFont = talker.body != null ? talker.body : talker.GetComponent<Body>();
        TMP_FontAsset wantFont = bodyForFont != null ? CharacterSpawn.GetNoteFont(bodyForFont.charType) : null;
        wantFont ??= talker.text.font;

        if (wantFont == null || talker.text.font != wantFont) {
            data.GetOrAdd("DefaultTalkerFont", talker.text.font);
            data.GetOrAdd("DefaultTalkerFontSize", talker.text.fontSize);
        }

        if (wantFont != null) {
            if (talker.text.font != wantFont) {
                talker.text.font = wantFont;
            }
            if (mirror.ui.font != wantFont) {
                mirror.ui.font = wantFont;
            }
            if (wantFont.material != null && mirror.ui.fontSharedMaterial != wantFont.material) {
                mirror.ui.fontSharedMaterial = wantFont.material;
            }
        }

        mirror.ui.text = msg;
        mirror.ui.color = talker.text.color;
        mirror.ui.alignment = talker.text.alignment;
        mirror.ui.enableAutoSizing = false;
        mirror.ui.enableWordWrapping = true;
        mirror.ui.overflowMode = TextOverflowModes.Overflow;
        if (mirror.root != null) {
            mirror.root.position = screen;
        } else {
            mirror.ui.rectTransform.position = screen;
        }

        float stockSize = data.GetOrDefault("DefaultTalkerFontSize", talker.text.fontSize);
        if (stockSize <= 0f) {
            stockSize = talker.text.fontSize;
        }
        TMP_FontAsset stockFont = data.GetOrDefault<TMP_FontAsset>("DefaultTalkerFont", null);
        float stockCap = CapFactor(stockFont);
        if (stockCap < 0.01f) stockCap = CapFactor(TalkerSpeciesFont.ExperimentTalkFont);
        if (stockCap < 0.01f) stockCap = 1f;

        float worldCap = stockSize * Mathf.Abs(talker.text.transform.lossyScale.y) * stockCap;
        if (!talker.text.isOrthographic) {
            worldCap *= 0.1f;
        }

        float screenCap;
        if (cam.orthographic) {
            screenCap = worldCap * cam.pixelHeight / (2f * Mathf.Max(0.01f, cam.orthographicSize));
        } else {
            Vector3 origin = talker.text.transform.position;
            Vector3 above = origin + Vector3.up * worldCap;
            screenCap = Mathf.Abs(cam.WorldToScreenPoint(above).y - cam.WorldToScreenPoint(origin).y);
        }

        float canvasScale = 1f;
        if (mirror.root != null) {
            Canvas canvas = mirror.root.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.scaleFactor > 0.01f) {
                canvasScale = canvas.scaleFactor;
            }
        }

        float noteCap = CapFactor(wantFont);
        if (noteCap < 0.01f) {
            noteCap = 1f;
        }

        const float nonDuneYell = 1.64f;
        const float duneYell = 2.5f;
        float basePx = screenCap / canvasScale / noteCap * nonDuneYell;

        string yellSrc = string.IsNullOrEmpty(talker.currentString) ? msg : talker.currentString;
        mirror.yelling = TalkerSpeciesFont.IsYelling(yellSrc);
        float yellMult = 1f;
        if (mirror.yelling) {
            Body body = talker.body != null ? talker.body : talker.GetComponent<Body>();
            int chr = body != null ? CharacterSpawn.ClampChar(body.charType) : 0;
            yellMult = chr == 2 ? duneYell : nonDuneYell;
        }
        float fontPx = Mathf.Clamp(basePx * yellMult, 1f, 512f);

        float refW = DesignResolutionWidth();
        float wrapW = Mathf.Clamp(refW * 0.28f, 120f, 420f) * nonDuneYell * 2f;
        if (mirror.yelling) {
            wrapW *= yellMult;
        }

        RectTransform sized = mirror.root != null ? mirror.root : mirror.ui.rectTransform;
        sized.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, wrapW);
        mirror.ui.fontSize = fontPx;
        mirror.ui.ForceMeshUpdate(true);
        float prefH = Mathf.Max(fontPx, mirror.ui.preferredHeight);
        sized.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, prefH + 6f);
        CopyOutline(mirror, fontPx);
    }

    static float CapFactor(TMP_FontAsset font) {
        if (font == null) return 0f;
        FaceInfo face = font.faceInfo;
        float point = face.pointSize;
        if (point < 1f) return 0f;
        float cap = face.capLine - face.baseline;
        if (cap < 1f) cap = face.ascentLine - face.baseline;
        if (cap < 1f) return 0f;
        float scale = face.scale <= 0f ? 1f : face.scale;
        return cap / point * scale;
    }

    static void CopyOutline(Mirror mirror, float fontPx) {
        if (mirror.outline == null || mirror.root == null) {
            return;
        }
        float px = 2f;
        Canvas canvas = mirror.root.GetComponentInParent<Canvas>();
        if (canvas != null && canvas.scaleFactor > 0.01f) {
            px = 2f / canvas.scaleFactor;
        }
        for (int i = 0; i < mirror.outline.Length; i++) {
            TextMeshProUGUI o = mirror.outline[i];
            if (o == null) continue;
            Vector2 off = OutlineOffsets[i] * px;
            o.rectTransform.offsetMin = off;
            o.rectTransform.offsetMax = off;
            if (o.font != mirror.ui.font) o.font = mirror.ui.font;
            if (mirror.ui.fontSharedMaterial != null && o.fontSharedMaterial != mirror.ui.fontSharedMaterial) {
                o.fontSharedMaterial = mirror.ui.fontSharedMaterial;
            }
            if (o.fontSize != fontPx) o.fontSize = fontPx;
            if (o.text != mirror.ui.text) o.text = mirror.ui.text;
            o.alignment = mirror.ui.alignment;
            o.color = Color.black;
        }
    }

    static float DesignResolutionWidth() {
        if (PlayerCamera.main != null) {
            PixelPerfectCamera ppc = PlayerCamera.main.GetComponent<PixelPerfectCamera>();
            if (ppc != null && ppc.refResolutionX > 0) {
                return ppc.refResolutionX;
            }
        }
        return 1920f;
    }

    static void HideMirror(Mirror mirror) {
        if (mirror == null || mirror.ui == null) return;
        mirror.ui.text = "";
        if (mirror.root != null) {
            mirror.root.gameObject.SetActive(false);
        } else {
            mirror.ui.gameObject.SetActive(false);
        }
    }

    static Mirror Find(Talker talker) {
        SweepDead();
        return _mirrors.FirstOrDefault(x => x.talker == talker);
    }

    static void SweepDead() {
        for (int i = _mirrors.Count - 1; i >= 0; i--) {
            Mirror m = _mirrors[i];
            if (m.talker != null && m.ui != null) continue;
            DestroyMirror(m);
            _mirrors.RemoveAt(i);
        }
    }

    static void DestroyMirror(Mirror m) {
        if (m == null) return;
        if (m.ui != null) m.ui.text = "";
        GameObject go = m.root != null ? m.root.gameObject : (m.ui != null ? m.ui.gameObject : null);
        if (go != null) Object.Destroy(go);
    }

    static void EnsureCanvas() {
        HookScene();
        if (_canvas != null || PlayerCamera.main == null || PlayerCamera.main.mainCanvas == null) {
            return;
        }
        var go = new GameObject("MP_TalkOverlayCanvas", typeof(RectTransform));
        _canvas = go.GetComponent<RectTransform>();
        _canvas.SetStretch();
        go.AddComponent<CanvasGroup>().blocksRaycasts = false;
        go.transform.SetParent(PlayerCamera.main.mainCanvas);
        go.transform.SetAsFirstSibling();
    }

    static void HookScene() {
        if (_sceneHooked) return;
        _sceneHooked = true;
        UnityEngine.SceneManagement.SceneManager.sceneLoaded += (_, __) => Teardown();
    }

    static Camera ResolveMainCamera() {
        if (PlayerCamera.main != null) {
            Camera c = PlayerCamera.main.GetComponent<Camera>();
            if (c != null) return c;
        }
        return Camera.main;
    }

    public static void Teardown() {
        for (int i = 0; i < _mirrors.Count; i++) {
            Mirror m = _mirrors[i];
            Talker talker = m.talker;
            if (talker != null && talker.text != null) {
                AnyDataContainerComponent data = talker.GetOrAddComponent<AnyDataContainerComponent>();
                talker.text.font = data.GetOrDefault("DefaultTalkerFont", talker.text.font);
                float savedSize = data.GetOrDefault("DefaultTalkerFontSize", 0f);
                if (savedSize > 0f) {
                    talker.text.fontSize = savedSize;
                }
                if (talker.text.renderer != null) {
                    talker.text.renderer.enabled = true;
                }
            }
            DestroyMirror(m);
        }
        _mirrors.Clear();
        if (_canvas != null) {
            Object.Destroy(_canvas.gameObject);
            _canvas = null;
        }
    }
}

[HarmonyPatch(typeof(Talker), "Start")]
static class Talker_Start_SpeciesFont_MultiplayerPatch {
    static void Postfix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new[] { typeof(List<string>), typeof(Limb), typeof(bool), typeof(bool) })]
static class Talker_TalkList_SpeciesFont_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.Talk), new[] { typeof(string), typeof(Limb), typeof(bool), typeof(bool) })]
static class Talker_TalkString_SpeciesFont_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerSpeciesFont.Apply(__instance);
    }
}

[HarmonyPatch(typeof(Talker), "LateUpdate")]
static class Talker_LateUpdate_PixelExempt_MultiplayerPatch {
    static void Postfix(Talker __instance) {
        TalkerPixelExempt.SyncOne(__instance);
    }
}

[HarmonyPatch(typeof(Talker), "OnDestroy")]
static class Talker_OnDestroy_PixelExempt_MultiplayerPatch {
    static void Prefix(Talker __instance) {
        TalkerPixelExempt.Remove(__instance);
    }
}
#endif
