using System;
using System.Globalization;
using System.Text;
using UnityEngine;

namespace TripodCam;

/// Where a tripod's camera is this frame, worked out from its settings: the same maths as the recording (Fixed, Aim at
/// Flippy, Follow Flippy, Keyframes), with its own smoothing, every frame. The camera model uses it (yours, and other
/// players' through FIH Sync), so the model moves smoothly even when the video records fewer frames than the game draws.
/// Also turns a tripod into a short line of text for FIH Sync and back (checked, nothing out of range gets in).
internal sealed class TripodPose
{
    public TripodDriver.Mode Mode = TripodDriver.Mode.Fixed;
    public Vector3 Pos, FollowOffset;
    public Quaternion Rot = Quaternion.identity;
    public float AimHeight = 0.3f, Smoothing = 0.35f;
    public KeyPath Path = new();
    public PathEnd End;
    public int Take; // which recording this is: a new number starts the path over

    readonly float[] sample = new float[KeyPath.Channels];
    Vector3 smoothPos, smoothVel;
    Quaternion smoothRot;
    bool ready;

    public void Restart() => ready = false;

    /// `flippy` is that player's Flippy, `pathTime` the seconds into the keyframe path.
    public void Get(Vector3 flippy, float pathTime, float dt, out Vector3 pos, out Quaternion rot)
    {
        dt = Mathf.Max(dt, 1e-4f);
        switch (Mode)
        {
            case TripodDriver.Mode.Keys when Path.Count > 0:
                float t = pathTime;
                if (End == PathEnd.Loop && Path.End > 0.05f && t > Path.End) t %= Path.End;
                Path.Sample(t, sample);
                pos = new Vector3(sample[0], sample[1], sample[2]);
                rot = Quaternion.Euler(Mathf.Clamp(sample[4], -89f, 89f), sample[3], 0f);
                return;
            case TripodDriver.Mode.Aim:
            case TripodDriver.Mode.Follow:
                break;
            default:
                pos = Pos;
                rot = Rot;
                return;
        }
        if (Mode == TripodDriver.Mode.Follow)
        {
            var want = flippy + FollowOffset;
            if (!ready) { smoothPos = want; smoothVel = Vector3.zero; }
            smoothPos = Smoothing <= 0f ? want : Vector3.SmoothDamp(smoothPos, want, ref smoothVel, Smoothing * 0.6f, Mathf.Infinity, dt);
            pos = smoothPos;
        }
        else pos = Pos;
        var dir = flippy + Vector3.up * AimHeight - pos;
        var look = dir.sqrMagnitude > 1e-6f ? Quaternion.LookRotation(dir, Vector3.up) : Rot;
        if (!ready) { smoothRot = look; ready = true; }
        smoothRot = Smoothing <= 0f ? look : Quaternion.Slerp(smoothRot, look, 1f - Mathf.Exp(-dt / (Smoothing * 0.25f)));
        rot = smoothRot;
    }

    // ----- sharing -----

    const string Tag = "TC1";
    const int MaxKeys = 256;

    public string Write()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder(128);
        void F(float v) => sb.Append(';').Append(v.ToString("R", c));
        sb.Append(Tag).Append(';').Append((int)Mode).Append(';').Append(Take).Append(';').Append((int)End);
        F(Pos.x); F(Pos.y); F(Pos.z);
        F(Rot.x); F(Rot.y); F(Rot.z); F(Rot.w);
        F(FollowOffset.x); F(FollowOffset.y); F(FollowOffset.z);
        F(AimHeight); F(Smoothing);
        int n = Mode == TripodDriver.Mode.Keys ? Math.Min(Path.Count, MaxKeys) : 0;
        sb.Append(';').Append(n);
        for (int i = 0; i < n; i++)
        {
            var k = Path[i];
            F(k.Time);
            for (int ch = 0; ch < KeyPath.Channels; ch++) F(k.Pose[ch]);
        }
        return sb.ToString();
    }

    /// Another player's tripod; null when the text is broken or out of range.
    public static TripodPose Read(string text)
    {
        try
        {
            var parts = text.Split(';');
            if (parts.Length < 17 || parts[0] != Tag) return null;
            int at = 1;
            int I() => int.Parse(parts[at++], NumberStyles.Integer, CultureInfo.InvariantCulture);
            float F(float min, float max)
            {
                float v = float.Parse(parts[at++], NumberStyles.Float, CultureInfo.InvariantCulture);
                if (!float.IsFinite(v)) throw new FormatException("not a number");
                return Math.Clamp(v, min, max);
            }
            const float Far = 100000f;
            var p = new TripodPose();
            int mode = I();
            if (mode < 0 || mode > (int)TripodDriver.Mode.Keys) return null;
            p.Mode = (TripodDriver.Mode)mode;
            p.Take = I();
            int end = I();
            p.End = end == (int)PathEnd.Loop ? PathEnd.Loop : PathEnd.Hold;
            p.Pos = new Vector3(F(-Far, Far), F(-Far, Far), F(-Far, Far));
            var q = new Quaternion(F(-1f, 1f), F(-1f, 1f), F(-1f, 1f), F(-1f, 1f));
            float len = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            p.Rot = len > 0.01f ? new Quaternion(q.x / len, q.y / len, q.z / len, q.w / len) : Quaternion.identity;
            p.FollowOffset = new Vector3(F(-1000f, 1000f), F(-1000f, 1000f), F(-1000f, 1000f));
            p.AimHeight = F(-5f, 5f);
            p.Smoothing = F(0f, 2f);
            int n = I();
            if (n < 0 || n > MaxKeys || parts.Length != at + n * (1 + KeyPath.Channels)) return null;
            var pose = new float[KeyPath.Channels];
            for (int i = 0; i < n; i++)
            {
                float time = F(0f, 3600f);
                pose[0] = F(-Far, Far); pose[1] = F(-Far, Far); pose[2] = F(-Far, Far);
                pose[3] = F(-100000f, 100000f); pose[4] = F(-89f, 89f); pose[5] = F(15f, 110f);
                p.Path.Add(time, pose);
            }
            if (p.Mode == TripodDriver.Mode.Keys && n == 0) p.Mode = TripodDriver.Mode.Fixed;
            return p;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
