// TripodCamProbe: a throwaway recon plugin for Tripod Cam's M0. It changes nothing in the game; it only
// writes down what can't be read from the files, into BepInEx/TripodCamProbe.txt:
// - once Fabio is in a level: the live camera rig (Camera, URP camera data, Cinemachine brain and camera,
//   orbit, deoccluder, look input), the x-ray fade, the player's rigidbody, timing, every input action
//   with its current keys (his rebinds included), and FMOD's mixer format;
// - when he presses F8 there: a test camera renders into a 1080p texture for 60 frames next to the game
//   camera, with GPU readback, to measure what a tripod costs. One frame is saved as TripodCamProbe_frame.bmp.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace TripodCamProbe;

[BepInPlugin(Guid, "TripodCamProbe", "0.1.0")]
public class Plugin : BasePlugin
{
    public const string Guid = "com.fabuleo8796.tripodcamprobe";
    internal static ManualLogSource Logger;

    public override void Load()
    {
        Logger = Log;
        ClassInjector.RegisterTypeInIl2Cpp<ProbeDriver>();
        var host = new GameObject("TripodCamProbe");
        Object.DontDestroyOnLoad(host);
        host.hideFlags = HideFlags.HideAndDontSave;
        host.AddComponent<ProbeDriver>();
        Log.LogInfo("TripodCamProbe loaded: the report is written 5 s after you're in a level; press F8 there for the camera test.");
    }
}

public class ProbeDriver : MonoBehaviour
{
    public ProbeDriver(IntPtr ptr) : base(ptr) { }

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    float readySince = -1f;
    int reports;
    CameraTest test;
    readonly Queue<float> frameTimes = new();

    void Update()
    {
        try
        {
            frameTimes.Enqueue(Time.unscaledDeltaTime);
            while (frameTimes.Count > 240) frameTimes.Dequeue();

            bool ready = EHS.PlayerRef.LocalPlayer != null && FindManager() != null;
            if (!ready) readySince = -1f;
            else if (readySince < 0f) readySince = Time.unscaledTime;
            if (reports == 0 && ready && Time.unscaledTime - readySince > 5f)
            {
                reports++;
                WriteReport("in level");
            }

            var kb = Keyboard.current;
            if (kb != null && kb.f8Key.wasPressedThisFrame && test == null)
            {
                if (!ready) Plugin.Logger.LogWarning("F8: get into a level first (no local player or camera rig yet).");
                else test = new CameraTest(FindManager());
            }
            if (test != null && test.Tick()) test = null;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("probe update: " + e);
        }
    }

    [HideFromIl2Cpp]
    static EHS.CameraManager FindManager()
    {
        var found = Object.FindObjectsOfType(Il2CppType.Of<EHS.CameraManager>());
        return found != null && found.Length > 0 ? found[0].TryCast<EHS.CameraManager>() : null;
    }

    [HideFromIl2Cpp]
    void WriteReport(string when)
    {
        var r = new StringBuilder();
        r.AppendLine($"TripodCamProbe report ({when}), {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Section(r, "Machine and timing", () =>
        {
            float[] ts = frameTimes.ToArray();
            float avg = ts.Length > 0 ? ts.Average() : 0f;
            r.AppendLine($"graphics: {SystemInfo.graphicsDeviceType}, {SystemInfo.graphicsDeviceName}, asyncReadback={SystemInfo.supportsAsyncGPUReadback}");
            r.AppendLine($"screen {Screen.width}x{Screen.height}, fullscreen={Screen.fullScreenMode}, refresh={Screen.currentResolution.refreshRateRatio.value.ToString("0.##", Inv)}");
            r.AppendLine($"targetFrameRate={Application.targetFrameRate}, vSync={QualitySettings.vSyncCount}, fixedDeltaTime={Time.fixedDeltaTime.ToString("0.#####", Inv)}, timeScale={Time.timeScale.ToString(Inv)}");
            r.AppendLine($"last {ts.Length} frames: avg {(avg * 1000f).ToString("0.00", Inv)} ms ({(avg > 0 ? 1f / avg : 0f).ToString("0", Inv)} fps), worst {(ts.Length > 0 ? ts.Max() * 1000f : 0f).ToString("0.0", Inv)} ms");
            var rp = GraphicsSettings.currentRenderPipeline;
            r.AppendLine($"render pipeline: {(rp != null ? rp.name + " (" + rp.GetIl2CppType().FullName + ")" : "none")}");
            var urp = rp?.TryCast<UniversalRenderPipelineAsset>();
            if (urp != null)
                r.AppendLine($"URP: renderScale={urp.renderScale.ToString(Inv)}, msaa={urp.msaaSampleCount}, hdr={urp.supportsHDR}, shadowDistance={urp.shadowDistance.ToString(Inv)}");
            var names = new List<string>();
            for (int i = 0; i < 32; i++) if (LayerMask.LayerToName(i).Length > 0) names.Add($"{i}={LayerMask.LayerToName(i)}");
            r.AppendLine("layers: " + string.Join(", ", names));
        });

        var manager = FindManager();
        Section(r, "CameraManager", () => r.AppendLine(Props(manager)));
        Camera cam = null;
        Section(r, "Game camera", () =>
        {
            cam = manager.cam ?? Camera.main;
            r.AppendLine($"camera '{cam.name}' tag={cam.tag} isMain={(Camera.main != null && Camera.main.Pointer == cam.Pointer)} depth={cam.depth.ToString(Inv)} fov={cam.fieldOfView.ToString(Inv)} near={cam.nearClipPlane.ToString(Inv)} far={cam.farClipPlane.ToString(Inv)}");
            r.AppendLine($"cullingMask={cam.cullingMask} ({Layers(cam.cullingMask)}), clear={cam.clearFlags}, hdr={cam.allowHDR}, msaa={cam.allowMSAA}, dynRes={cam.allowDynamicResolution}, targetTexture={(cam.targetTexture != null ? cam.targetTexture.name : "screen")}, rect={cam.rect}");
            var cams = Camera.allCameras;
            r.AppendLine($"all enabled cameras ({cams.Length}): " + string.Join(", ", cams.Select(c => $"{c.name}(depth {c.depth.ToString(Inv)}, rt {(c.targetTexture != null)})")));
            r.AppendLine("components: " + Components(cam.gameObject));
        });
        Section(r, "URP camera data", () => r.AppendLine(Props(cam.GetComponent<UniversalAdditionalCameraData>())));
        Section(r, "Cinemachine brain", () =>
        {
            var brain = cam.GetComponent<CinemachineBrain>();
            r.AppendLine(Props(brain));
            try { r.AppendLine("active camera: " + brain.ActiveVirtualCamera?.Name); } catch (Exception e) { r.AppendLine("active camera: " + e.GetType().Name); }
        });
        Section(r, "Cinemachine camera", () =>
        {
            var cin = manager.cinCam;
            r.AppendLine($"'{cin.name}' components: " + Components(cin.gameObject));
            r.AppendLine(Props(cin));
            r.AppendLine("orbital follow: " + Props(manager.orbitalFollow));
            r.AppendLine("deoccluder: " + Props(manager.deoccluder));
            var sync = manager.axisSettingsSync;
            r.AppendLine("axis sync: " + Props(sync));
            if (sync != null) r.AppendLine("input axis controller: " + Props(sync.axisController));
        });
        Section(r, "X-ray fade", () => r.AppendLine(Props(manager.xrayPlayerShader)));
        Section(r, "Local player", () =>
        {
            var p = EHS.PlayerRef.LocalPlayer;
            var rb = p.rb;
            r.AppendLine($"player '{p.name}' at {p.transform.position}, rb interpolation={rb.interpolation}, collisionDetection={rb.collisionDetectionMode}, mass={rb.mass.ToString(Inv)}, layer={p.gameObject.layer}");
            r.AppendLine("material handler: " + Props(p.materialHandler));
        });
        Section(r, "Input actions (current keys, rebinds included)", () => Inputs(r));
        Section(r, "FMOD", () => Fmod.Describe(r));

        string path = Path.Combine(Paths.BepInExRootPath, "TripodCamProbe.txt");
        File.WriteAllText(path, r.ToString());
        Plugin.Logger.LogInfo("TripodCamProbe report written: " + path);
    }

    [HideFromIl2Cpp]
    static void Section(StringBuilder r, string title, Action body)
    {
        r.AppendLine().AppendLine("## " + title);
        try { body(); }
        catch (Exception e) { r.AppendLine("FAILED: " + e.GetType().Name + ": " + e.Message); }
    }

    [HideFromIl2Cpp]
    static void Inputs(StringBuilder r)
    {
        var enabled = new Il2CppSystem.Collections.Generic.List<InputAction>();
        InputSystem.ListEnabledActions(enabled);
        var assets = new Dictionary<IntPtr, InputActionAsset>();
        r.AppendLine($"{enabled.Count} enabled actions");
        for (int i = 0; i < enabled.Count; i++)
        {
            var a = enabled[i];
            var asset = a.actionMap?.asset;
            if (asset != null && !assets.ContainsKey(asset.Pointer)) assets[asset.Pointer] = asset;
            if (asset == null) r.AppendLine($"(no asset) {a.name}: {Bindings(a)}");
        }
        foreach (var asset in assets.Values)
        {
            r.AppendLine($"asset '{asset.name}'");
            var maps = asset.actionMaps;
            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                var actions = map.actions;
                for (int k = 0; k < actions.Count; k++)
                {
                    var a = actions[k];
                    r.AppendLine($"  {map.name}/{a.name} [{(a.enabled ? "on" : "off")}]: {Bindings(a)}");
                }
            }
            try
            {
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, $"TripodCamProbe_input_{Safe(asset.name)}.json"), asset.ToJson());
            }
            catch (Exception e) { r.AppendLine("  json: " + e.Message); }
        }
    }

    [HideFromIl2Cpp]
    static string Bindings(InputAction a)
    {
        var parts = new List<string>();
        var b = a.bindings;
        for (int i = 0; i < b.Count; i++)
        {
            var x = b[i];
            if (x.isComposite) parts.Add($"{x.name}(");
            else parts.Add((x.isPartOfComposite ? x.name + "=" : "") + x.effectivePath + (string.IsNullOrEmpty(x.groups) ? "" : $" [{x.groups}]"));
        }
        return string.Join(", ", parts);
    }

    static string Safe(string s) => new string((s ?? "asset").Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    static string Layers(int mask)
    {
        var on = new List<string>();
        for (int i = 0; i < 32; i++)
            if ((mask & (1 << i)) != 0) on.Add(LayerMask.LayerToName(i) is { Length: > 0 } n ? n : i.ToString());
        return string.Join("|", on);
    }

    [HideFromIl2Cpp]
    static string Components(GameObject go)
    {
        var comps = go.GetComponents<Component>();
        return string.Join(", ", comps.Select(c => c == null ? "null" : c.GetIl2CppType().Name + (c.TryCast<Behaviour>() is { } b && !b.enabled ? "(off)" : "")));
    }

    // Every property the interop wrapper declares on this type (fields included), one level deep.
    [HideFromIl2Cpp]
    internal static string Props(object o, int depth = 0)
    {
        if (o == null) return "null";
        if (o is Il2CppObjectBase ob && ob.Pointer == IntPtr.Zero) return "destroyed";
        var sb = new StringBuilder();
        // The type itself and its game/package base classes, stopping at Unity's and Il2Cpp's own.
        for (var t = o.GetType(); t != null; t = t.BaseType)
        {
            string ns = t.Namespace ?? "";
            if (t != o.GetType() && (ns.StartsWith("Il2Cpp") || ns == "UnityEngine" || ns == "System")) break;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name == "Pointer" || p.Name == "ObjectClass" || p.Name == "WasCollected") continue;
                string v;
                try { v = Show(p.GetValue(o), depth); }
                catch (TargetInvocationException e) { v = "!" + e.InnerException?.GetType().Name; }
                catch (Exception e) { v = "!" + e.GetType().Name; }
                sb.Append(p.Name).Append('=').Append(v).Append("; ");
            }
        }
        return sb.ToString();
    }

    [HideFromIl2Cpp]
    static string Show(object v, int depth)
    {
        switch (v)
        {
            case null: return "null";
            case float f: return f.ToString("0.###", Inv);
            case double d: return d.ToString("0.###", Inv);
            case string s: return "\"" + s + "\"";
            case Enum or bool or int or uint or long: return Convert.ToString(v, Inv);
            case Vector2 or Vector3 or Vector4 or Quaternion or Color or LayerMask: return v.ToString();
            case Object uo: return uo == null ? "null" : $"<{uo.GetIl2CppType().Name} '{uo.name}'>";
        }
        var type = v.GetType();
        if (depth < 1 && (type.Namespace ?? "").StartsWith("Unity.Cinemachine")) return "{" + Props(v, depth + 1) + "}";
        if (v is Il2CppObjectBase) return "<" + type.Name + ">";
        return v.ToString();
    }
}

// F8: a test camera next to the game camera renders into a 1080p texture for 60 frames (after 60 frames
// without it, for comparison), with an async GPU readback every frame.
class CameraTest
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    readonly Camera main;
    Camera cam;
    RenderTexture rt;
    int frame;
    readonly List<float> before = new(), during = new();
    readonly List<(int Frame, AsyncGPUReadbackRequest Req)> pending = new();
    readonly List<int> latency = new();
    int errors;
    bool saved;
    readonly StringBuilder log = new();

    public CameraTest(EHS.CameraManager manager)
    {
        main = manager.cam ?? Camera.main;
        Plugin.Logger.LogInfo("Camera test: 1 s without, then 1 s with a 1080p test camera...");
    }

    // True when finished.
    public bool Tick()
    {
        try
        {
            frame++;
            if (frame <= 60) { before.Add(Time.unscaledDeltaTime); return false; }
            if (frame == 61) Start();
            if (frame > 61 && frame <= 121) during.Add(Time.unscaledDeltaTime);
            if (frame <= 120)
                pending.Add((frame, AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, null)));
            Poll();
            if (frame < 121 + 30 && pending.Count > 0) return false;
            Finish();
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("camera test: " + e);
            Cleanup();
            return true;
        }
    }

    void Start()
    {
        var go = new GameObject("TripodCamProbe_TestCam");
        go.hideFlags = HideFlags.HideAndDontSave;
        cam = go.AddComponent<Camera>();
        cam.CopyFrom(main);
        var src = main.GetComponent<UniversalAdditionalCameraData>();
        var dst = go.AddComponent<UniversalAdditionalCameraData>();
        if (src != null)
        {
            dst.SetRenderer(src.m_RendererIndex);
            dst.renderPostProcessing = src.renderPostProcessing;
            dst.antialiasing = src.antialiasing;
            dst.antialiasingQuality = src.antialiasingQuality;
            dst.stopNaN = src.stopNaN;
            dst.dithering = src.dithering;
            dst.renderShadows = src.renderShadows;
            dst.volumeLayerMask = src.volumeLayerMask;
            dst.requiresColorOption = src.requiresColorOption;
            dst.requiresDepthOption = src.requiresDepthOption;
            log.AppendLine($"copied URP data: renderer {src.m_RendererIndex}, post={src.renderPostProcessing}, aa={src.antialiasing}, volumeMask={src.volumeLayerMask.value}");
        }
        rt = new RenderTexture(1920, 1080, 24, RenderTextureFormat.ARGB32) { name = "TripodCamProbe_RT" };
        rt.Create();
        cam.targetTexture = rt;
        go.transform.SetPositionAndRotation(main.transform.position, main.transform.rotation);
    }

    void Poll()
    {
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (f, req) = pending[i];
            if (!req.done) continue;
            pending.RemoveAt(i);
            if (req.hasError) { errors++; continue; }
            latency.Add(frame - f);
            if (!saved) { saved = true; Save(req); }
        }
    }

    unsafe void Save(AsyncGPUReadbackRequest req)
    {
        IntPtr data = req.GetDataRaw(0);
        if (data == IntPtr.Zero) { log.AppendLine("readback data pointer was null"); return; }
        int w = 1920, h = 1080;
        var bytes = new byte[54 + w * h * 4];
        fixed (byte* b = bytes)
        {
            byte* px = (byte*)data;
            for (int i = 0; i < w * h; i++)
            {
                byte* o = b + 54 + i * 4;
                o[0] = px[i * 4 + 2]; o[1] = px[i * 4 + 1]; o[2] = px[i * 4]; o[3] = 255;
            }
        }
        void U32(int at, int v) { bytes[at] = (byte)v; bytes[at + 1] = (byte)(v >> 8); bytes[at + 2] = (byte)(v >> 16); bytes[at + 3] = (byte)(v >> 24); }
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        U32(2, bytes.Length); U32(10, 54); U32(14, 40); U32(18, w); U32(22, h);
        bytes[26] = 1; bytes[28] = 32; U32(34, w * h * 4);
        File.WriteAllBytes(Path.Combine(Paths.BepInExRootPath, "TripodCamProbe_frame.bmp"), bytes);
        log.AppendLine("saved one frame as TripodCamProbe_frame.bmp");
    }

    void Finish()
    {
        float b = before.Count > 0 ? before.Average() : 0f, d = during.Count > 0 ? during.Average() : 0f;
        log.Insert(0, $"\n## Camera test (F8), {DateTime.Now:HH:mm:ss}\n" +
                      $"without test camera: {(b * 1000f).ToString("0.00", Inv)} ms/frame ({(b > 0 ? 1f / b : 0f).ToString("0", Inv)} fps)\n" +
                      $"with 1080p test camera + readback: {(d * 1000f).ToString("0.00", Inv)} ms/frame ({(d > 0 ? 1f / d : 0f).ToString("0", Inv)} fps), worst {(during.Count > 0 ? during.Max() * 1000f : 0f).ToString("0.0", Inv)} ms\n" +
                      $"readbacks: {latency.Count} ok, {errors} errors, {pending.Count} unfinished, latency {(latency.Count > 0 ? latency.Min() : -1)}..{(latency.Count > 0 ? latency.Max() : -1)} frames\n");
        File.AppendAllText(Path.Combine(Paths.BepInExRootPath, "TripodCamProbe.txt"), log.ToString());
        Plugin.Logger.LogInfo("Camera test done, added to TripodCamProbe.txt");
        Cleanup();
    }

    void Cleanup()
    {
        if (cam != null) { cam.targetTexture = null; Object.Destroy(cam.gameObject); }
        if (rt != null) { rt.Release(); Object.Destroy(rt); }
        cam = null;
        rt = null;
    }
}

// Read-only questions to FMOD's native API, through the game's own core system handle.
static unsafe class Fmod
{
    [DllImport("kernel32", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr module, string name);

    public static void Describe(StringBuilder r)
    {
        var sys = FMODUnity.RuntimeManager.CoreSystem;
        IntPtr handle = sys.handle;
        IntPtr module = GetModuleHandleW("fmodstudio.dll");
        r.AppendLine($"core system handle {(handle == IntPtr.Zero ? "none" : "ok")}, fmodstudio.dll {(module == IntPtr.Zero ? "not loaded" : "loaded")}");
        if (handle == IntPtr.Zero || module == IntPtr.Zero) return;
        IntPtr F(string name) => GetProcAddress(module, name);

        var getVersion = (delegate* unmanaged<IntPtr, uint*, uint*, int>)F("FMOD5_System_GetVersion");
        var getFormat = (delegate* unmanaged<IntPtr, int*, int*, int*, int>)F("FMOD5_System_GetSoftwareFormat");
        var getBuffer = (delegate* unmanaged<IntPtr, uint*, int*, int>)F("FMOD5_System_GetDSPBufferSize");
        var getOutput = (delegate* unmanaged<IntPtr, int*, int>)F("FMOD5_System_GetOutput");
        var getMaster = (delegate* unmanaged<IntPtr, IntPtr*, int>)F("FMOD5_System_GetMasterChannelGroup");
        r.AppendLine($"exports: version {Has(getVersion)}, format {Has(getFormat)}, buffer {Has(getBuffer)}, output {Has(getOutput)}, master {Has(getMaster)}, createDSP {(F("FMOD5_System_CreateDSP") != IntPtr.Zero)}");

        uint version = 0, build = 0;
        if (getVersion != null) r.AppendLine($"version: result {getVersion(handle, &version, &build)}, {version >> 16:x}.{(version >> 8) & 0xff:x2}.{version & 0xff:x2} build {build}");
        int rate = 0, mode = 0, raw = 0;
        if (getFormat != null) r.AppendLine($"software format: result {getFormat(handle, &rate, &mode, &raw)}, {rate} Hz, speaker mode {mode}, raw speakers {raw}");
        uint length = 0; int count = 0;
        if (getBuffer != null) r.AppendLine($"DSP buffer: result {getBuffer(handle, &length, &count)}, {length} samples x {count}");
        int output = 0;
        if (getOutput != null) r.AppendLine($"output type: result {getOutput(handle, &output)}, {output}");
        IntPtr master = IntPtr.Zero;
        if (getMaster != null) r.AppendLine($"master channel group: result {getMaster(handle, &master)}, {(master == IntPtr.Zero ? "none" : "ok")}");
    }

    static string Has(void* f) => f == null ? "missing" : "ok";
}
