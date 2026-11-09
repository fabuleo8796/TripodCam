using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Il2CppInterop.Runtime;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace TripodCam;

/// The tripod's camera and the video pipeline, built to cost the game as little as possible:
/// - one extra camera, a copy of the game camera's settings minus what a video doesn't need (HDR; MSAA and shadows when
///   switched off), rendering into a texture at the video size, switched on only for the frames that go into the video
///   (a 30 fps video at 120 fps play renders 1 frame in 4);
/// - the NV12 shader turns that picture into the encoder's own format on the GPU, so the readback is 1.5 bytes a pixel
///   instead of 4 and nothing is converted on the CPU (if the shader can't load, Media Foundation converts BGRA);
/// - an async GPU readback the frame after (no stall), copied into a pooled buffer;
/// - a background thread at below-normal priority encoding to MP4 (MfEncoder). At most a few frames wait in line: if
///   the encoder falls behind, frames are skipped (the video keeps the right timing) instead of the game slowing down.
/// Frames carry their real capture time, so a slow game makes a choppier video, never a faster or slower one.
internal sealed class Recorder
{
    const int MaxQueued = 6;

    enum State { Idle, Starting, Recording, Finishing }
    State state;

    GameObject host;
    Camera cam;
    RenderTexture rt, nv12Target;
    bool nv12;
    int width, height, fps, frameBytes;
    double start, next, renderTime;
    bool rendered;
    readonly List<(AsyncGPUReadbackRequest Request, double Time)> pending = new();

    readonly ConcurrentQueue<(byte[] Data, long Time)> queue = new();
    readonly ConcurrentBag<byte[]> pool = new();
    SemaphoreSlim signal;
    int queued;
    volatile bool stopping, ready, done;
    volatile string error;
    Thread thread;
    string path, stopReason;
    long captured, dropped;
    bool hardware;

    // Sound: the game's own, captured by Windows (AudioCapture) and written by the encoder thread next to the frames.
    AudioCapture audio;
    string soundProblem;
    int shownSecond = -1;

    // Stats: main-thread time of the tripod camera's render and of our own work, encoder time per frame.
    static long cameraStart;
    long cameraTicks, workTicks, encodeTicks, totalCamera, totalWork, totalEncode;
    int cameraRenders, encodeFrames, totalRenders, totalEncoded, secondFrames;
    double statsAt;
    public string Stats { get; private set; } = "";

    // x-ray: the game fades Flippy when its own camera gets close; the tripod shows Flippy solid.
    EHS.CameraXrayPlayerShader xray;
    EHS.MaterialHandlerOptimized xrayTarget;
    static Recorder current;
    static bool hooked, forced;

    // Speed lines: the game's streaks ride along with its own camera, so from the tripod they'd float in the world.
    // Their renderer is switched off just for the tripod camera's render and back on right after.
    ParticleSystemRenderer speedLines;
    static ParticleSystemRenderer hiddenLines;
    static bool modelHidden; // no free layer for the camera model: it's switched off during the tripod's render

    static AssetBundle shaderBundle;
    static AssetBundleCreateRequest bundleLoad;
    static AssetBundleRequest shaderLoad;
    static Material nv12Material;
    static int shaderStep; // 0 not started, 1 loading the bundle, 2 loading the shader, 3 done (ready or not)
    static readonly int SizeId = Shader.PropertyToID("_Size");

    public bool Active => state == State.Starting || state == State.Recording;
    public bool Recording => state == State.Recording;
    public bool Busy => state == State.Finishing;
    public double Seconds => state == State.Recording ? Time.unscaledTimeAsDouble - start : 0;
    public RenderTexture Picture => rt;

    public void Start(EHS.CameraManager manager)
    {
        var game = manager.cam;
        width = Math.Max(16, Plugin.Width.Value / 2 * 2);
        height = Math.Max(16, Plugin.Height.Value / 2 * 2);
        fps = Math.Clamp(Plugin.Fps.Value, 15, 60);
        try
        {
            SetUpCamera(game);
            xray = manager.xrayPlayerShader;
            xrayTarget = xray != null ? xray.localPlayerMaterialHandler : null;
            var lines = manager.speedLinesController;
            speedLines = lines != null ? lines.rend : null;
            Hook();

            Directory.CreateDirectory(Plugin.VideoFolder);
            path = Path.Combine(Plugin.VideoFolder, $"FIH {DateTime.Now:yyyy-MM-dd HH-mm-ss}.mp4");
            state = State.Starting;
            stopping = ready = done = false;
            error = null;
            stopReason = null;
            audio = null;
            soundProblem = null;
            captured = dropped = 0;
            queued = 0;
            shownSecond = -1;
            cameraTicks = workTicks = encodeTicks = totalCamera = totalWork = totalEncode = 0;
            cameraRenders = encodeFrames = totalRenders = totalEncoded = secondFrames = 0;
            Stats = "";
            signal = new SemaphoreSlim(0);
            thread = new Thread(EncodeLoop) { IsBackground = true, Name = "TripodCam encoder", Priority = System.Threading.ThreadPriority.BelowNormal };
            thread.Start();
            current = this;
            Overlay.Toast("Getting the recorder ready…");
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("couldn't start recording: " + e);
            Overlay.Toast("Couldn't start recording (see the BepInEx log)");
            state = State.Idle;
        }
    }

    void SetUpCamera(Camera game)
    {
        if (host == null)
        {
            host = new GameObject("TripodCam Camera");
            Object.DontDestroyOnLoad(host);
            host.hideFlags = HideFlags.HideAndDontSave;
            cam = host.AddComponent<Camera>();
            host.AddComponent<UniversalAdditionalCameraData>();
        }
        cam.CopyFrom(game); // fresh each time: levels can change the game camera's settings
        cam.enabled = false;
        cam.depth = game.depth - 10;
        if (Plugin.HideNames.Value) cam.cullingMask &= ~((1 << 5) | (1 << 15) | (1 << 16)); // UI, UIOnTop, Pings
        int model = CameraModel.HiddenLayer(game); // the camera model in the world never goes into the video
        if (model >= 0) cam.cullingMask &= ~(1 << model);
        cam.allowMSAA = game.allowMSAA && Plugin.SmoothEdges.Value;
        var src = game.GetComponent<UniversalAdditionalCameraData>();
        var dst = host.GetComponent<UniversalAdditionalCameraData>();
        if (src != null && dst != null)
        {
            dst.SetRenderer(src.m_RendererIndex);
            dst.renderPostProcessing = src.renderPostProcessing;
            dst.antialiasing = src.antialiasing;
            dst.antialiasingQuality = src.antialiasingQuality;
            dst.stopNaN = src.stopNaN;
            dst.dithering = src.dithering;
            dst.renderShadows = src.renderShadows && Plugin.Shadows.Value;
            dst.volumeLayerMask = src.volumeLayerMask;
            dst.requiresColorOption = src.requiresColorOption;
            dst.requiresDepthOption = src.requiresDepthOption;
            // Without post-processing HDR only costs memory and an extra copy: the video is 8-bit anyway.
            cam.allowHDR = game.allowHDR && src.renderPostProcessing;
        }
        if (rt == null || rt.width != width || rt.height != height)
        {
            if (rt != null) { rt.Release(); Object.Destroy(rt); }
            rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) { name = "TripodCam", hideFlags = HideFlags.HideAndDontSave };
            rt.Create();
        }
        cam.targetTexture = rt;

        nv12 = nv12Material != null;
        if (nv12 && (nv12Target == null || nv12Target.width != width || nv12Target.height != height * 3 / 2))
        {
            if (nv12Target != null) { nv12Target.Release(); Object.Destroy(nv12Target); }
            nv12Target = new RenderTexture(width, height * 3 / 2, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear)
                { name = "TripodCam NV12", hideFlags = HideFlags.HideAndDontSave };
            nv12Target.Create();
        }
        if (nv12) nv12Material.SetVector(SizeId, new Vector4(width, height, rt.sRGB ? 1 : 0, 0));
        frameBytes = nv12 ? width * height * 3 / 2 : width * height * 4;
    }

    /// The NV12 shader comes from a tiny asset bundle inside the DLL (built from the unity\ project with the game's
    /// Unity version). It loads in the background a few seconds after launch, a step per frame, with the async
    /// loaders: the game uses those itself, so they're its real compiled code. (The plain AssetBundle.LoadFromMemory /
    /// LoadFromFile / LoadAllAssets are stripped from this game and rebuilt by Il2CppInterop, and the rebuilt
    /// LoadFromMemory crashed the game in 0.2.0.) Until it's ready, or if it fails, the CPU path records instead.
    public static void PrepareShader()
    {
        if (shaderStep == 3) return;
        try
        {
            switch (shaderStep)
            {
                case 0:
                    shaderStep = 3; // in case something throws
                    if (!SystemInfo.SupportsRenderTextureFormat(RenderTextureFormat.R8)) throw new Exception("no R8 render textures");
                    var path = Path.Combine(Plugin.ConfigFolder, "tripodcam.shaders");
                    var bytes = Overlay.Resource("tripodcam.shaders");
                    Directory.CreateDirectory(Plugin.ConfigFolder);
                    if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length || !File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
                        File.WriteAllBytes(path, bytes);
                    bundleLoad = AssetBundle.LoadFromFileAsync(path);
                    shaderStep = 1;
                    break;
                case 1:
                    if (!bundleLoad.isDone) return;
                    shaderStep = 3;
                    shaderBundle = bundleLoad.assetBundle;
                    bundleLoad = null;
                    if (shaderBundle == null) throw new Exception("the shader bundle didn't load");
                    shaderLoad = shaderBundle.LoadAllAssetsAsync(Il2CppType.Of<Shader>());
                    shaderStep = 2;
                    break;
                case 2:
                    if (!shaderLoad.isDone) return;
                    shaderStep = 3;
                    var assets = shaderLoad.allAssets;
                    shaderLoad = null;
                    var shader = assets != null && assets.Length > 0 ? assets[0].TryCast<Shader>() : null;
                    if (shader == null || !shader.isSupported) throw new Exception("the NV12 shader isn't supported on this graphics card");
                    shader.hideFlags = HideFlags.HideAndDontSave;
                    nv12Material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    Plugin.Logger.LogInfo($"GPU colour conversion ready ({QualitySettings.activeColorSpace} colour space)");
                    break;
            }
        }
        catch (Exception e)
        {
            shaderStep = 3;
            nv12Material = null;
            Plugin.Logger.LogWarning($"GPU colour conversion is off, the CPU converts frames instead: {e.Message}");
        }
    }

    /// True when the current frame should go into the video (LateUpdate asks before working out the pose).
    public bool WantsFrame() => state == State.Recording && Time.unscaledTimeAsDouble + 0.0005 >= next;

    /// Renders this frame from the tripod (LateUpdate).
    public void Frame(Vector3 pos, Quaternion rot, float fov)
    {
        double now = Time.unscaledTimeAsDouble;
        next += 1.0 / fps;
        if (next < now) next = now + 1.0 / fps; // the game fell behind: no catch-up burst
        cam.transform.SetPositionAndRotation(pos, rot);
        cam.fieldOfView = fov;
        cam.enabled = true;
        rendered = true;
        renderTime = now - start;
    }

    /// Every frame (Update): convert and read back last frame's render, collect finished readbacks, timers, stats.
    public void Update()
    {
        if (state == State.Idle) return;
        long t0 = Stopwatch.GetTimestamp();
        if (state == State.Starting)
        {
            if (error != null) { Fail(); return; }
            if (ready)
            {
                state = State.Recording;
                start = next = statsAt = Time.unscaledTimeAsDouble;
                // the sound's clock is Stopwatch's: video time 0 is `start` on Unity's clock, which ran on since then
                if (audio != null)
                    audio.Origin = AudioCapture.Now() - (long)((Time.realtimeSinceStartupAsDouble - start) * 10_000_000)
                                   - Plugin.SoundOffsetMs.Value * 10_000L;
                Overlay.ShowRec(true, $"{height}p · {fps} fps");
                string key = Panel.KeyName(Plugin.RecordKey.Value);
                Overlay.Toast(soundProblem != null ? $"Recording without sound (see the log) · {key} to stop" : $"Recording · {key} to stop");
                Plugin.Logger.LogInfo($"Recording to {path} ({width}x{height} {fps} fps, {Plugin.Bitrate(width, height, fps) / 1_000_000} Mbps, " +
                                      $"{(nv12 ? "GPU" : "CPU")} colour conversion, {(hardware ? "GPU" : "CPU")} encoder, " +
                                      $"shadows {(Plugin.Shadows.Value ? "on" : "off")}, smooth edges {(cam.allowMSAA ? "on" : "off")}, " +
                                      (audio != null ? $"sound on ({audio.BufferSeconds * 1000:0} ms packets)" : "sound off") + ")");
                if (soundProblem != null) Plugin.Logger.LogWarning("No sound in this video: " + soundProblem);
            }
        }
        if (rendered)
        {
            rendered = false;
            cam.enabled = false;
            if (nv12)
            {
                var active = RenderTexture.active;
                Graphics.Blit(rt, nv12Target, nv12Material);
                RenderTexture.active = active;
                pending.Add((AsyncGPUReadback.Request(nv12Target, 0, TextureFormat.R8, null), renderTime));
            }
            else pending.Add((AsyncGPUReadback.Request(rt, 0, TextureFormat.BGRA32, null), renderTime));
        }
        for (int i = pending.Count - 1; i >= 0; i--)
        {
            var (request, time) = pending[i];
            if (!request.done) continue;
            pending.RemoveAt(i);
            if (state != State.Recording) continue; // still in flight when recording stopped
            if (request.hasError) { dropped++; continue; }
            Deliver(request, time);
        }
        if (state == State.Recording)
        {
            double now = Time.unscaledTimeAsDouble;
            int second = (int)(now - start);
            if (second != shownSecond) { shownSecond = second; Overlay.SetRecTime(second); }
            workTicks += Stopwatch.GetTimestamp() - t0;
            if (now - statsAt >= 1.0) RollStats(now);
            if (error != null) Fail();
        }
        else if (state == State.Finishing && done) Finished();
    }

    void Deliver(AsyncGPUReadbackRequest request, double time)
    {
        if (Volatile.Read(ref queued) >= MaxQueued) { dropped++; return; }
        IntPtr data = request.GetDataRaw(0);
        if (data == IntPtr.Zero) { dropped++; return; }
        if (!pool.TryTake(out var buffer) || buffer.Length != frameBytes) buffer = new byte[frameBytes];
        Marshal.Copy(data, buffer, 0, frameBytes);
        queue.Enqueue((buffer, (long)(time * 10_000_000)));
        Interlocked.Increment(ref queued);
        signal.Release();
        captured++;
        secondFrames++;
    }

    void RollStats(double now)
    {
        double ms = 1000.0 / Stopwatch.Frequency;
        long encTicks = Interlocked.Exchange(ref encodeTicks, 0);
        int encFrames = Interlocked.Exchange(ref encodeFrames, 0);
        totalCamera += cameraTicks; totalRenders += cameraRenders;
        totalWork += workTicks;
        string camera = cameraRenders > 0 ? $"{cameraTicks * ms / cameraRenders:0.0}" : "–";
        string work = secondFrames > 0 ? $"{workTicks * ms / secondFrames:0.0}" : "–";
        string encode = encFrames > 0 ? $"{encTicks * ms / encFrames:0.0}" : "–";
        Stats = $"tripod camera {camera} ms · copy {work} ms · encoder {encode} ms · {secondFrames / (now - statsAt):0} fps · " +
                $"queue {Volatile.Read(ref queued)}/{MaxQueued} · skipped {dropped} · {(nv12 ? "GPU" : "CPU")} colours · {(hardware ? "GPU" : "CPU")} encoder · " +
                (audio != null ? "sound" : "no sound");
        cameraTicks = workTicks = 0;
        cameraRenders = secondFrames = 0;
        statsAt = now;
    }

    public void Stop(string reason)
    {
        if (!Active) return;
        stopReason = reason;
        state = State.Finishing;
        if (cam != null) cam.enabled = false;
        rendered = false;
        stopping = true;
        signal?.Release();
        Overlay.ShowRec(false, null);
        Overlay.Toast("Saving the video…");
        if (reason == "the game closed") thread?.Join(5000); // let the MP4 finish before the process ends
    }

    void Finished()
    {
        state = State.Idle;
        current = null;
        string name = Path.GetFileName(path);
        if (captured == 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* leave it */ }
            Overlay.Toast("Stopped before anything was recorded");
            return;
        }
        if (error != null) { Fail(); return; }
        string note = dropped > 0 ? $" · skipped {dropped} frame{(dropped == 1 ? "" : "s")}" : "";
        Overlay.Toast($"Saved {name}{note}" + (stopReason != null ? $" ({stopReason})" : ""));
        double ms = 1000.0 / Stopwatch.Frequency;
        Plugin.Logger.LogInfo($"Saved {path}: {captured} frames, {dropped} skipped. Averages: tripod camera " +
                              $"{(totalRenders > 0 ? totalCamera * ms / totalRenders : 0):0.00} ms, copy {(captured > 0 ? totalWork * ms / captured : 0):0.00} ms " +
                              $"(main thread), encoder {(totalEncoded > 0 ? totalEncode * ms / totalEncoded : 0):0.00} ms per frame" +
                              (audio != null ? $". Sound: {audio.Seconds:0.00} s, {audio.PaddedSeconds:0.00} s filled in, {audio.DroppedSeconds:0.00} s dropped" +
                                               (audio.Error != null ? $", stopped early: {audio.Error}" : "") : ""));
        audio = null;
    }

    void Fail()
    {
        Plugin.Logger.LogError($"Recording failed: {error}");
        Overlay.ShowRec(false, null);
        Overlay.Toast("Recording failed (see the BepInEx log)");
        stopping = true;
        signal?.Release();
        state = State.Idle;
        current = null;
        if (cam != null) cam.enabled = false;
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint mode);

    // Background thread: starts the sound capture and opens the encoder (that can take a second or two), then writes
    // frames as they come and the sound that piled up in between.
    void EncodeLoop()
    {
        MfEncoder encoder = null;
        AudioCapture sound = null;
        try
        {
            CoInitializeEx(IntPtr.Zero, 0); // multithreaded COM for Media Foundation
            if (Plugin.Sound.Value)
            {
                try { sound = new AudioCapture(); }
                catch (Exception e) { soundProblem = e.Message; } // record anyway, without sound
            }
            encoder = new MfEncoder(path, width, height, fps, Plugin.Bitrate(width, height, fps), Plugin.GpuEncoder.Value, nv12, sound != null);
            hardware = encoder.Hardware;
            audio = sound;
            ready = true;
            long duration = 10_000_000L / fps;
            Action<IntPtr, int, long> writeSound = (data, frames, time) => encoder.WriteAudio(data, frames, time);
            while (true)
            {
                signal.Wait(sound != null ? 50 : 250);
                sound?.Drain(writeSound);
                while (queue.TryDequeue(out var frame))
                {
                    Interlocked.Decrement(ref queued);
                    long t0 = Stopwatch.GetTimestamp();
                    encoder.Write(frame.Data, frame.Time, duration);
                    long spent = Stopwatch.GetTimestamp() - t0;
                    Interlocked.Add(ref encodeTicks, spent);
                    Interlocked.Increment(ref encodeFrames);
                    totalEncode += spent;
                    totalEncoded++;
                    pool.Add(frame.Data);
                }
                if (stopping) break;
            }
            if (sound != null)
            {
                sound.Dispose();
                sound.Drain(writeSound);
            }
            encoder.Finish();
        }
        catch (Exception e)
        {
            error = e.Message;
        }
        finally
        {
            sound?.Dispose();
            try { encoder?.Dispose(); } catch { /* shutting down anyway */ }
            while (queue.TryDequeue(out _)) Interlocked.Decrement(ref queued);
            done = true;
        }
    }

    // ----- x-ray, speed lines and render timing -----

    static void Hook()
    {
        if (hooked) return;
        hooked = true;
        try
        {
            RenderPipelineManager.add_beginCameraRendering((Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(BeginCamera));
            RenderPipelineManager.add_endCameraRendering((Il2CppSystem.Action<ScriptableRenderContext, Camera>)new Action<ScriptableRenderContext, Camera>(EndCamera));
        }
        catch (Exception e)
        {
            Plugin.Logger.LogWarning($"Couldn't watch camera renders, Flippy may look faded in videos when the game camera is close: {e.Message}");
        }
    }

    static void BeginCamera(ScriptableRenderContext context, Camera camera)
    {
        try
        {
            var r = current;
            if (r == null || r.cam == null || camera == null || camera.Pointer != r.cam.Pointer) return;
            cameraStart = Stopwatch.GetTimestamp();
            if (CameraModel.NeedsRenderHide) { CameraModel.RenderHide(true); modelHidden = true; }
            if (r.speedLines != null && r.speedLines.enabled) // enabled, not forceRenderingOff: its getter is rebuilt (trap 19)
            {
                r.speedLines.enabled = false;
                hiddenLines = r.speedLines;
            }
            if (r.xray == null || r.xrayTarget == null || r.xray.shaderAlpha >= 0.999f) return;
            r.xrayTarget.SetFloat(EHS.CameraXrayPlayerShader.FadeStrength, 1f, true);
            forced = true;
        }
        catch { /* never break the game's rendering */ }
    }

    static void EndCamera(ScriptableRenderContext context, Camera camera)
    {
        if (hiddenLines is not null) // first and on its own: the player's own view must always get them back
        {
            var lines = hiddenLines;
            hiddenLines = null;
            try { if (lines != null) lines.enabled = true; } catch { /* gone with the level */ }
        }
        if (modelHidden) // same: the player's own view must always get the camera model back
        {
            modelHidden = false;
            try { CameraModel.RenderHide(false); } catch { /* never break the game's rendering */ }
        }
        if (cameraStart == 0) return;
        try
        {
            var r = current;
            if (r == null || r.cam == null || camera == null || camera.Pointer != r.cam.Pointer) return;
            r.cameraTicks += Stopwatch.GetTimestamp() - cameraStart;
            r.cameraRenders++;
            cameraStart = 0;
            if (!forced) return;
            forced = false;
            if (r.xray != null && r.xrayTarget != null) r.xrayTarget.SetFloat(EHS.CameraXrayPlayerShader.FadeStrength, r.xray.shaderAlpha, true);
        }
        catch { /* never break the game's rendering */ }
    }
}
