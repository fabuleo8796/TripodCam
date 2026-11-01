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
///   the game's controls pause so Flippy stays put. 1/2/3 pick the mode, Enter/click plants, Esc cancels.
/// - Record key: start/stop recording from the planted tripod (Recorder).
/// - Menu key: the panel (Panel), which also changes the mode, smoothing and field of view of a planted tripod.
/// - Every frame while recording: the tripod's pose for this frame (Fixed, Aim at Flippy, Follow Flippy), after the
///   game has moved Flippy (its rigidbody interpolates, so its transform is already smooth here).
/// Idle cost: reading three keys.
public class TripodDriver : MonoBehaviour
{
    public TripodDriver(IntPtr ptr) : base(ptr) { }

    internal static TripodDriver Instance;
    static readonly string[] ModeNames = { "Fixed", "Aim at Flippy", "Follow Flippy" };

    internal enum Mode { Fixed, Aim, Follow }

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
    Vector3 flyPos;
    Mode placeMode;

    readonly Recorder recorder = new();
    bool prepared;
    float playerMissing = -1f;
    int previewShown;

    [HideFromIl2Cpp] internal Recorder Recorder => recorder;
    [HideFromIl2Cpp] internal bool Planted => planted;
    [HideFromIl2Cpp] internal Mode CurrentMode => mode;
    [HideFromIl2Cpp] internal float Fov => fov;

    void Update()
    {
        try
        {
            Instance ??= this;
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
            if (!(tripod || record || menu) || Typing()) return;
            bool playing = Cursor.lockState == CursorLockMode.Locked;
            if (tripod && playing) StartPlacing();
            else if (record) ToggleRecording();
            else if (menu && playing) Panel.Open();
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
                gameCam.transform.SetPositionAndRotation(flyPos, Quaternion.Euler(pitch, yaw, 0f));
                gameCam.fieldOfView = ScreenFov(flyFov);
            }
            if (planted && recorder.WantsFrame())
            {
                var player = EHS.PlayerRef.LocalPlayer;
                if (player != null)
                {
                    Pose(player.transform.position, out var pos, out var rot);
                    recorder.Frame(pos, rot, fov);
                }
            }
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
    /// Fixed keeps the current view.
    [HideFromIl2Cpp]
    internal void SetMode(Mode m)
    {
        if (planted && m != mode)
        {
            var pos = mode == Mode.Follow && smoothReady ? smoothPos : tripodPos;
            var rot = mode != Mode.Fixed && smoothReady ? smoothRot : tripodRot;
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

        var from = planted ? tripodPos : gameCam.transform.position;
        var rot = planted ? tripodRot : gameCam.transform.rotation;
        flyPos = from;
        var e = rot.eulerAngles;
        yaw = e.y;
        pitch = Mathf.DeltaAngle(0f, e.x);
        flyFov = planted ? fov : VideoFov(gameFov);
        flySpeed = Plugin.FlySpeed.Value;
        placeMode = mode;
        placing = true;
        GameControls.Pause();
        Overlay.ShowPlacing(true);
        Overlay.SetMode((int)placeMode);
        Overlay.SetFlyInfo(flyFov, flySpeed);
    }

    [HideFromIl2Cpp]
    void Placing(Keyboard keyboard, Mouse mouse)
    {
        if (gameCam == null) { EndPlacing(); return; }
        float dt = Time.unscaledDeltaTime;
        if (mouse != null)
        {
            var look = mouse.delta.ReadValue() * (0.1f * Plugin.LookSpeed.Value);
            yaw += look.x;
            pitch = Mathf.Clamp(pitch - look.y, -89f, 89f);
            float notches = mouse.scroll.ReadValue().y / 120f;
            if (Mathf.Abs(notches) > 0.01f)
            {
                if (keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed) flyFov = Mathf.Clamp(flyFov - notches * 3f, 15f, 110f);
                else flySpeed = Mathf.Clamp(flySpeed * Mathf.Pow(1.2f, notches), 0.5f, 80f);
                Overlay.SetFlyInfo(flyFov, flySpeed);
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
        float speed = flySpeed * (keyboard.leftShiftKey.isPressed ? 4f : 1f) * (keyboard.leftCtrlKey.isPressed ? 0.25f : 1f);
        if (move.sqrMagnitude > 1e-6f) flyPos += move.normalized * (speed * dt);

        if (keyboard.digit1Key.wasPressedThisFrame) SetPlaceMode(Mode.Fixed);
        if (keyboard.digit2Key.wasPressedThisFrame) SetPlaceMode(Mode.Aim);
        if (keyboard.digit3Key.wasPressedThisFrame) SetPlaceMode(Mode.Follow);

        if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame || (mouse != null && mouse.leftButton.wasPressedThisFrame)) Plant();
        else if (keyboard.escapeKey.wasPressedThisFrame || keyboard[Plugin.TripodKey.Value].wasPressedThisFrame)
        {
            EndPlacing();
            Overlay.Toast(planted ? "Kept the tripod where it was" : "Cancelled");
        }
    }

    [HideFromIl2Cpp]
    void SetPlaceMode(Mode m)
    {
        placeMode = m;
        Overlay.SetMode((int)m);
    }

    [HideFromIl2Cpp]
    void Plant()
    {
        var player = EHS.PlayerRef.LocalPlayer;
        planted = true;
        mode = placeMode;
        tripodPos = flyPos;
        tripodRot = Quaternion.Euler(pitch, yaw, 0f);
        fov = flyFov;
        followOffset = player != null ? flyPos - player.transform.position : Vector3.zero;
        smoothReady = false;
        EndPlacing();
        Overlay.Toast(recorder.Active ? "Tripod moved" : $"Tripod planted · {Panel.KeyName(Plugin.RecordKey.Value)} to record · {Panel.KeyName(Plugin.MenuKey.Value)} for the panel");
    }

    [HideFromIl2Cpp]
    void EndPlacing()
    {
        placing = false;
        if (gameCam != null) gameCam.fieldOfView = gameFov;
        if (brain != null) brain.enabled = brainWasOn; // the brain puts the camera back on Flippy next frame
        brain = null;
        GameControls.Resume();
        Overlay.ShowPlacing(false);
    }

    // ----- the tripod's pose -----

    [HideFromIl2Cpp]
    void Pose(Vector3 flippy, out Vector3 pos, out Quaternion rot)
    {
        float dt = Mathf.Max(Time.deltaTime, 1e-4f), smoothing = Plugin.Smoothing.Value;
        var aimAt = flippy + Vector3.up * Plugin.AimHeight.Value;
        switch (mode)
        {
            case Mode.Fixed:
                pos = tripodPos;
                rot = tripodRot;
                return;
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

    [HideFromIl2Cpp]
    static bool Typing()
    {
        var selected = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        return selected != null && selected.GetComponent<TMP_InputField>() != null;
    }
}
