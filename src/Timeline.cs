using System.Collections.Generic;
using System.Globalization;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TripodCam;

/// The keyframe editor's on-screen parts (placing in mode 4), on the overlay's canvas:
/// - the timeline above the bottom hints: a ruler in seconds, the keys as diamonds, the stretch the path covers, and
///   the playhead with its time and frame number;
/// - the path itself over the world: a dotted line through the keys and a numbered marker at each one.
/// The driver (Keyframes.cs) does the editing; this draws and says what's under the mouse. The ruler and the diamonds
/// are redrawn only when the path, the playhead, the view or the screen changes; the dots follow the camera every frame.
/// The view fits the whole path until you zoom (Ctrl+Scroll over the timeline) or pan (Scroll); from then on it stays
/// where you put it and only moves to keep the playhead in sight.
internal static class Timeline
{
    const float Bottom = 156f, Height = 120f, Margin = 40f, LanePad = 28f;
    const float HeaderY = 24f, LabelY = 52f, TickY = 64f, LaneY = 92f, PlayheadTop = 42f;
    const int PathDots = 90;

    internal struct Hit
    {
        public bool Inside, Lane;
        public float Time;
        public int Key, Edge; // Edge: -1 / 1 when the mouse is at or past the left / right end of the lane
    }

    static RectTransform frame, root, bar, track, span, playhead, pathLayer;
    static Image barBack;
    static TextMeshProUGUI title, clock, hints;
    static Sprite diamond;
    static readonly List<RectTransform> ticks = new();
    static readonly List<TextMeshProUGUI> labels = new();
    static readonly List<Image> diamonds = new();
    static readonly List<RectTransform> dots = new();
    static readonly List<(RectTransform Back, Image Image, TextMeshProUGUI Number)> markers = new();
    static readonly Vector3[] samples = new Vector3[PathDots];
    static readonly float[] pose = new float[KeyPath.Channels];
    static float viewStart, viewSpan = 5f, shownStart = -1f, shownSpan = -1f, shownWidth = -1f, shownTime = -1f, lastTime = -1f;
    static bool zoomed;
    static int shownVersion = -1, shownKey = -2, shownHover = -2, shownFps, sampledVersion = -1, shownMouse = -1;

    public static bool Shown => root != null && root.gameObject.activeSelf;

    /// The bar's top edge, in canvas units from the bottom of the screen.
    public static float Top => frame.anchorMin.y * Screen.height / Scale + Bottom + Height;

    // ---------------------------------------------------------------- build (with the overlay)

    public static void Build(Transform placing, RectTransform videoFrame)
    {
        frame = videoFrame;
        diamond = MakeDiamond(32);

        // the path over the world, under every chip
        pathLayer = Overlay.Node(placing, "Path");
        Overlay.Stretch(pathLayer);
        pathLayer.SetAsFirstSibling();

        root = Overlay.Node(frame, "Timeline");
        Overlay.Stretch(root);
        bar = Overlay.Node(root, "Bar");
        bar.anchorMin = new Vector2(0f, 0f);
        bar.anchorMax = new Vector2(1f, 0f);
        bar.pivot = new Vector2(0.5f, 0f);
        bar.offsetMin = new Vector2(Margin, Bottom);
        bar.offsetMax = new Vector2(-Margin, Bottom + Height);
        barBack = Panel.Fill(bar, Overlay.rounded, Overlay.PanelColor);

        title = Text(bar, new Vector2(0f, 1f), new Vector2(0f, 0.5f), new Vector2(22f, -HeaderY), Overlay.semiBold, 17f, TextAlignmentOptions.MidlineLeft);
        clock = Text(bar, new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, -HeaderY), Overlay.semiBold, 18f, TextAlignmentOptions.Center);
        hints = Text(bar, new Vector2(1f, 1f), new Vector2(1f, 0.5f), new Vector2(-22f, -HeaderY), Overlay.medium, 15f, TextAlignmentOptions.MidlineRight);
        hints.color = Overlay.Muted;

        track = Panel.At(bar, "Track", LanePad, LaneY - 1f, 100f, 2f);
        Panel.Fill(track, null, new Color(1f, 1f, 1f, 0.16f));
        span = Panel.At(bar, "Span", LanePad, LaneY - 3f, 100f, 6f);
        Panel.Fill(span, null, new Color(Overlay.Accent.r, Overlay.Accent.g, Overlay.Accent.b, 0.4f));

        playhead = Panel.At(bar, "Playhead", 0f, PlayheadTop, 0f, 0f);
        var line = Panel.At(playhead, "Line", -1f, 0f, 2f, Height - PlayheadTop - 6f);
        Panel.Fill(line, null, Overlay.Accent);
        var knob = Center(playhead, "Knob", 0f, 0f, 14f);
        var knobImage = knob.gameObject.AddComponent<Image>();
        knobImage.sprite = diamond;
        knobImage.color = Overlay.Accent;
        knobImage.raycastTarget = false;
        playhead.SetAsLastSibling();

        root.gameObject.SetActive(false);
        pathLayer.gameObject.SetActive(false);
    }

    public static void Show(bool on)
    {
        if (root == null) return;
        root.gameObject.SetActive(on);
        pathLayer.gameObject.SetActive(on);
        shownStart = shownSpan = shownWidth = shownTime = -1f;
        shownVersion = shownKey = shownHover = sampledVersion = shownMouse = -2;
    }

    // ---------------------------------------------------------------- per frame while editing

    /// The timeline: holdSpan keeps the view still while something is being dragged.
    public static void Draw(KeyPath path, float time, int fps, bool holdSpan, int hover, bool mouse)
    {
        if (!Shown) return;
        float width = Width();
        if (!zoomed && !holdSpan)
        {
            viewStart = 0f;
            viewSpan = Mathf.Max(5f, Mathf.Ceil(Mathf.Max(path.End, time) + 1f));
        }
        else if (zoomed && !holdSpan && time != lastTime) // the playhead moved: keep it in sight
        {
            if (time < viewStart) viewStart = Mathf.Max(0f, time - viewSpan * 0.1f);
            else if (time > viewStart + viewSpan) viewStart = time - viewSpan * 0.9f;
        }
        lastTime = time;
        bool layout = viewStart != shownStart || viewSpan != shownSpan || Mathf.Abs(width - shownWidth) > 0.5f;
        if (layout)
        {
            shownStart = viewStart;
            shownSpan = viewSpan;
            shownWidth = width;
            Ruler(width);
        }
        int selected = path.Near(time, 0.5f / fps);
        bool picked = selected != shownKey;
        if (layout || picked || path.Version != shownVersion || hover != shownHover)
        {
            shownVersion = path.Version;
            shownKey = selected;
            shownHover = hover;
            Keys(path, width, selected, hover);
            title.text = "<color=#3DFF7A>KEYFRAMES</color>   " +
                         (path.Count == 0 ? "none yet" : $"{path.Count} key{(path.Count == 1 ? "" : "s")} · {Seconds(path.End)} s");
        }
        if (layout || picked || time != shownTime || fps != shownFps)
        {
            shownTime = time;
            shownFps = fps;
            bool inView = time >= viewStart - 1e-4f && time <= viewStart + viewSpan + 1e-4f;
            if (playhead.gameObject.activeSelf != inView) playhead.gameObject.SetActive(inView);
            playhead.anchoredPosition = new Vector2(X(time, width), -PlayheadTop);
            clock.text = $"{Seconds(time)} s   <color=#ffffffaa>frame {Mathf.RoundToInt(time * fps)}</color>" + (selected >= 0 ? $"   <color=#3DFF7A>key {selected + 1}</color>" : "");
        }
        int mouseState = mouse ? 1 : 0;
        if (mouseState != shownMouse)
        {
            shownMouse = mouseState;
            string add = Panel.KeyName(Plugin.KeyframeKey.Value);
            hints.text = mouse
                ? "<color=#3DFF7A>Drag</color> keys or time · <color=#3DFF7A>Right-click</color> delete · <color=#3DFF7A>Scroll</color> move · <color=#3DFF7A>Ctrl+Scroll</color> zoom · <color=#3DFF7A>Click outside</color> or <color=#3DFF7A>Tab</color> fly"
                : $"<color=#3DFF7A>{add}</color> key here · <color=#3DFF7A>Arrows</color> pick · <color=#3DFF7A>Del</color> delete · <color=#3DFF7A>Space</color> play · <color=#3DFF7A>Click</color> use the mouse";
            barBack.color = mouse ? new Color(0.09f, 0.12f, 0.15f, 0.86f) : Overlay.PanelColor;
        }
    }

    /// The path over the world, seen from the placing camera (LateUpdate, after the camera moved).
    public static void DrawPath(KeyPath path, float time, int fps, Vector3 camPos, Quaternion camRot, float screenFov)
    {
        if (!Shown) return;
        int n = path.Count;
        if (path.Version != sampledVersion)
        {
            sampledVersion = path.Version;
            for (int i = 0; i < PathDots && n >= 2; i++)
            {
                path.Sample(path.Start + (path.End - path.Start) * i / (PathDots - 1), pose);
                samples[i] = new Vector3(pose[0], pose[1], pose[2]);
            }
        }
        float scale = Scale, w = Screen.width, h = Mathf.Max(1f, Screen.height);
        var view = Quaternion.Inverse(camRot);
        float tan = Mathf.Tan(Mathf.Clamp(screenFov, 1f, 179f) * 0.5f * Mathf.Deg2Rad), aspect = w / h;

        while (dots.Count < PathDots) dots.Add(MakeDot());
        for (int i = 0; i < PathDots; i++)
        {
            var at = Vector2.zero;
            bool on = n >= 2 && Project(samples[i], camPos, view, tan, aspect, w, h, scale, out at);
            if (on) dots[i].anchoredPosition = at;
            if (dots[i].gameObject.activeSelf != on) dots[i].gameObject.SetActive(on);
        }

        int selected = path.Near(time, 0.5f / fps);
        while (markers.Count < n) markers.Add(MakeMarker());
        for (int i = 0; i < markers.Count; i++)
        {
            var (back, image, number) = markers[i];
            var at = Vector2.zero;
            bool on = i < n && Project(new Vector3(path[i].Pose[0], path[i].Pose[1], path[i].Pose[2]), camPos, view, tan, aspect, w, h, scale, out at);
            if (on)
            {
                back.anchoredPosition = at;
                string label = (i + 1).ToString(CultureInfo.InvariantCulture);
                if (number.text != label) number.text = label;
                image.color = i == selected ? Overlay.Accent : Overlay.PanelColor;
                number.color = i == selected ? Overlay.AccentText : Color.white;
            }
            if (back.gameObject.activeSelf != on) back.gameObject.SetActive(on);
        }
    }

    /// What's under the mouse (screen pixels, from the bottom left like Mouse.position).
    public static Hit HitTest(Vector2 screen, KeyPath path)
    {
        var hit = new Hit { Key = -1 };
        if (!Shown) return hit;
        float width = Width();
        Local(screen, out float x, out float y);
        hit.Inside = x >= 0f && x <= width && y >= 0f && y <= Height;
        hit.Lane = hit.Inside && y >= PlayheadTop - 6f;
        hit.Time = Mathf.Max(0f, TimeAt(x, width));
        hit.Edge = x <= LanePad + 6f ? -1 : x >= width - LanePad - 6f ? 1 : 0;
        if (hit.Inside && Mathf.Abs(y - LaneY) <= 18f)
        {
            float best = 14f;
            for (int i = 0; i < path.Count; i++)
            {
                float gap = Mathf.Abs(X(path[i].Time, width) - x);
                if (gap <= best) { best = gap; hit.Key = i; }
            }
        }
        return hit;
    }

    public static bool Over(Vector2 screen)
    {
        if (!Shown) return false;
        Local(screen, out float x, out float y);
        return x >= 0f && x <= Width() && y >= 0f && y <= Height;
    }

    /// Ctrl+Scroll over the timeline: zoom in (scroll up) or out around the time under the mouse, 1 s to 15 min across.
    public static void Zoom(Vector2 screen, float notches)
    {
        float width = Width();
        Local(screen, out float x, out _);
        float at = Mathf.Max(0f, TimeAt(Mathf.Clamp(x, LanePad, width - LanePad), width));
        float next = Mathf.Clamp(viewSpan * Mathf.Pow(1.25f, -notches), 1f, 900f);
        viewStart = Mathf.Max(0f, at - (at - viewStart) * next / viewSpan);
        viewSpan = next;
        zoomed = true;
    }

    /// Scroll over the timeline: earlier (up) or later (down).
    public static void Pan(float notches) => MoveView(-notches * viewSpan * 0.15f);

    /// Dragging at an end of the lane slides the view that way.
    public static void EdgePan(int dir, float dt) => MoveView(dir * viewSpan * 0.8f * dt);

    /// Slides the view; it stops fitting the path by itself only once it really moved (not when it's already at 0 s).
    static void MoveView(float seconds)
    {
        float start = Mathf.Max(0f, viewStart + seconds);
        if (start == viewStart) return;
        viewStart = start;
        zoomed = true;
    }

    // ---------------------------------------------------------------- drawing

    static void Ruler(float width)
    {
        float step = 1f;
        foreach (var s in new[] { 0.1f, 0.25f, 0.5f, 1f, 2f, 5f, 10f, 15f, 30f, 60f, 120f, 300f })
        {
            step = s;
            if (viewSpan / s <= 16f) break;
        }
        float end = viewStart + viewSpan;
        int used = 0, labelled = 0, first = Mathf.CeilToInt(viewStart / step - 1e-4f);
        if ((first - 0.5f) * step >= viewStart) Tick(ref used, X((first - 0.5f) * step, width), 6f);
        for (int k = first; k * step <= end + 1e-3f; k++)
        {
            float t = k * step;
            Tick(ref used, X(t, width), 12f);
            var label = labels.Count > labelled ? labels[labelled] : NewLabel();
            labelled++;
            label.rectTransform.anchoredPosition = new Vector2(X(t, width), -LabelY);
            label.text = t >= 60f && step >= 1f ? $"{(int)t / 60}:{(int)t % 60:00}" : t.ToString(step < 1f ? "0.##" : "0", CultureInfo.InvariantCulture) + "s";
            if (!label.gameObject.activeSelf) label.gameObject.SetActive(true);
            if ((k + 0.5f) * step <= end) Tick(ref used, X((k + 0.5f) * step, width), 6f);
        }
        for (int i = used; i < ticks.Count; i++) ticks[i].gameObject.SetActive(false);
        for (int i = labelled; i < labels.Count; i++) labels[i].gameObject.SetActive(false);
        track.sizeDelta = new Vector2(width - 2f * LanePad, 2f);
    }

    static void Tick(ref int used, float x, float height)
    {
        if (ticks.Count <= used)
        {
            var t = Panel.At(bar, "Tick", 0f, 0f, 2f, 2f);
            Panel.Fill(t, null, new Color(1f, 1f, 1f, 0.35f));
            ticks.Add(t);
        }
        var tick = ticks[used++];
        tick.anchoredPosition = new Vector2(x - 1f, -(TickY + 12f - height));
        tick.sizeDelta = new Vector2(2f, height);
        if (!tick.gameObject.activeSelf) tick.gameObject.SetActive(true);
    }

    static TextMeshProUGUI NewLabel()
    {
        var label = Text(bar, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), Vector2.zero, Overlay.medium, 13f, TextAlignmentOptions.Center);
        label.color = new Color(1f, 1f, 1f, 0.55f);
        labels.Add(label);
        return label;
    }

    static void Keys(KeyPath path, float width, int selected, int hover)
    {
        while (diamonds.Count < path.Count)
        {
            var image = Center(bar, "Key", 0f, 0f, 18f).gameObject.AddComponent<Image>();
            image.sprite = diamond;
            image.raycastTarget = false;
            diamonds.Add(image);
        }
        float end = viewStart + viewSpan;
        for (int i = 0; i < diamonds.Count; i++)
        {
            var image = diamonds[i];
            bool on = i < path.Count && path[i].Time >= viewStart - 1e-4f && path[i].Time <= end + 1e-4f;
            if (image.gameObject.activeSelf != on) image.gameObject.SetActive(on);
            if (!on) continue;
            float size = i == selected ? 26f : i == hover ? 24f : 19f;
            image.rectTransform.anchoredPosition = new Vector2(X(path[i].Time, width), -LaneY);
            image.rectTransform.sizeDelta = new Vector2(size, size);
            image.color = i == selected ? Overlay.Accent : Color.white;
        }
        if (selected >= 0) diamonds[selected].transform.SetAsLastSibling();
        playhead.SetAsLastSibling();
        float a = X(Mathf.Max(path.Start, viewStart), width), b = X(Mathf.Min(path.End, end), width);
        bool stretch = path.Count >= 2 && b > a;
        span.gameObject.SetActive(stretch);
        if (stretch)
        {
            span.anchoredPosition = new Vector2(a, -(LaneY - 3f));
            span.sizeDelta = new Vector2(b - a, 6f);
        }
    }

    /// World point to the canvas (the path layer covers the whole screen, from the bottom left).
    static bool Project(Vector3 p, Vector3 camPos, Quaternion view, float tan, float aspect, float w, float h, float scale, out Vector2 at)
    {
        var v = view * (p - camPos);
        at = default;
        if (v.z < 0.2f) return false;
        float nx = v.x / (v.z * tan * aspect), ny = v.y / (v.z * tan);
        if (nx < -1.2f || nx > 1.2f || ny < -1.2f || ny > 1.2f) return false;
        at = new Vector2((nx + 1f) * 0.5f * w / scale, (ny + 1f) * 0.5f * h / scale);
        return true;
    }

    static RectTransform MakeDot()
    {
        var dot = Overlay.Node(pathLayer, "Dot");
        Overlay.Anchor(dot, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(7f, 7f));
        var image = dot.gameObject.AddComponent<Image>();
        image.sprite = Overlay.circle;
        image.color = new Color(1f, 1f, 1f, 0.7f);
        image.raycastTarget = false;
        dot.gameObject.SetActive(false);
        return dot;
    }

    static (RectTransform, Image, TextMeshProUGUI) MakeMarker()
    {
        var back = Overlay.Node(pathLayer, "Marker");
        Overlay.Anchor(back, Vector2.zero, new Vector2(0.5f, 0.5f), Vector2.zero, new Vector2(30f, 30f));
        var image = back.gameObject.AddComponent<Image>();
        image.sprite = Overlay.circle;
        image.raycastTarget = false;
        var number = Text(back, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), Vector2.zero, Overlay.semiBold, 15f, TextAlignmentOptions.Center);
        back.gameObject.SetActive(false);
        return (back, image, number);
    }

    // ---------------------------------------------------------------- helpers

    static float Scale
    {
        get
        {
            float s = Overlay.canvas != null ? Overlay.canvas.scaleFactor : 1f;
            return s > 0.01f ? s : 1f;
        }
    }

    /// The bar's width in canvas units: the video frame's width less the margins.
    static float Width() => Mathf.Max(200f, (frame.anchorMax.x - frame.anchorMin.x) * Screen.width / Scale - 2f * Margin);

    static float X(float time, float width) => LanePad + (time - viewStart) / viewSpan * (width - 2f * LanePad);

    static float TimeAt(float x, float width) => viewStart + (x - LanePad) / Mathf.Max(1f, width - 2f * LanePad) * viewSpan;

    /// Screen pixels to the bar's own units: x from its left edge, y from its top edge going down.
    static void Local(Vector2 screen, out float x, out float y)
    {
        float scale = Scale;
        float left = frame.anchorMin.x * Screen.width + Margin * scale, bottom = frame.anchorMin.y * Screen.height + Bottom * scale;
        x = (screen.x - left) / scale;
        y = Height - (screen.y - bottom) / scale;
    }

    static string Seconds(float t) => t.ToString("0.00", CultureInfo.InvariantCulture);

    static RectTransform Center(Transform parent, string name, float x, float y, float size)
    {
        var rt = Overlay.Node(parent, name);
        Overlay.Anchor(rt, new Vector2(0f, 1f), new Vector2(0.5f, 0.5f), new Vector2(x, y), new Vector2(size, size));
        return rt;
    }

    static TextMeshProUGUI Text(Transform parent, Vector2 anchor, Vector2 pivot, Vector2 pos, TMP_FontAsset font, float size, TextAlignmentOptions align)
    {
        var rt = Overlay.Node(parent, "Text");
        Overlay.Anchor(rt, anchor, pivot, pos, new Vector2(640f, 30f));
        var label = rt.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) label.font = font;
        label.fontSize = size;
        label.color = Color.white;
        label.alignment = align;
        label.enableWordWrapping = false;
        label.overflowMode = TextOverflowModes.Overflow;
        label.raycastTarget = false;
        return label;
    }

    /// A soft-edged diamond: a square turned 45°.
    static Sprite MakeDiamond(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var pixels = new Color32[size * size];
        float c = size / 2f, r = c - 1f;
        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            float d = (Mathf.Abs(x + 0.5f - c) + Mathf.Abs(y + 0.5f - c) - r) * 0.7071f;
            pixels[y * size + x] = new Color32(255, 255, 255, (byte)(Mathf.Clamp01(0.5f - d) * 255));
        }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        tex.hideFlags = HideFlags.HideAndDontSave;
        var sprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), 100f, 0, SpriteMeshType.FullRect, Vector4.zero);
        sprite.hideFlags = HideFlags.HideAndDontSave;
        return sprite;
    }
}
