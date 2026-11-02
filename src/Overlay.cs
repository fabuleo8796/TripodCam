using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace TripodCam;

/// The on-screen parts, StickerSwap's style made cheap: dark see-through pills with Geist text, built once a few
/// seconds after launch, then only switched on and off. Nothing here runs per frame except a toast's 0.4 s fade-out.
/// - Placing: the video's frame (with dark bars where the screen's shape differs), thirds, the mode switcher, fly
///   info, key hints, and in mode 4 the keyframe timeline and path (Timeline.cs).
/// - REC badge with the time (updates once a second), and the live preview while recording.
/// - Toasts.
/// The panel (Panel.cs) is built from the same pieces.
internal static class Overlay
{
    internal static readonly Color PanelColor = new(0.06f, 0.07f, 0.1f, 0.74f);
    internal static readonly Color Accent = new(0.239f, 1f, 0.478f, 1f);
    internal static readonly Color AccentText = new(0.024f, 0.17f, 0.08f, 1f);
    internal static readonly Color RecRed = new(1f, 0.231f, 0.188f, 1f);
    internal static readonly Color Muted = new(1f, 1f, 1f, 0.72f);
    static readonly string[] ModeLabels = { "1   Fixed", "2   Aim at Flippy", "3   Follow Flippy", "4   Keyframes" };
    static readonly string[] ModeHints =
    {
        "Stays exactly where you plant it",
        "Stays put and turns to keep Flippy in frame",
        "Moves with Flippy at the same distance and angle",
        "Glides smoothly through the keyframes you set",
    };

    static bool built;
    static GameObject placing, rec, toast, preview;
    static RectTransform frame, barA, barB, modeBar, modeHintChip, flyChip, recChip, toastChip, plantChip, escChip, keysChip;
    static readonly Image[] modeChips = new Image[4];
    static readonly TextMeshProUGUI[] modeTexts = new TextMeshProUGUI[4];
    static TextMeshProUGUI modeHint, flyInfo, recText, toastText, previewText, plantText, escText, keysText;
    static RawImage previewImage;
    static CanvasGroup toastGroup;
    static float toastUntil;
    static string recInfo = "";
    internal static TMP_FontAsset semiBold, medium;
    internal static Canvas canvas;
    internal static Sprite rounded, small, circle;

    public static void Build()
    {
        if (built) return;
        built = true;
        try
        {
            semiBold = Font("SemiBold");
            medium = Font("Medium");
            rounded = MakeRounded(64, 24);
            small = MakeRounded(40, 12);
            circle = MakeRounded(64, 32);

            var canvasGo = new GameObject("TripodCam UI");
            Object.DontDestroyOnLoad(canvasGo);
            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 30010;
            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;
            var root = canvasGo.transform;

            BuildPlacing(root);
            BuildRec(root);
            BuildPreview(root);
            BuildToast(root);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("couldn't build the overlay: " + e);
        }
    }

    static void BuildPlacing(Transform root)
    {
        placing = Node(root, "Placing").gameObject;
        Stretch(placing.GetComponent<RectTransform>());
        barA = Plain(placing.transform, new Color(0f, 0f, 0f, 0.5f));
        barB = Plain(placing.transform, new Color(0f, 0f, 0f, 0.5f));
        frame = Node(placing.transform, "Frame");

        // corner brackets and thirds, on the video's frame
        foreach (var (ax, ay) in new[] { (0f, 0f), (1f, 0f), (0f, 1f), (1f, 1f) })
        {
            float sx = ax == 0f ? 1f : -1f, sy = ay == 0f ? 1f : -1f;
            var h = Plain(frame, Color.white);
            Anchor(h, new Vector2(ax, ay), new Vector2(ax, ay), new Vector2(sx * 24f, sy * 24f), new Vector2(64f, 5f));
            var v = Plain(frame, Color.white);
            Anchor(v, new Vector2(ax, ay), new Vector2(ax, ay), new Vector2(sx * 24f, sy * 24f), new Vector2(5f, 64f));
        }
        var line = new Color(1f, 1f, 1f, 0.28f);
        foreach (float t in new[] { 1f / 3f, 2f / 3f })
        {
            var vl = Plain(frame, line);
            vl.anchorMin = new Vector2(t, 0f); vl.anchorMax = new Vector2(t, 1f); vl.sizeDelta = new Vector2(2f, 0f); vl.anchoredPosition = Vector2.zero;
            var hl = Plain(frame, line);
            hl.anchorMin = new Vector2(0f, t); hl.anchorMax = new Vector2(1f, t); hl.sizeDelta = new Vector2(0f, 2f); hl.anchoredPosition = Vector2.zero;
        }

        Chip(frame, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(40f, -40f), 46f, 20f, PanelColor,
            "<color=#3DFF7A>TRIPOD CAM</color>   placing the tripod", semiBold, 20f, Color.white, out _);

        // mode switcher: a dark pill with three chips, the chosen one green
        modeBar = Chip(frame, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -36f), 58f, 0f, PanelColor, "", medium, 1f, Color.clear, out _);
        float x = 6f;
        for (int i = 0; i < 4; i++)
        {
            var chip = Chip(modeBar, new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(x, 0f), 46f, 22f, Color.clear, ModeLabels[i], semiBold, 20f, Muted, out modeTexts[i]);
            modeChips[i] = chip.GetComponent<Image>();
            x += chip.sizeDelta.x + 4f;
        }
        modeBar.sizeDelta = new Vector2(x + 2f, 58f);
        modeHintChip = Chip(frame, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -104f), 36f, 16f, PanelColor, ModeHints[1], medium, 17f, Muted, out modeHint);

        flyChip = Chip(frame, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-40f, -40f), 46f, 20f, PanelColor, "", medium, 19f, Color.white, out flyInfo);
        keysChip = Chip(frame, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(40f, 40f), 46f, 20f, PanelColor, "", medium, 18f, Muted, out keysText);
        plantChip = Chip(frame, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 40f), 50f, 22f, Accent,
            "Enter / Click  plant here", semiBold, 20f, AccentText, out plantText);
        escChip = Chip(frame, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-40f, 100f), 40f, 18f, PanelColor,
            "Esc  cancel", medium, 17f, Muted, out escText);
        Timeline.Build(placing.transform, frame);
        placing.SetActive(false);
    }

    static void BuildRec(Transform root)
    {
        recChip = Chip(root, new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(-32f, -32f), 48f, 22f, PanelColor, "REC 00:00", semiBold, 21f, Color.white, out recText);
        recText.margin = new Vector4(26f, 0f, 0f, 0f);
        var dot = Node(recChip, "Dot");
        Anchor(dot, new Vector2(0f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(26f, 0f), new Vector2(16f, 16f));
        var dotImage = dot.gameObject.AddComponent<Image>();
        dotImage.sprite = MakeRounded(32, 16);
        dotImage.color = RecRed;
        dotImage.raycastTarget = false;
        rec = recChip.gameObject;
        rec.SetActive(false);
    }

    static void BuildPreview(Transform root)
    {
        var box = Node(root, "Preview");
        Anchor(box, new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(-32f, 32f), new Vector2(400f, 262f));
        var back = box.gameObject.AddComponent<Image>();
        back.sprite = rounded;
        back.type = Image.Type.Sliced;
        back.color = PanelColor;
        back.raycastTarget = false;
        var pic = Node(box, "Picture");
        Anchor(pic, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -8f), new Vector2(384f, 216f));
        previewImage = pic.gameObject.AddComponent<RawImage>();
        previewImage.raycastTarget = false;
        var caption = Node(box, "Caption");
        Anchor(caption, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(14f, 6f), new Vector2(372f, 30f));
        previewText = caption.gameObject.AddComponent<TextMeshProUGUI>();
        if (medium != null) previewText.font = medium;
        previewText.fontSize = 16f;
        previewText.color = Muted;
        previewText.alignment = TextAlignmentOptions.MidlineLeft;
        previewText.enableWordWrapping = false;
        previewText.raycastTarget = false;
        preview = box.gameObject;
        preview.SetActive(false);
    }

    static void BuildToast(Transform root)
    {
        toastChip = Chip(root, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 150f), 50f, 24f, PanelColor, "", medium, 20f, Color.white, out toastText);
        toast = toastChip.gameObject;
        toastGroup = toast.AddComponent<CanvasGroup>();
        toastGroup.blocksRaycasts = false;
        toast.SetActive(false);
    }

    // ----- what the driver calls -----

    public static void ShowPlacing(bool on)
    {
        if (!built) Build();
        if (placing == null) return;
        if (on)
        {
            LayoutFrame();
            SetChipText(keysChip, keysText,
                "<color=#3DFF7A>WASD</color> fly   <color=#3DFF7A>Q E</color> down / up   <color=#3DFF7A>Mouse</color> look   <color=#3DFF7A>Scroll</color> speed   " +
                "<color=#3DFF7A>Alt+Scroll</color> zoom   <color=#3DFF7A>Shift</color> fast   <color=#3DFF7A>Ctrl</color> slow   " +
                $"<color=#3DFF7A>{Panel.KeyName(Plugin.ToFlippyKey.Value)}</color> to Flippy", 20f);
        }
        placing.SetActive(on);
    }

    public static void SetMode(int mode)
    {
        if (placing == null) return;
        for (int i = 0; i < 4; i++)
        {
            modeChips[i].color = i == mode ? Accent : Color.clear;
            modeTexts[i].color = i == mode ? AccentText : Muted;
        }
        SetChipText(modeHintChip, modeHint, ModeHints[Math.Clamp(mode, 0, 3)], 16f);
        bool keys = mode == 3; // in mode 4 a click belongs to the timeline
        SetChipText(plantChip, plantText, keys ? "Enter  use this path" : "Enter / Click  plant here", 22f);
        SetChipText(escChip, escText, keys ? "Esc  close, keys are kept" : "Esc  cancel", 18f);
    }

    public static void SetFlyInfo(float fov, float speed)
    {
        if (flyChip == null) return;
        SetChipText(flyChip, flyInfo, $"FOV <color=#3DFF7A>{Mathf.RoundToInt(fov)}°</color>    Speed <color=#3DFF7A>{speed:0.#}</color>", 20f);
    }

    public static void ShowRec(bool on, string info)
    {
        if (!built) Build();
        if (rec == null) return;
        if (info != null) recInfo = info;
        if (on) SetRecTime(0);
        rec.SetActive(on);
    }

    /// The tripod's picture in the corner while recording (it's rendered for the video anyway, so this is free).
    public static void ShowPreview(Texture picture, string caption)
    {
        if (preview == null) return;
        bool on = picture != null;
        if (on)
        {
            float aspect = picture.width / (float)Mathf.Max(1, picture.height);
            previewImage.rectTransform.sizeDelta = aspect >= 384f / 216f ? new Vector2(384f, 384f / aspect) : new Vector2(216f * aspect, 216f);
            previewImage.texture = picture;
            previewText.text = $"Tripod view · {caption}<pos=88%><color=#3DFF7A>live</color>";
        }
        else previewImage.texture = null;
        if (preview.activeSelf != on) preview.SetActive(on);
    }

    public static void SetRecTime(int seconds)
    {
        if (recChip == null) return;
        SetChipText(recChip, recText, $"REC {seconds / 60:00}:{seconds % 60:00}   <color=#ffffffaa><size=80%>{recInfo}</size></color>", 22f, 26f);
    }

    public static void Toast(string text)
    {
        if (!built) Build();
        if (toast == null) return;
        SetChipText(toastChip, toastText, text, 24f);
        toastChip.anchoredPosition = new Vector2(0f, Timeline.Shown ? Timeline.Top + 30f : 150f); // above the keyframe timeline
        toastGroup.alpha = 1f;
        toast.SetActive(true);
        toastUntil = Time.unscaledTime + 3f;
    }

    /// Only the toast fades; everything else is static.
    public static void Tick()
    {
        if (toast == null || !toast.activeSelf) return;
        float left = toastUntil - Time.unscaledTime;
        if (left > 0f) return;
        if (left < -0.4f) { toast.SetActive(false); return; }
        toastGroup.alpha = 1f + left / 0.4f;
    }

    // ----- layout helpers -----

    /// The video's frame on screen: full width with bars above and below on screens taller than the video, full
    /// height with bars at the sides on wider ones.
    static void LayoutFrame()
    {
        float screen = Screen.width / (float)Mathf.Max(1, Screen.height), video = TripodDriver.VideoAspect;
        if (screen <= video)
        {
            float h = screen / video, gap = (1f - h) / 2f;
            frame.anchorMin = new Vector2(0f, gap); frame.anchorMax = new Vector2(1f, 1f - gap);
            barA.anchorMin = Vector2.zero; barA.anchorMax = new Vector2(1f, gap);
            barB.anchorMin = new Vector2(0f, 1f - gap); barB.anchorMax = Vector2.one;
        }
        else
        {
            float w = video / screen, gap = (1f - w) / 2f;
            frame.anchorMin = new Vector2(gap, 0f); frame.anchorMax = new Vector2(1f - gap, 1f);
            barA.anchorMin = Vector2.zero; barA.anchorMax = new Vector2(gap, 1f);
            barB.anchorMin = new Vector2(1f - gap, 0f); barB.anchorMax = Vector2.one;
        }
        foreach (var r in new[] { frame, barA, barB }) { r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero; }
    }

    internal static RectTransform Node(Transform parent, string name)
    {
        var go = new GameObject(name);
        var rt = go.AddComponent<RectTransform>();
        rt.SetParent(parent, false);
        return rt;
    }

    internal static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero; rt.offsetMax = Vector2.zero;
    }

    internal static void Anchor(RectTransform rt, Vector2 anchor, Vector2 pivot, Vector2 pos, Vector2 size)
    {
        rt.anchorMin = rt.anchorMax = anchor;
        rt.pivot = pivot;
        rt.anchoredPosition = pos;
        rt.sizeDelta = size;
    }

    internal static RectTransform Plain(Transform parent, Color color)
    {
        var rt = Node(parent, "Shape");
        var image = rt.gameObject.AddComponent<Image>();
        image.color = color;
        image.raycastTarget = false;
        return rt;
    }

    /// A rounded pill with one line of text, sized to the text.
    internal static RectTransform Chip(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, float height, float pad, Color background,
                              string text, TMP_FontAsset font, float size, Color color, out TextMeshProUGUI label)
    {
        var rt = Node(parent, "Chip");
        Anchor(rt, anchor, pivot, pos, new Vector2(100f, height));
        var image = rt.gameObject.AddComponent<Image>();
        image.sprite = rounded;
        image.type = Image.Type.Sliced;
        image.color = background;
        image.raycastTarget = false;
        var lt = Node(rt, "Label");
        Stretch(lt);
        label = lt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = color;
        label.alignment = TextAlignmentOptions.Center;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        SetChipText(rt, label, text, pad);
        return rt;
    }

    internal static void SetChipText(RectTransform chip, TextMeshProUGUI label, string text, float pad, float extraLeft = 0f)
    {
        if (label.text == text && chip.sizeDelta.x > 1f) return;
        label.text = text;
        float width = string.IsNullOrEmpty(text) ? 0f : label.GetPreferredValues(text).x;
        chip.sizeDelta = new Vector2(width + pad * 2f + extraLeft, chip.sizeDelta.y);
        if (extraLeft > 0f) label.margin = new Vector4(extraLeft, 0f, 0f, 0f);
    }

    // ----- fonts and sprites (from StickerSwap) -----

    /// Geist as a dynamic TMP font; falls back to the game's own font.
    static TMP_FontAsset Font(string weight)
    {
        TMP_FontAsset game = null;
        try
        {
            foreach (var f in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
                if (f != null && (game == null || f.name.StartsWith("Jersey10"))) game = f;
            var folder = Path.Combine(Plugin.ConfigFolder, "fonts");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"Geist-{weight}.ttf");
            if (!File.Exists(path)) File.WriteAllBytes(path, Resource($"Geist-{weight}.ttf"));
            var license = Path.Combine(folder, "OFL-Geist.txt");
            if (!File.Exists(license)) File.WriteAllBytes(license, Resource("OFL-Geist.txt"));
            var font = TMP_FontAsset.CreateFontAsset(path, 0, 72, 6, GlyphRenderMode.SDFAA, 1024, 1024, AtlasPopulationMode.Dynamic, true);
            if (font == null) return game;
            font.name = $"Geist-{weight} (TripodCam)";
            font.hideFlags = HideFlags.HideAndDontSave;
            var shader = font.material != null ? font.material.shader : null;
            if ((shader == null || !shader.isSupported) && game != null && game.material != null) font.material.shader = game.material.shader;
            if (game != null) font.fallbackFontAssetTable?.Add(game);
            return font;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't load Geist {weight}, using the game's font: {e.Message}");
            return game;
        }
    }

    internal static byte[] Resource(string name)
    {
        using var stream = typeof(Overlay).Assembly.GetManifestResourceStream(name);
        using var ms = new MemoryStream();
        stream!.CopyTo(ms);
        return ms.ToArray();
    }

    internal static Sprite MakeRounded(int size, int radius)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float cx = Mathf.Clamp(x + 0.5f, radius, size - radius), cy = Mathf.Clamp(y + 0.5f, radius, size - radius);
            var d = Mathf.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(radius - d + 0.5f) * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var border = new Vector4(radius, radius, radius, radius);
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, border);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
