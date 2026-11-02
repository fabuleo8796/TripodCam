using Il2CppInterop.Runtime.Attributes;
using UnityEngine;
using UnityEngine.InputSystem;

namespace TripodCam;

/// Mode 4, Keyframes: the tripod flies a path through camera poses you set.
/// - Editing happens while placing (T, then 4): fly to a spot, press I and a keyframe goes in at the playhead, which
///   then moves on by the gap (2 s) so the next I adds the next key. I on a key's time updates that key.
///   Arrows pick keys (the camera jumps there), Del deletes, Space plays the path from the playhead.
/// - Click (or Tab) frees the mouse for the timeline: drag keys to retime them, drag the playhead to scrub (the camera
///   follows the path), right-click a key to delete it, Scroll / Ctrl+Scroll to move along / zoom the timeline, click
///   outside it to fly again.
/// - Recording plays the path from its start: time 0 is the video's first frame. After the last key the camera holds,
///   loops or stops the recording (panel). I while recording restarts the path.
/// Keys live in world space, so they go when the level does.
public partial class TripodDriver
{
    readonly KeyPath path = new();
    readonly float[] pose = new float[KeyPath.Channels];
    float playhead;
    double pathStart;
    bool playingPath, mouseMode, scrubbing;
    KeyPath.Key dragging;
    int hover = -1;
    CursorLockMode placeLock;
    bool placeCursor;

    [HideFromIl2Cpp] internal KeyPath Path => path;

    static int FrameRate => Mathf.Clamp(Plugin.Fps.Value, 15, 60);

    static float Snap(float t) => Mathf.Max(0f, Mathf.Round(t * FrameRate) / FrameRate);

    // ----- placing in Keyframes mode -----

    /// Every frame while placing in mode 4, after flying.
    [HideFromIl2Cpp]
    void EditKeys(Keyboard keyboard, Mouse mouse)
    {
        if (keyboard[Plugin.KeyframeKey.Value].wasPressedThisFrame) AddKey();
        if (keyboard.leftArrowKey.wasPressedThisFrame) StepKey(-1);
        if (keyboard.rightArrowKey.wasPressedThisFrame) StepKey(1);
        if (keyboard.deleteKey.wasPressedThisFrame || keyboard.backspaceKey.wasPressedThisFrame) DeleteKey(path.Near(playhead, 0.5f / FrameRate));
        if (keyboard.spaceKey.wasPressedThisFrame) TogglePlay();
        if (keyboard.tabKey.wasPressedThisFrame) SetMouseMode(!mouseMode);
        if (playingPath) PlayStep();
        if (mouse != null)
        {
            if (mouseMode) MouseOnTimeline(mouse);
            else if (mouse.leftButton.wasPressedThisFrame) SetMouseMode(true);
        }
        Timeline.Draw(path, playhead, FrameRate, dragging != null || scrubbing, hover, mouseMode);
    }

    [HideFromIl2Cpp]
    void AddKey()
    {
        StopPlay();
        FlyPose(pose);
        int at = path.Near(playhead, 0.5f / FrameRate);
        if (at >= 0)
        {
            path.Set(at, pose);
            Overlay.Toast($"Keyframe {at + 1} updated");
            return;
        }
        bool last = path.Count == 0 || playhead > path.End;
        at = path.Add(playhead, pose);
        Overlay.Toast($"Keyframe {at + 1} at {Seconds(playhead)} s" +
                      (path.Count == 1 ? $" · fly on and press {Panel.KeyName(Plugin.KeyframeKey.Value)} for the next one" : ""));
        if (last) playhead = Snap(playhead + Mathf.Max(0.1f, Plugin.KeyGap.Value));
    }

    /// The previous or next key; past the last one is the free spot where the next key goes.
    [HideFromIl2Cpp]
    void StepKey(int dir)
    {
        StopPlay();
        if (path.Count == 0) { Overlay.Toast($"No keyframes yet: press {Panel.KeyName(Plugin.KeyframeKey.Value)} to add one"); return; }
        const float eps = 0.001f;
        if (dir < 0)
        {
            int i = path.Count - 1;
            while (i > 0 && path[i].Time >= playhead - eps) i--;
            JumpTo(i);
            return;
        }
        for (int i = 0; i < path.Count; i++)
            if (path[i].Time > playhead + eps) { JumpTo(i); return; }
        playhead = Snap(path.End + Mathf.Max(0.1f, Plugin.KeyGap.Value));
        Overlay.Toast($"After the last keyframe: fly somewhere and press {Panel.KeyName(Plugin.KeyframeKey.Value)}");
    }

    [HideFromIl2Cpp]
    void DeleteKey(int at)
    {
        StopPlay();
        if (at < 0) { Overlay.Toast("Pick a keyframe first (arrows, or click it)"); return; }
        path.RemoveAt(at);
        hover = -1;
        Overlay.Toast($"Keyframe {at + 1} deleted");
    }

    [HideFromIl2Cpp]
    void JumpTo(int i)
    {
        playhead = path[i].Time;
        UsePose(path[i].Pose);
    }

    [HideFromIl2Cpp]
    void TogglePlay()
    {
        if (playingPath) { StopPlay(); return; }
        if (path.Count < 2) { Overlay.Toast("Add 2 keyframes or more to play the path"); return; }
        if (playhead >= path.End - 0.001f) playhead = 0f;
        playingPath = true;
        dragging = null;
        scrubbing = false;
    }

    [HideFromIl2Cpp]
    void PlayStep()
    {
        playhead += Time.unscaledDeltaTime;
        if (playhead >= path.End) { playhead = path.End; playingPath = false; }
        FollowPath();
    }

    [HideFromIl2Cpp]
    void StopPlay()
    {
        if (!playingPath) return;
        playingPath = false;
        playhead = Snap(playhead);
    }

    /// The mouse on the timeline (mouse mode).
    [HideFromIl2Cpp]
    void MouseOnTimeline(Mouse mouse)
    {
        if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None; // the game grabs it back
        if (!Cursor.visible) Cursor.visible = true;
        var hit = Timeline.HitTest(mouse.position.ReadValue(), path);
        hover = dragging != null ? path.Near(dragging.Time, 0.0001f) : hit.Key;

        if (mouse.leftButton.wasPressedThisFrame)
        {
            if (!hit.Inside) { SetMouseMode(false); return; }
            StopPlay();
            if (hit.Key >= 0)
            {
                dragging = path[hit.Key];
                JumpTo(hit.Key);
            }
            else if (hit.Lane) scrubbing = true;
        }
        else if (mouse.rightButton.wasPressedThisFrame && hit.Key >= 0 && dragging == null) DeleteKey(hit.Key);

        if ((dragging != null || scrubbing) && mouse.leftButton.isPressed && hit.Edge != 0)
            Timeline.EdgePan(hit.Edge, Time.unscaledDeltaTime); // drag past an end to go further in time

        if (dragging != null)
        {
            if (mouse.leftButton.isPressed)
            {
                float t = Snap(hit.Time);
                if (t != dragging.Time) path.Retime(dragging, t);
                playhead = t;
            }
            else
            {
                // never two keys on one frame: step off the other one
                float frame = 1f / FrameRate;
                for (int guard = 0; guard < 100; guard++)
                {
                    int other = path.Near(dragging.Time, frame * 0.5f);
                    if (other < 0 || path[other] == dragging) break;
                    path.Retime(dragging, dragging.Time + frame);
                }
                playhead = dragging.Time;
                dragging = null;
            }
        }
        else if (scrubbing)
        {
            if (mouse.leftButton.isPressed)
            {
                playhead = Snap(hit.Time);
                FollowPath();
            }
            else scrubbing = false;
        }
    }

    [HideFromIl2Cpp]
    void SetMouseMode(bool on)
    {
        mouseMode = on;
        dragging = null;
        scrubbing = false;
        hover = -1;
        Cursor.lockState = on ? CursorLockMode.None : placeLock;
        Cursor.visible = on || placeCursor;
    }

    /// The fly camera onto the path at the playhead.
    [HideFromIl2Cpp]
    void FollowPath()
    {
        if (path.Sample(playhead, pose)) UsePose(pose);
    }

    [HideFromIl2Cpp]
    void UsePose(float[] p)
    {
        flyPos = new Vector3(p[0], p[1], p[2]);
        yaw = p[3];
        pitch = Mathf.Clamp(p[4], -89f, 89f);
        flyFov = Mathf.Clamp(p[5], 15f, 110f);
        Overlay.SetFlyInfo(flyFov, flySpeed);
    }

    [HideFromIl2Cpp]
    void FlyPose(float[] p)
    {
        p[0] = flyPos.x; p[1] = flyPos.y; p[2] = flyPos.z;
        p[3] = yaw; p[4] = pitch; p[5] = flyFov;
    }

    /// Entering and leaving the editor (placing switched to or from mode 4, or placing ending).
    [HideFromIl2Cpp]
    void ShowKeyEditor(bool on)
    {
        if (!on)
        {
            StopPlay();
            if (mouseMode) SetMouseMode(false);
        }
        Timeline.Show(on);
    }

    // ----- recording -----

    /// The path's pose for this video frame; false once the path ended and stopped the recording.
    [HideFromIl2Cpp]
    bool PathPose(out Vector3 pos, out Quaternion rot, out float camFov)
    {
        if (path.Count == 0)
        {
            pos = tripodPos; rot = tripodRot; camFov = fov;
            return true;
        }
        float t = (float)(recorder.Seconds - pathStart);
        var end = Plugin.PathEndMode.Value;
        if (end == PathEnd.Loop && path.End > 0.05f && t > path.End) t %= path.End;
        else if (end == PathEnd.Stop && path.Count > 1 && t > path.End + 0.25f)
        {
            recorder.Stop("the keyframe path ended");
            pos = default; rot = default; camFov = 0f;
            return false;
        }
        path.Sample(t, pose);
        pos = new Vector3(pose[0], pose[1], pose[2]);
        rot = Quaternion.Euler(Mathf.Clamp(pose[4], -89f, 89f), pose[3], 0f);
        camFov = Mathf.Clamp(pose[5], 15f, 110f);
        // the fallback pose and what Fixed / Aim / Follow start from when the panel switches mode
        tripodPos = pos; tripodRot = rot; fov = camFov;
        return true;
    }

    /// I while recording in mode 4: the path starts over from here.
    [HideFromIl2Cpp]
    void RestartPath()
    {
        if (!planted || mode != Mode.Keys || !recorder.Recording || path.Count == 0) return;
        pathStart = recorder.Seconds;
        Overlay.Toast("Keyframe path restarted");
    }

    static string Seconds(float t) => t.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
}
