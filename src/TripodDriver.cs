using System;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.Attributes;
using TMPro;
using Unity.Cinemachine;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using Object = UnityEngine.Object;

namespace TripodCam;

/// Keys and the tripod itself.
/// - Tripod key: placing mode. The game's camera brain is switched off and the main camera flies (WASD, Q/E, mouse),
///   the game's controls pause so Flippy stays put. 1/2/3/4 pick the mode, Enter/click plants, Esc cancels. Mode 4's
///   keyframe editor is in Keyframes.cs.
/// - Record key: start/stop recording from the planted tripod (Recorder).
/// - Menu key: the panel (Panel), which also changes the mode, smoothing and field of view of a planted tripod.
/// - Every frame while recording: the tripod's pose for this frame (Fixed, Aim at Flippy, Follow Flippy, Keyframes),
///   after the game has moved Flippy (its rigidbody interpolates, so its transform is already smooth here).
/// - The camera model (CameraModel) stands where the tripod films, worked out every frame by TripodPose: see-through
///   while planted, solid while recording (and then it also goes to other players through FIH Sync, SyncLink).
/// Idle cost: reading four keys.
public partial class TripodDriver : MonoBehaviour
{
    public TripodDriver(IntPtr ptr) : base(ptr) { }

    internal static TripodDriver Instance;
    static readonly string[] ModeNames = { "Fixed", "Aim at Flippy", "Follow Flippy", "Keyframes" };

    internal enum Mode { Fixed, Aim, Follow, Keys }

    // The planted tripod. Fov is the video's vertical field of view.
    bool planted;
    Mode mode = Mode.Aim;
    Vector3 tripodPos, followOffset;
    Quaternion tripodRot = Quaternion.identity;
    float fov = 60f;
    Vector3 smoothPos, smoothVel;
    Quaternion smoothRot;
    bool smoothReady;

    // Placing.
    bool placing;
    Camera gameCam;
    CinemachineBrain brain;
    bool brainWasOn;
    float gameFov, yaw, pitch, flySpeed, flyFov;
    Vector3 flyPos, homeOffset;
    Quaternion homeRot;
    static bool scrollLogged;
    Mode placeMode;

    readonly Recorder recorder = new();
    bool prepared;
    float playerMissing = -1f;
    int previewShown;

    // The camera model in the world while recording, and the tripod's settings it's worked out from.
    CameraModel.Rig model;
    readonly TripodPose display = new();
    bool modelShown;
    int take;

    [HideFromIl2Cpp] internal Recorder Recorder => recorder;
    [HideFromIl2Cpp] internal bool Planted => planted;
    [HideFromIl2Cpp] internal Mode CurrentMode => mode;
    [HideFromIl2Cpp] internal float Fov => fov;

    void Update()
    {
        try
        {
            Instance ??= this;
            SyncLink.Start();
            Overlay.Tick();
            Panel.TakeFolder();
            if (!prepared && Time.unscaledTime > 6f)
            {
                prepared = true;
                Overlay.Build(); // ahead of time: the first build loads fonts and takes a moment
                Panel.Build();
            }
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            GameControls.Tick(keyboard, mouse);
            if (prepared) Recorder.PrepareShader();
            recorder.Update();
            UpdatePreview();
            WatchPlayer();
            if (keyboard == null) return;
            if (placing) { Placing(keyboard, mouse); return; }
            if (Panel.IsOpen)
            {
                if (Panel.Tick(keyboard) || Panel.Rebinding) return;
                if (FolderPicker.Busy) return; // keys typed into the folder window may still reach the game
                if (keyboard.escapeKey.wasPressedThisFrame || keyboard[Plugin.MenuKey.Value].wasPressedThisFrame) Panel.Close();
                else if (keyboard[Plugin.RecordKey.Value].wasPressedThisFrame) { ToggleRecording(); Panel.Refresh(); }
                else if (keyboard[Plugin.TripodKey.Value].wasPressedThisFrame) PlaceFromPanel();
                return;
            }
            if (GameControls.Active || FolderPicker.Busy) return;
            // our keys first: the typing check and the cursor only matter on the frame one of them is pressed
            bool tripod = keyboard[Plugin.TripodKey.Value].wasPressedThisFrame;
            bool record = keyboard[Plugin.RecordKey.Value].wasPressedThisFrame;
            bool menu = keyboard[Plugin.MenuKey.Value].wasPressedThisFrame;
            bool keyframe = keyboard[Plugin.KeyframeKey.Value].wasPressedThisFrame;
            if (!(tripod || record || menu || keyframe) || Typing()) return;
            bool playing = Cursor.lockState == CursorLockMode.Locked;
            if (tripod && playing) StartPlacing();
            else if (record) ToggleRecording();
            else if (menu && playing) Panel.Open();
            else if (keyframe) RestartPath();
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("update: " + e);
        }
    }

    void LateUpdate()
    {
        try
        {
            if (placing && gameCam != null)
            {
                var flyRot = Quaternion.Euler(pitch, yaw, 0f);
                float screenFov = ScreenFov(flyFov);
                gameCam.transform.SetPositionAndRotation(flyPos, flyRot);
                gameCam.fieldOfView = screenFov;
                if (placeMode == Mode.Keys) Timeline.DrawPath(path, playhead, FrameRate, flyPos, flyRot, screenFov);
            }
            if (planted && recorder.WantsFrame())
            {
                var player = EHS.PlayerRef.LocalPlayer;
                if (player != null && Pose(player.transform.position, out var pos, out var rot, out var camFov))
                    recorder.Frame(pos, rot, camFov);
            }
            UpdateModel();
            CameraModel.Tick();
            SyncLink.Tick(Time.deltaTime);
        }
        catch (Exception e)
        {
            Plugin.Logger.LogError("late update: " + e);
        }
    }

    void OnApplicationQuit() => recorder.Stop("the game closed");

    [HideFromIl2Cpp]
    internal void ToggleRecording()
    {
        if (recorder.Busy) return;
        if (recorder.Active) { recorder.Stop(null); return; }
        if (!planted) { Overlay.Toast($"Press {Panel.KeyName(Plugin.TripodKey.Value)} to place the tripod first"); return; }
        var manager = FindManager();
        if (manager == null || manager.cam == null) { Overlay.Toast("Get into a level first"); return; }
        smoothReady = false; // Aim and Follow start from where Flippy is now
        pathStart = 0; // the keyframe path starts with the video
        take++;
        recorder.Start(manager);
    }

    /// The panel's "Place / Move tripod" button and the tripod key while the panel is open.
    [HideFromIl2Cpp]
    internal void PlaceFromPanel()
    {
        Panel.Close();
        StartPlacing();
    }

    /// Switching mode from the panel keeps the camera where it is right now: Follow keeps today's distance to Flippy,
    /// Fixed keeps the current view (in Keyframes mode, where the path is).
    [HideFromIl2Cpp]
    internal void SetMode(Mode m)
    {
        if (m == Mode.Keys && m != mode && path.Count == 0)
            Overlay.Toast($"No keyframes yet: press {Panel.KeyName(Plugin.TripodKey.Value)}, then 4 and {Panel.KeyName(Plugin.KeyframeKey.Value)} to add some");
        if (planted && m != mode)
        {
            var pos = mode == Mode.Follow && smoothReady ? smoothPos : tripodPos;
            var rot = (mode == Mode.Aim || mode == Mode.Follow) && smoothReady ? smoothRot : tripodRot;
            tripodPos = pos;
            tripodRot = rot;
            var player = EHS.PlayerRef.LocalPlayer;
            if (player != null) followOffset = pos - player.transform.position;
            smoothPos = pos;
            smoothVel = Vector3.zero;
            smoothRot = rot;
        }
        mode = m;
    }

    [HideFromIl2Cpp]
    internal void SetFov(float value) => fov = Mathf.Clamp(value, 15f, 110f);

    // The live preview: the tripod's picture in the corner while recording (if switched on).
    [HideFromIl2Cpp]
    void UpdatePreview()
    {
        bool want = recorder.Recording && Plugin.Preview.Value && recorder.Picture != null;
        int shown = want ? 1 + (int)mode : 0;
        if (shown == previewShown) return;
        previewShown = shown;
        Overlay.ShowPreview(want ? recorder.Picture : null, ModeNames[(int)mode]);
    }

    // Recording stops by itself if the level goes away (no local player for 2 s).
    [HideFromIl2Cpp]
    void WatchPlayer()
    {
        bool present = EHS.PlayerRef.LocalPlayer != null;
        if (present) { playerMissing = -1f; return; }
        if (playerMissing < 0f) playerMissing = Time.unscaledTime;
        if (Time.unscaledTime - playerMissing > 2f)
        {
            if (recorder.Active) recorder.Stop("the level changed");
            if (placing) EndPlacing();
            if (Panel.IsOpen) Panel.Close();
            planted = false;
            if (path.Count > 0) path.Clear(); // keyframes are spots in that level
            playhead = 0f;
        }
    }

    // ----- placing -----

    [HideFromIl2Cpp]
    void StartPlacing()
    {
        var manager = FindManager();
        gameCam = manager != null ? manager.cam : null;
        if (gameCam == null) { Overlay.Toast("Get into a level first"); return; }
        brain = gameCam.GetComponent<CinemachineBrain>();
        brainWasOn = brain != null && brain.enabled;
        if (brain != null) brain.enabled = false;
        gameFov = gameCam.fieldOfView;
        var player = EHS.PlayerRef.LocalPlayer;
        homeOffset = player != null ? gameCam.transform.position - player.transform.position : Vector3.zero; // the game's own view of Flippy
        homeRot = gameCam.transform.rotation;

        var from = planted ? tripodPos : gameCam.transform.position;
        var rot = planted ? tripodRot : gameCam.transform.rotation;
        flyPos = from;
        var e = rot.eulerAngles;
        yaw = e.y;
        pitch = Mathf.DeltaAngle(0f, e.x);
        flyFov = planted ? fov : VideoFov(gameFov);
        flySpeed = Plugin.FlySpeed.Value;
        placeMode = mode;
        placeLock = Cursor.lockState;
        placeCursor = Cursor.visible;
        if (placeMode == Mode.Keys && path.Sample(Mathf.Min(playhead, path.End), pose)) UsePose(pose); // back where the path is
        placing = true;
        GameControls.Pause();
        Overlay.ShowPlacing(true);
        Overlay.SetMode((int)placeMode);
        Overlay.SetFlyInfo(flyFov, flySpeed);
        ShowKeyEditor(placeMode == Mode.Keys);
    }

    [HideFromIl2Cpp]
    void Placing(Keyboard keyboard, Mouse mouse)
    {
        if (gameCam == null) { EndPlacing(); return; }
        float dt = Time.unscaledDeltaTime;
        bool keys = placeMode == Mode.Keys;
        if (mouse != null)
        {
            if (!mouseMode && !playingPath)
            {
                var look = mouse.delta.ReadValue() * (0.1f * Plugin.LookSpeed.Value);
                yaw += look.x;
                pitch = Mathf.Clamp(pitch - look.y, -89f, 89f);
            }
            float notches = ScrollNotches(mouse);
            if (Mathf.Abs(notches) > 0.01f)
            {
                var at = mouse.position.ReadValue();
                if (keys && mouseMode && Timeline.Over(at)) // over the timeline: Ctrl+Scroll zooms it, Scroll moves along it
                {
                    if (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed) Timeline.Zoom(at, notches);
                    else Timeline.Pan(notches);
                }
                else
                {
                    if (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed) flyFov = Mathf.Clamp(flyFov - notches * 3f, 15f, 110f);
                    else flySpeed = Mathf.Clamp(flySpeed * Mathf.Pow(1.2f, notches), 0.5f, 200f);
                    Overlay.SetFlyInfo(flyFov, flySpeed);
                }
            }
        }

        var rot = Quaternion.Euler(pitch, yaw, 0f);
        var move = Vector3.zero;
        if (keyboard.wKey.isPressed) move += rot * Vector3.forward;
        if (keyboard.sKey.isPressed) move -= rot * Vector3.forward;
        if (keyboard.dKey.isPressed) move += rot * Vector3.right;
        if (keyboard.aKey.isPressed) move -= rot * Vector3.right;
        if (keyboard.eKey.isPressed) move += Vector3.up;
        if (keyboard.qKey.isPressed) move -= Vector3.up;
        float speed = flySpeed * (keyboard.leftShiftKey.isPressed ? Plugin.ShiftBoost.Value : 1f) * (keyboard.leftCtrlKey.isPressed ? 0.25f : 1f);
        if (move.sqrMagnitude > 1e-6f && !playingPath) flyPos += move.normalized * (speed * dt);

        if (keyboard.digit1Key.wasPressedThisFrame) SetPlaceMode(Mode.Fixed);
        if (keyboard.digit2Key.wasPressedThisFrame) SetPlaceMode(Mode.Aim);
        if (keyboard.digit3Key.wasPressedThisFrame) SetPlaceMode(Mode.Follow);
        if (keyboard.digit4Key.wasPressedThisFrame) SetPlaceMode(Mode.Keys);
        if (keyboard[Plugin.ToFlippyKey.Value].wasPressedThisFrame) ToFlippy();
        if (keys != (placeMode == Mode.Keys)) return; // the editor opened or closed this frame
        if (keys) EditKeys(keyboard, mouse);

        // in mode 4 a click belongs to the timeline, so only Enter plants
        bool plant = keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame;
        if (plant || (!keys && mouse != null && mouse.leftButton.wasPressedThisFrame)) Plant();
        else if (keys && playingPath && keyboard.escapeKey.wasPressedThisFrame) StopPlay();
        else if (keyboard.escapeKey.wasPressedThisFrame || keyboard[Plugin.TripodKey.Value].wasPressedThisFrame)
        {
            EndPlacing();
            Overlay.Toast(keys && path.Count > 0 ? "Closed · your keyframes are kept" : planted ? "Kept the tripod where it was" : "Cancelled");
        }
    }

    /// The teleport key: the camera jumps back to Flippy, seen the way the game showed it when placing started.
    [HideFromIl2Cpp]
    void ToFlippy()
    {
        var player = EHS.PlayerRef.LocalPlayer;
        if (player == null) return;
        StopPlay();
        flyPos = player.transform.position + homeOffset;
        var e = homeRot.eulerAngles;
        yaw = e.y;
        pitch = Mathf.Clamp(Mathf.DeltaAngle(0f, e.x), -89f, 89f);
        Overlay.Toast("Camera back at Flippy");
    }

    [HideFromIl2Cpp]
    void SetPlaceMode(Mode m)
    {
        if (m == placeMode) return;
        placeMode = m;
        Overlay.SetMode((int)m);
        ShowKeyEditor(m == Mode.Keys);
    }

    [HideFromIl2Cpp]
    void Plant()
    {
        if (placeMode == Mode.Keys && path.Count == 0)
        {
            Overlay.Toast($"Add a keyframe first: {Panel.KeyName(Plugin.KeyframeKey.Value)} sets one where the camera is");
            return;
        }
        var player = EHS.PlayerRef.LocalPlayer;
        planted = true;
        mode = placeMode;
        tripodPos = flyPos;
        tripodRot = Quaternion.Euler(pitch, yaw, 0f);
        fov = flyFov;
        followOffset = player != null ? flyPos - player.transform.position : Vector3.zero;
        smoothReady = false;
        EndPlacing();
        string record = Panel.KeyName(Plugin.RecordKey.Value);
        if (mode == Mode.Keys)
            Overlay.Toast(recorder.Active ? "Keyframe path updated" : $"Keyframe path ready · {record} records it from the start");
        else
            Overlay.Toast(recorder.Active ? "Tripod moved" : $"Tripod planted · {record} to record · {Panel.KeyName(Plugin.MenuKey.Value)} for the panel");
    }

    [HideFromIl2Cpp]
    void EndPlacing()
    {
        placing = false;
        ShowKeyEditor(false);
        if (gameCam != null) gameCam.fieldOfView = gameFov;
        if (brain != null) brain.enabled = brainWasOn; // the brain puts the camera back on Flippy next frame
        brain = null;
        GameControls.Resume();
        Overlay.ShowPlacing(false);
    }

    // ----- the tripod's pose -----

    /// False when the keyframe path just ended the recording.
    [HideFromIl2Cpp]
    bool Pose(Vector3 flippy, out Vector3 pos, out Quaternion rot, out float camFov)
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f), smoothing = Plugin.Smoothing.Value;
        var aimAt = flippy + Vector3.up * Plugin.AimHeight.Value;
        camFov = fov;
        switch (mode)
        {
            case Mode.Keys:
                return PathPose(out pos, out rot, out camFov);
            case Mode.Fixed:
                pos = tripodPos;
                rot = tripodRot;
                return true;
            case Mode.Follow:
                var want = flippy + followOffset; // world offset: Flippy tumbles, the camera doesn't
                if (!smoothReady) { smoothPos = want; smoothVel = Vector3.zero; }
                smoothPos = smoothing <= 0f ? want : Vector3.SmoothDamp(smoothPos, want, ref smoothVel, smoothing * 0.6f, Mathf.Infinity, dt);
                pos = smoothPos;
                break;
            default:
                pos = tripodPos;
                break;
        }
        var dir = aimAt - pos;
        var look = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir, Vector3.up) : tripodRot;
        if (!smoothReady) { smoothRot = look; smoothReady = true; }
        smoothRot = smoothing <= 0f ? look : Quaternion.Slerp(smoothRot, look, 1f - Mathf.Exp(-dt / (smoothing * 0.25f)));
        rot = smoothRot;
        return true;
    }

    // ----- the camera model -----

    /// Whenever the tripod is planted (and the model is switched on in the panel): the little camera stands where the
    /// tripod films, see-through until you record, solid while recording. It's worked out every frame, so it moves
    /// smoothly even when the video takes fewer frames than the game draws.
    [HideFromIl2Cpp]
    void UpdateModel()
    {
        var player = EHS.PlayerRef.LocalPlayer;
        bool recording = recorder.Active;
        CameraModel.GhostAlpha = Plugin.GhostOpacity.Value;
        bool want = planted && Plugin.ShowCamera.Value && player != null && (recording || Plugin.GhostOpacity.Value > 0.01f);
        if (!want)
        {
            if (modelShown) { model?.Show(false); modelShown = false; }
            return;
        }
        if (model == null || !model.Alive)
        {
            var manager = FindManager();
            if (manager != null) CameraModel.HiddenLayer(manager.cam); // on its layer from the start, before any recording
            model = new CameraModel.Rig("TripodCam Camera Model");
        }
        FillPose(display);
        if (!modelShown) display.Restart();
        model.SetGhost(!recording);
        float dt = Time.deltaTime;
        // not recording: a keyframe path shows where it starts
        display.Get(player.transform.position, recording ? (float)(recorder.Seconds - pathStart) : 0f, dt, out var pos, out var rot);
        model.Place(pos, rot, Plugin.CameraSize.Value, dt);
        model.Show(!CameraModel.TooClose(pos, Plugin.CameraSize.Value));
        modelShown = true;
    }

    [HideFromIl2Cpp]
    void FillPose(TripodPose p)
    {
        p.Mode = mode;
        p.Pos = tripodPos;
        p.Rot = tripodRot;
        p.FollowOffset = followOffset;
        p.AimHeight = Plugin.AimHeight.Value;
        p.Smoothing = Plugin.Smoothing.Value;
        p.Path = path;
        p.End = Plugin.PathEndMode.Value;
        p.Take = take;
    }

    /// What FIH Sync sends to other modded players: the tripod's settings while recording with the model on, else null.
    /// In Keyframes mode only the path goes (the spot the recording last passed changes every frame).
    [HideFromIl2Cpp]
    internal string ShareState()
    {
        if (!planted || !recorder.Active || !Plugin.ShowCamera.Value) return null;
        var p = new TripodPose();
        FillPose(p);
        if (mode == Mode.Keys) { p.Pos = Vector3.zero; p.Rot = Quaternion.identity; }
        return p.Write();
    }

    // ----- helpers -----

    /// The video's frame on this screen: the screen camera shows the same width as the video (or the same height on
    /// screens wider than the video), so the frame guide is exactly what gets recorded.
    internal static float VideoAspect => Plugin.Width.Value / (float)Mathf.Max(1, Plugin.Height.Value);

    static float ScreenAspect => Screen.width / (float)Mathf.Max(1, Screen.height);

    [HideFromIl2Cpp]
    static float ScreenFov(float videoFov)
    {
        if (ScreenAspect >= VideoAspect) return videoFov;
        float t = Mathf.Tan(videoFov * 0.5f * Mathf.Deg2Rad) * VideoAspect / ScreenAspect;
        return 2f * Mathf.Atan(t) * Mathf.Rad2Deg;
    }

    [HideFromIl2Cpp]
    static float VideoFov(float screenFov)
    {
        if (ScreenAspect >= VideoAspect) return screenFov;
        float t = Mathf.Tan(screenFov * 0.5f * Mathf.Deg2Rad) * ScreenAspect / VideoAspect;
        return 2f * Mathf.Atan(t) * Mathf.Rad2Deg;
    }

    static EHS.CameraManager cachedManager;

    [HideFromIl2Cpp]
    internal static EHS.CameraManager FindManager()
    {
        if (cachedManager != null && cachedManager.Pointer != IntPtr.Zero && cachedManager.cam != null) return cachedManager;
        var found = Object.FindObjectsOfType(Il2CppType.Of<EHS.CameraManager>());
        cachedManager = found != null && found.Length > 0 ? found[0].TryCast<EHS.CameraManager>() : null;
        return cachedManager;
    }

    /// Scroll wheel steps this frame. This game's Input System gives 1 per notch (its "uniform" scroll setting);
    /// Windows' own range is 120 per notch, in case a game update switches to it.
    [HideFromIl2Cpp]
    static float ScrollNotches(Mouse mouse)
    {
        float y = mouse.scroll.ReadValue().y;
        if (y == 0f) return 0f;
        if (!scrollLogged)
        {
            scrollLogged = true;
            Plugin.Logger.LogInfo($"Scroll wheel reads {y.ToString(System.Globalization.CultureInfo.InvariantCulture)} per step");
        }
        return Mathf.Abs(y) >= 20f ? y / 120f : y;
    }

    [HideFromIl2Cpp]
    static bool Typing()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.GetComponent<TMP_InputField>() != null;
    }
}
