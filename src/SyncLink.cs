using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using UnityEngine;

namespace TripodCam;

/// Other players see your camera model through FIH Sync (a separate, optional mod), and you see theirs.
/// - While you record (with the camera model on), your tripod's settings go out as one short line of text: mode,
///   spot, aim, follow offset, smoothing and the keyframe path. Nothing goes out the rest of the time.
/// - Each player works the pose out on their side from your Flippy as they see it, so nothing is sent per frame. A
///   keyframe path starts when it arrives (a second or two after your video starts).
/// - Without FIH Sync this does nothing: the calls into it sit in NoInlining methods, so a missing FIHSync.dll is one
///   caught exception. Other players' numbers are checked and clamped (TripodPose.Read); drawing their camera counts
///   towards FIH Sync's lag shield.
internal static class SyncLink
{
    sealed class Remote
    {
        public TripodPose Pose;
        public CameraModel.Rig Rig;
        public float Since;
    }

    static readonly Dictionary<int, Remote> remote = new();
    static readonly Stopwatch watch = new();
    static bool started, linked;

    internal static void Start()
    {
        if (started) return;
        started = true;
        try { Link(); linked = true; }
        catch (Exception) { Plugin.Logger.LogInfo("FIH Sync isn't installed: other players won't see your camera model"); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void Link() => FIHSync.Share.Register("tripodcam", Version, Data, Received, Gone);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static void ReportCost(int owner, double ms) => FIHSync.Share.ReportCost(owner, ms);

    [MethodImpl(MethodImplOptions.NoInlining)]
    static EHS.PlayerRef Find(int owner) => FIHSync.Share.FindPlayer(owner);

    // the line of text is its own version; null = not recording (or the model is off): nothing to show
    static string Version() => TripodDriver.Instance != null ? TripodDriver.Instance.ShareState() : null;

    static byte[] Data() => Encoding.UTF8.GetBytes(Version() ?? "");

    static void Received(int ownerId, byte[] data, string folder)
    {
        Gone(ownerId);
        if (data == null || data.Length > 64 * 1024) return;
        var pose = TripodPose.Read(Encoding.UTF8.GetString(data));
        if (pose == null) { Plugin.Logger.LogWarning($"player {ownerId}'s tripod didn't make sense, not shown"); return; }
        remote[ownerId] = new Remote { Pose = pose, Since = Time.unscaledTime };
        Plugin.Logger.LogInfo($"showing player {ownerId}'s tripod camera ({pose.Mode}, {pose.Path.Count} keyframes)");
    }

    static void Gone(int ownerId)
    {
        if (!remote.TryGetValue(ownerId, out var r)) return;
        r.Rig?.Destroy();
        remote.Remove(ownerId);
    }

    /// Every frame (LateUpdate). A player who isn't spawned here (loading, another level) just hides theirs.
    internal static void Tick(float dt)
    {
        if (!linked || remote.Count == 0) return;
        bool show = Plugin.ShowCamera.Value;
        foreach (var kv in remote)
        {
            var r = kv.Value;
            EHS.PlayerRef p = null;
            try { p = Find(kv.Key); } catch { p = null; }
            if (p != null && EHS.PlayerRef.IsLocalPlayer(p)) p = null;
            if (!show || p == null)
            {
                r.Rig?.Show(false);
                continue;
            }
            watch.Restart();
            if (r.Rig == null || !r.Rig.Alive) r.Rig = new CameraModel.Rig($"TripodCam Camera Model (player {kv.Key})");
            r.Pose.Get(p.transform.position, Time.unscaledTime - r.Since, dt, out var pos, out var rot);
            r.Rig.Place(pos, rot, Plugin.CameraSize.Value, dt);
            r.Rig.Show(!CameraModel.TooClose(pos, Plugin.CameraSize.Value));
            try { ReportCost(kv.Key, watch.Elapsed.TotalMilliseconds); } catch { /* FIH Sync gone */ }
        }
    }
}
