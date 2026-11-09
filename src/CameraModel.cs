using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace TripodCam;

/// The camera you see in the world: a little cinema camera (lens, matte box, top handle, flip-out
/// monitor, blinking REC light) on a tripod (fluid head, pan bar, column, three legs that reach down to the ground).
/// - Built from code (boxes, cylinders and spheres), no game art. The meshes are made once and shared by every rig.
/// - The head turns with the shot (yaw), the camera tilts on it (pitch); the legs stay upright.
/// - Solid while recording; see-through (a "ghost", GhostAlpha) while the tripod is planted but not recording. The
///   ghost uses its own transparent copies of the materials (URP Lit's transparent surface), casts no shadow and its
///   REC light is off.
/// - Never in Tripod Cam's own videos: every part sits on a layer the game camera draws and the tripod camera leaves out
///   (an unused one). If no such layer exists, the parts are switched off just for the tripod camera's render.
/// - No colliders, so nothing in the game bumps into it (the game camera only collides with layer 10 anyway).
internal static class CameraModel
{
    internal enum Mat { Body, Rubber, Metal, Glass, Accent, Rec, Screen }
    const int MatCount = 7;
    const float Splay = 24f; // legs, degrees from straight down
    const float DefaultLeg = 1.2f; // with no ground below (a tripod in the air)

    // the tilt pivot under the camera, from the lens (camera space: +Z where it films)
    static readonly Vector3 Pivot = new(0f, -0.26f, -0.36f);
    static readonly Vector3 Hub = new(0f, -0.33f, 0f); // legs start here, from the pivot

    static Material[] mats, ghostMats;
    static readonly Color[] baseColors = new Color[MatCount];
    static float ghostAlphaUsed = -1f;

    /// How see-through the ghost is (0..1 opacity); the driver sets it from the config, the ghost follows on the next Tick.
    internal static float GhostAlpha = 0.35f;
    static Mesh[] bodyMeshes, headMeshes, legMeshes, footMeshes;
    static bool failed;
    static int hiddenLayer = -2; // -2 not picked yet, -1 none found
    static readonly List<Rig> rigs = new();
    static float blinkAt;
    static bool blinkOn;

    /// The layer the tripod camera must leave out, picked once from the game camera (and the sun, so the model stays
    /// lit): an unnamed layer both of them include. -1 when there's none (then RenderHide does the job).
    internal static int HiddenLayer(Camera game)
    {
        if (hiddenLayer != -2 || game == null) return hiddenLayer;
        hiddenLayer = -1;
        try
        {
            int camMask = game.cullingMask, sunMask = -1;
            var sun = RenderSettings.sun;
            if (sun != null) sunMask = sun.cullingMask;
            for (int i = 31; i >= 8; i--)
            {
                int bit = 1 << i;
                if ((camMask & bit) == 0 || (sunMask & bit) == 0) continue;
                if (!string.IsNullOrEmpty(LayerMask.LayerToName(i))) continue;
                hiddenLayer = i;
                break;
            }
            Plugin.Logger.LogInfo(hiddenLayer >= 0
                ? $"Camera model on layer {hiddenLayer}, which the tripod camera leaves out"
                : "No free layer for the camera model: it's switched off during the tripod camera's render instead");
            foreach (var r in rigs) r.SetLayer(Math.Max(0, hiddenLayer));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning("couldn't pick a layer for the camera model: " + e.Message);
        }
        return hiddenLayer;
    }

    static int Layer => hiddenLayer >= 0 ? hiddenLayer : 0;

    /// True when your own game camera is right at (or inside) the camera model, as with a tripod planted with the
    /// to-Flippy key and following: then it hides, so it never blocks your view.
    internal static bool TooClose(Vector3 camPos, float size)
    {
        var manager = TripodDriver.FindManager();
        if (manager == null || manager.cam == null) return false;
        float near = 1.6f * Mathf.Clamp(size, 0.25f, 4f);
        return (manager.cam.transform.position - camPos).sqrMagnitude < near * near;
    }

    /// When no layer could be left out: hide every rig for the tripod camera's render, show them again after it.
    internal static bool NeedsRenderHide => hiddenLayer == -1 && rigs.Count > 0;

    internal static void RenderHide(bool hide)
    {
        foreach (var r in rigs) r.RenderHide(hide);
    }

    /// Once a frame: the REC light blinks (shared material), and the ghost follows a changed opacity.
    internal static void Tick()
    {
        if (mats == null || rigs.Count == 0) return;
        float alpha = Mathf.Clamp01(GhostAlpha);
        if (ghostMats != null && Mathf.Abs(alpha - ghostAlphaUsed) > 0.001f)
        {
            ghostAlphaUsed = alpha;
            for (int i = 0; i < MatCount; i++)
                if (ghostMats[i] != null) Tint(ghostMats[i], new Color(baseColors[i].r, baseColors[i].g, baseColors[i].b, alpha));
        }
        float now = Time.unscaledTime;
        if (now < blinkAt) return;
        blinkAt = now + 0.5f;
        blinkOn = !blinkOn;
        var m = mats[(int)Mat.Rec];
        if (m == null) return;
        var c = blinkOn ? new Color(1f, 0.08f, 0.06f, 1f) : new Color(0.25f, 0.03f, 0.03f, 1f);
        Tint(m, c);
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", blinkOn ? new Color(4f, 0.25f, 0.2f, 1f) : Color.black);
    }

    // ---------------------------------------------------------------- one camera on a tripod

    internal sealed class Rig
    {
        readonly GameObject root;
        readonly Transform body;
        readonly Transform[] legs = new Transform[3], feet = new Transform[3];
        readonly Vector3[] legDirs = new Vector3[3];
        readonly List<Renderer> renderers = new();
        readonly List<int> matIndex = new();
        float legLength = -1f;
        bool shown, renderHidden, ghost;

        public bool Alive => root != null;

        public Rig(string name)
        {
            Build();
            root = new GameObject(name);
            if (Application.isPlaying) Object.DontDestroyOnLoad(root); // (the offline preview renders it in the editor)
            root.hideFlags = HideFlags.HideAndDontSave;
            if (failed) return;
            Parts(root.transform, headMeshes);
            body = new GameObject("Camera").transform;
            body.SetParent(root.transform, false);
            Parts(body, bodyMeshes);
            float s = Mathf.Sin(Splay * Mathf.Deg2Rad), c = Mathf.Cos(Splay * Mathf.Deg2Rad);
            float[] angles = { 90f, 210f, 330f }; // one leg under the lens, two at the back
            for (int i = 0; i < 3; i++)
            {
                float a = angles[i] * Mathf.Deg2Rad;
                legDirs[i] = new Vector3(s * Mathf.Cos(a), -c, s * Mathf.Sin(a));
                var leg = new GameObject("Leg").transform;
                leg.SetParent(root.transform, false);
                leg.localPosition = Hub + new Vector3(Mathf.Cos(a), 0f, Mathf.Sin(a)) * 0.055f;
                leg.localRotation = Quaternion.FromToRotation(Vector3.down, legDirs[i]);
                Parts(leg, legMeshes);
                legs[i] = leg;
                var foot = new GameObject("Foot").transform;
                foot.SetParent(root.transform, false);
                Parts(foot, footMeshes);
                feet[i] = foot;
            }
            root.SetActive(false);
            rigs.Add(this);
        }

        void Parts(Transform parent, Mesh[] meshes)
        {
            for (int m = 0; m < MatCount; m++)
            {
                if (meshes[m] == null) continue;
                var go = new GameObject(((Mat)m).ToString());
                go.transform.SetParent(parent, false);
                go.layer = Layer;
                go.AddComponent<MeshFilter>().sharedMesh = meshes[m];
                var mr = go.AddComponent<MeshRenderer>();
                mr.sharedMaterial = mats[m];
                mr.shadowCastingMode = m == (int)Mat.Glass || m == (int)Mat.Screen ? ShadowCastingMode.Off : ShadowCastingMode.On;
                mr.receiveShadows = true;
                renderers.Add(mr);
                matIndex.Add(m);
            }
        }

        /// See-through (planted, not recording) or solid (recording).
        public void SetGhost(bool on)
        {
            if (on == ghost || failed) return;
            ghost = on;
            for (int i = 0; i < renderers.Count; i++)
            {
                var r = renderers[i];
                if (r == null) continue;
                int m = matIndex[i];
                r.sharedMaterial = on && ghostMats[m] != null ? ghostMats[m] : mats[m];
                bool casts = !on && m != (int)Mat.Glass && m != (int)Mat.Screen;
                r.shadowCastingMode = casts ? ShadowCastingMode.On : ShadowCastingMode.Off;
            }
        }

        internal void SetLayer(int layer)
        {
            foreach (var r in renderers) if (r != null) r.gameObject.layer = layer;
        }

        internal void RenderHide(bool hide)
        {
            if (!shown || hide == renderHidden) return;
            renderHidden = hide;
            foreach (var r in renderers) if (r != null) r.enabled = !hide;
        }

        public void Show(bool on)
        {
            if (root == null || on == shown) return;
            shown = on;
            root.SetActive(on);
            if (!on) legLength = -1f; // next time the legs start at the ground right away
        }

        /// Puts the lens at `camPos`, filming along `camRot`; `size` scales the whole thing.
        public void Place(Vector3 camPos, Quaternion camRot, float size, float dt)
        {
            if (root == null || failed) return;
            size = Mathf.Clamp(size, 0.25f, 4f);
            var f = camRot * Vector3.forward;
            float yaw = Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
            float pitch = Mathf.Clamp(-Mathf.Asin(Mathf.Clamp(f.y, -1f, 1f)) * Mathf.Rad2Deg, -80f, 80f);
            var yawRot = Quaternion.Euler(0f, yaw, 0f);
            var pivot = camPos + yawRot * (Pivot * size);
            root.transform.SetPositionAndRotation(pivot, yawRot);
            root.transform.localScale = new Vector3(size, size, size);
            body.localRotation = Quaternion.Euler(pitch, 0f, 0f);

            // legs down to whatever is under the tripod (not players, not ourselves)
            var hub = pivot + Vector3.down * (-Hub.y * size);
            float want = DefaultLeg;
            const int skip = (1 << 2) | (1 << 5) | (1 << 6) | (1 << 8); // Ignore Raycast, UI, Player, PhoneDebris
            int mask = ~(skip | (hiddenLayer >= 0 ? 1 << hiddenLayer : 0));
            if (Physics.Raycast(hub, Vector3.down, out var hit, 12f * size, mask, QueryTriggerInteraction.Ignore))
                want = Mathf.Clamp(hit.distance / size / Mathf.Cos(Splay * Mathf.Deg2Rad), 0.15f, 12f);
            legLength = legLength < 0f ? want : Mathf.Lerp(legLength, want, 1f - Mathf.Exp(-Mathf.Max(dt, 0f) * 12f));
            for (int i = 0; i < 3; i++)
            {
                legs[i].localScale = new Vector3(1f, legLength, 1f);
                feet[i].localPosition = legs[i].localPosition + legDirs[i] * legLength;
            }
        }

        public void Destroy()
        {
            rigs.Remove(this);
            if (root != null) Object.Destroy(root);
        }
    }

    // ---------------------------------------------------------------- materials and meshes (once)

    static void Build()
    {
        if (mats != null || failed) return;
        try
        {
            var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Universal Render Pipeline/Simple Lit");
            shader ??= Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Sprites/Default");
            mats = new Material[MatCount];
            mats[(int)Mat.Body] = MakeMat(shader, "Body", new Color(0.15f, 0.16f, 0.18f, 1f), 0.15f, 0.5f);
            mats[(int)Mat.Rubber] = MakeMat(shader, "Rubber", new Color(0.045f, 0.045f, 0.05f, 1f), 0f, 0.25f);
            mats[(int)Mat.Metal] = MakeMat(shader, "Metal", new Color(0.62f, 0.64f, 0.68f, 1f), 0.9f, 0.65f);
            mats[(int)Mat.Glass] = MakeMat(shader, "Glass", new Color(0.05f, 0.08f, 0.14f, 1f), 0.4f, 0.97f);
            mats[(int)Mat.Accent] = MakeMat(shader, "Accent", new Color(0.24f, 1f, 0.48f, 1f), 0f, 0.5f, new Color(0.1f, 0.45f, 0.2f, 1f));
            mats[(int)Mat.Rec] = MakeMat(shader, "Rec", new Color(1f, 0.08f, 0.06f, 1f), 0f, 0.6f, new Color(4f, 0.25f, 0.2f, 1f));
            mats[(int)Mat.Screen] = MakeMat(shader, "Screen", new Color(0.08f, 0.2f, 0.17f, 1f), 0f, 0.9f, new Color(0.12f, 0.45f, 0.32f, 1f));

            ghostMats = new Material[MatCount];
            for (int i = 0; i < MatCount; i++)
            {
                var g = new Material(mats[i]) { name = mats[i].name + " (ghost)", hideFlags = HideFlags.HideAndDontSave };
                if (i == (int)Mat.Rec) { baseColors[i] = new Color(0.3f, 0.04f, 0.04f, 1f); NoEmission(g); }
                if (i == (int)Mat.Screen && g.HasProperty("_EmissionColor")) g.SetColor("_EmissionColor", new Color(0.05f, 0.18f, 0.13f, 1f));
                Transparent(g);
                ghostMats[i] = g;
            }

            var b = new Builder();
            BodyShape(b);
            bodyMeshes = b.Meshes("body");
            b = new Builder();
            HeadShape(b);
            headMeshes = b.Meshes("head");
            b = new Builder();
            b.Cylinder(Vector3.zero, new Vector3(0f, -0.55f, 0f), 0.022f, 0.022f, 10, Mat.Rubber, true);
            b.Cylinder(new Vector3(0f, -0.55f, 0f), new Vector3(0f, -0.59f, 0f), 0.029f, 0.029f, 10, Mat.Accent, true);
            b.Cylinder(new Vector3(0f, -0.59f, 0f), new Vector3(0f, -1f, 0f), 0.016f, 0.016f, 10, Mat.Metal, true);
            legMeshes = b.Meshes("leg");
            b = new Builder();
            b.Sphere(Vector3.zero, 0.032f, 8, 6, Mat.Rubber);
            footMeshes = b.Meshes("foot");
            Plugin.Logger.LogInfo($"Camera model ready ({shader?.name ?? "no shader"})");
        }
        catch (Exception e)
        {
            failed = true;
            Plugin.Logger.LogError("couldn't build the camera model: " + e);
        }
    }

    /// The camera, in camera space (origin at the lens mount, +Z where it films), shifted so the tilt pivot is the origin.
    static void BodyShape(Builder b)
    {
        b.Offset = -Pivot;
        // body, with a green line down both sides
        b.Box(new Vector3(0f, -0.02f, -0.36f), new Vector3(0.28f, 0.34f, 0.62f), Mat.Body);
        b.Box(new Vector3(0.1415f, 0.06f, -0.38f), new Vector3(0.004f, 0.022f, 0.48f), Mat.Accent);
        b.Box(new Vector3(-0.1415f, 0.06f, -0.38f), new Vector3(0.004f, 0.022f, 0.48f), Mat.Accent);
        b.Box(new Vector3(0f, -0.02f, -0.052f), new Vector3(0.24f, 0.3f, 0.01f), Mat.Rubber); // front plate
        // lens: mount, barrel with a green focus ring, flared front, glass, matte box
        b.Cylinder(new Vector3(0f, 0f, -0.06f), new Vector3(0f, 0f, -0.02f), 0.105f, 0.105f, 24, Mat.Metal, true);
        b.Cylinder(new Vector3(0f, 0f, -0.02f), new Vector3(0f, 0f, 0.2f), 0.09f, 0.09f, 24, Mat.Rubber, false);
        b.Cylinder(new Vector3(0f, 0f, 0.06f), new Vector3(0f, 0f, 0.1f), 0.095f, 0.095f, 24, Mat.Accent, true);
        b.Cylinder(new Vector3(0f, 0f, 0.13f), new Vector3(0f, 0f, 0.15f), 0.094f, 0.094f, 24, Mat.Metal, true);
        b.Cylinder(new Vector3(0f, 0f, 0.2f), new Vector3(0f, 0f, 0.25f), 0.095f, 0.11f, 24, Mat.Rubber, false);
        b.Cylinder(new Vector3(0f, 0f, 0.236f), new Vector3(0f, 0f, 0.244f), 0.1f, 0.1f, 24, Mat.Glass, true);
        b.Hood(0.25f, 0.37f, 0.125f, 0.095f, 0.18f, 0.13f, Mat.Rubber);
        // top handle on two posts
        b.Box(new Vector3(0f, 0.255f, -0.31f), new Vector3(0.05f, 0.04f, 0.44f), Mat.Rubber);
        b.Box(new Vector3(0f, 0.195f, -0.48f), new Vector3(0.035f, 0.09f, 0.035f), Mat.Metal);
        b.Box(new Vector3(0f, 0.195f, -0.14f), new Vector3(0.035f, 0.09f, 0.035f), Mat.Metal);
        // REC light on the front, a little tally light on the back
        b.Box(new Vector3(0.085f, 0.115f, -0.044f), new Vector3(0.035f, 0.022f, 0.012f), Mat.Rec);
        b.Box(new Vector3(0.09f, 0.12f, -0.674f), new Vector3(0.025f, 0.018f, 0.01f), Mat.Rec);
        // viewfinder and battery at the back
        b.Cylinder(new Vector3(-0.08f, 0.09f, -0.66f), new Vector3(-0.08f, 0.09f, -0.8f), 0.034f, 0.034f, 14, Mat.Rubber, true);
        b.Cylinder(new Vector3(-0.08f, 0.09f, -0.8f), new Vector3(-0.08f, 0.09f, -0.85f), 0.036f, 0.05f, 14, Mat.Rubber, true);
        b.Box(new Vector3(0.03f, -0.05f, -0.71f), new Vector3(0.19f, 0.2f, 0.08f), Mat.Rubber);
        b.Box(new Vector3(0.03f, 0.055f, -0.71f), new Vector3(0.12f, 0.012f, 0.06f), Mat.Accent);
        // flip-out monitor on the left, turned towards the back
        var tilt = Quaternion.Euler(0f, -20f, 0f);
        b.Box(new Vector3(-0.15f, 0.05f, -0.12f), new Vector3(0.03f, 0.035f, 0.03f), Mat.Metal);
        b.Box(new Vector3(-0.25f, 0.05f, -0.125f), new Vector3(0.2f, 0.13f, 0.016f), Mat.Rubber, tilt);
        b.Box(new Vector3(-0.25f, 0.05f, -0.125f) + tilt * new Vector3(0f, 0f, -0.0085f), new Vector3(0.18f, 0.11f, 0.002f), Mat.Screen, tilt);
        // base plate, tilt bracket, pan bar with a grip
        b.Box(new Vector3(0f, -0.205f, -0.36f), new Vector3(0.2f, 0.03f, 0.4f), Mat.Metal);
        b.Box(new Vector3(0f, -0.235f, -0.36f), new Vector3(0.13f, 0.035f, 0.15f), Mat.Rubber);
        b.Cylinder(new Vector3(-0.05f, -0.255f, -0.42f), new Vector3(-0.17f, -0.33f, -0.95f), 0.014f, 0.014f, 10, Mat.Metal, true);
        b.Cylinder(new Vector3(-0.17f, -0.33f, -0.95f), new Vector3(-0.2f, -0.35f, -1.12f), 0.023f, 0.023f, 10, Mat.Rubber, true);
    }

    /// The fluid head, column and leg hub (from the pivot; turns with the shot but never tilts).
    static void HeadShape(Builder b)
    {
        b.Sphere(Vector3.zero, 0.058f, 14, 8, Mat.Metal);
        b.Cylinder(new Vector3(0f, -0.03f, 0f), new Vector3(0f, -0.1f, 0f), 0.07f, 0.065f, 20, Mat.Rubber, true);
        b.Cylinder(new Vector3(0f, -0.1f, 0f), new Vector3(0f, -0.31f, 0f), 0.024f, 0.024f, 12, Mat.Metal, false);
        b.Cylinder(new Vector3(0f, -0.3f, 0f), new Vector3(0f, -0.37f, 0f), 0.075f, 0.068f, 20, Mat.Rubber, true);
        b.Cylinder(new Vector3(0f, -0.328f, 0f), new Vector3(0f, -0.338f, 0f), 0.078f, 0.078f, 20, Mat.Accent, true);
    }

    static Material MakeMat(Shader shader, string name, Color color, float metallic, float smoothness, Color? emission = null)
    {
        var m = new Material(shader) { name = "TripodCam model " + name, hideFlags = HideFlags.HideAndDontSave };
        baseColors[(int)Enum.Parse(typeof(Mat), name)] = color;
        Tint(m, color);
        if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", metallic);
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", smoothness);
        if (emission.HasValue && m.HasProperty("_EmissionColor"))
        {
            m.EnableKeyword("_EMISSION");
            m.SetColor("_EmissionColor", emission.Value);
        }
        return m;
    }

    /// URP Lit's transparent surface (what its inspector sets for Surface Type: Transparent, Blend: Alpha). Depth is
    /// still written, so the ghost shows its outside only, not the parts inside it.
    static void Transparent(Material m)
    {
        if (m.HasProperty("_Surface")) m.SetFloat("_Surface", 1f);
        if (m.HasProperty("_Blend")) m.SetFloat("_Blend", 0f);
        if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
        if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_SrcBlendAlpha")) m.SetFloat("_SrcBlendAlpha", (float)BlendMode.One);
        if (m.HasProperty("_DstBlendAlpha")) m.SetFloat("_DstBlendAlpha", (float)BlendMode.OneMinusSrcAlpha);
        if (m.HasProperty("_ZWrite")) m.SetFloat("_ZWrite", 1f);
        m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        m.SetOverrideTag("RenderType", "Transparent");
        m.renderQueue = (int)RenderQueue.Transparent;
    }

    static void NoEmission(Material m)
    {
        m.DisableKeyword("_EMISSION");
        if (m.HasProperty("_EmissionColor")) m.SetColor("_EmissionColor", Color.black);
    }

    static void Tint(Material m, Color c)
    {
        if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", c);
        if (m.HasProperty("_Color")) m.SetColor("_Color", c);
    }

    // ---------------------------------------------------------------- a tiny mesh builder

    /// Collects triangles per material; every face's winding follows its normal, so nothing ends up inside out.
    sealed class Builder
    {
        readonly List<Vector3>[] v = new List<Vector3>[MatCount], n = new List<Vector3>[MatCount];
        readonly List<int>[] t = new List<int>[MatCount];
        public Vector3 Offset;

        public Builder()
        {
            for (int i = 0; i < MatCount; i++) { v[i] = new(); n[i] = new(); t[i] = new(); }
        }

        int Vert(Mat m, Vector3 p, Vector3 normal)
        {
            v[(int)m].Add(p + Offset);
            n[(int)m].Add(normal);
            return v[(int)m].Count - 1;
        }

        void Tri(Mat m, int a, int b, int c)
        {
            var vs = v[(int)m];
            var face = Vector3.Cross(vs[b] - vs[a], vs[c] - vs[a]);
            var ns = n[(int)m];
            var want = ns[a] + ns[b] + ns[c];
            var list = t[(int)m];
            if (Vector3.Dot(face, want) >= 0f) { list.Add(a); list.Add(b); list.Add(c); }
            else { list.Add(a); list.Add(c); list.Add(b); }
        }

        void Quad(Mat m, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, Vector3 normal)
        {
            int a = Vert(m, p0, normal), b = Vert(m, p1, normal), c = Vert(m, p2, normal), d = Vert(m, p3, normal);
            Tri(m, a, b, c);
            Tri(m, a, c, d);
        }

        public void Box(Vector3 center, Vector3 size, Mat m, Quaternion? rotation = null)
        {
            var q = rotation ?? Quaternion.identity;
            var h = size * 0.5f;
            Vector3 P(float x, float y, float z) => center + q * new Vector3(x * h.x, y * h.y, z * h.z);
            Quad(m, P(1, -1, -1), P(1, 1, -1), P(1, 1, 1), P(1, -1, 1), q * Vector3.right);
            Quad(m, P(-1, -1, -1), P(-1, -1, 1), P(-1, 1, 1), P(-1, 1, -1), q * Vector3.left);
            Quad(m, P(-1, 1, -1), P(-1, 1, 1), P(1, 1, 1), P(1, 1, -1), q * Vector3.up);
            Quad(m, P(-1, -1, -1), P(1, -1, -1), P(1, -1, 1), P(-1, -1, 1), q * Vector3.down);
            Quad(m, P(-1, -1, 1), P(1, -1, 1), P(1, 1, 1), P(-1, 1, 1), q * Vector3.forward);
            Quad(m, P(-1, -1, -1), P(-1, 1, -1), P(1, 1, -1), P(1, -1, -1), q * Vector3.back);
        }

        /// A round tube from a (radius r0) to b (radius r1), smooth sides, optional flat ends.
        public void Cylinder(Vector3 a, Vector3 b, float r0, float r1, int segments, Mat m, bool caps)
        {
            var axis = b - a;
            float length = axis.magnitude;
            if (length < 1e-5f) return;
            var d = axis / length;
            var u = Vector3.Cross(d, Mathf.Abs(d.y) < 0.9f ? Vector3.up : Vector3.right).normalized;
            var w = Vector3.Cross(d, u);
            for (int i = 0; i <= segments; i++)
            {
                float angle = i * Mathf.PI * 2f / segments;
                var dir = u * Mathf.Cos(angle) + w * Mathf.Sin(angle);
                var normal = (dir * length + d * (r0 - r1)).normalized;
                int i0 = Vert(m, a + dir * r0, normal), i1 = Vert(m, b + dir * r1, normal);
                if (i > 0) { Tri(m, i0 - 2, i1 - 2, i1); Tri(m, i0 - 2, i1, i0); }
            }
            if (!caps) return;
            foreach (var (center, r, normal) in new[] { (a, r0, -d), (b, r1, d) })
            {
                int c = Vert(m, center, normal), start = v[(int)m].Count;
                for (int i = 0; i < segments; i++)
                {
                    float angle = i * Mathf.PI * 2f / segments;
                    Vert(m, center + (u * Mathf.Cos(angle) + w * Mathf.Sin(angle)) * r, normal);
                }
                for (int i = 0; i < segments; i++) Tri(m, c, start + i, start + (i + 1) % segments);
            }
        }

        public void Sphere(Vector3 center, float r, int around, int rings, Mat m)
        {
            int start = v[(int)m].Count;
            for (int y = 0; y <= rings; y++)
            {
                float lat = Mathf.PI * y / rings;
                for (int x = 0; x <= around; x++)
                {
                    float lon = Mathf.PI * 2f * x / around;
                    var dir = new Vector3(Mathf.Sin(lat) * Mathf.Cos(lon), Mathf.Cos(lat), Mathf.Sin(lat) * Mathf.Sin(lon));
                    Vert(m, center + dir * r, dir);
                }
            }
            for (int y = 0; y < rings; y++)
                for (int x = 0; x < around; x++)
                {
                    int a = start + y * (around + 1) + x, b = a + around + 1;
                    if (y > 0) Tri(m, a, b, a + 1);
                    if (y < rings - 1) Tri(m, a + 1, b, b + 1);
                }
        }

        /// The matte box: an open, flared rectangle in front of the lens, seen from inside and out.
        public void Hood(float z0, float z1, float w0, float h0, float w1, float h1, Mat m)
        {
            Vector3 A(float sx, float sy) => new(sx * w0, sy * h0, z0);
            Vector3 B(float sx, float sy) => new(sx * w1, sy * h1, z1);
            var sides = new[] { (1f, -1f, 1f, 1f), (1f, 1f, -1f, 1f), (-1f, 1f, -1f, -1f), (-1f, -1f, 1f, -1f) };
            foreach (var (x0, y0, x1, y1) in sides)
            {
                var p0 = A(x0, y0); var p1 = A(x1, y1); var p2 = B(x1, y1); var p3 = B(x0, y0);
                var mid = (p0 + p1) * 0.5f;
                var outward = new Vector3(mid.x, mid.y, 0f).normalized;
                Quad(m, p0, p1, p2, p3, outward);
                Quad(m, p0, p1, p2, p3, -outward);
            }
        }

        public Mesh[] Meshes(string name)
        {
            var result = new Mesh[MatCount];
            for (int i = 0; i < MatCount; i++)
            {
                if (t[i].Count == 0) continue;
                var mesh = new Mesh { name = $"TripodCam model {name} {(Mat)i}", hideFlags = HideFlags.HideAndDontSave };
                var verts = new Il2CppStructArray<Vector3>(v[i].Count);
                var norms = new Il2CppStructArray<Vector3>(n[i].Count);
                for (int k = 0; k < v[i].Count; k++) { verts[k] = v[i][k]; norms[k] = n[i][k]; }
                var tris = new Il2CppStructArray<int>(t[i].Count);
                for (int k = 0; k < t[i].Count; k++) tris[k] = t[i][k];
                mesh.vertices = verts;
                mesh.normals = norms;
                mesh.triangles = tris;
                mesh.RecalculateBounds();
                result[i] = mesh;
            }
            return result;
        }
    }
}
