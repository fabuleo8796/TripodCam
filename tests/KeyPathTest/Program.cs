using System;
using TripodCam;

// Checks KeyPath's maths outside the game: the path goes through every key, speed is continuous through the keys,
// it eases out of the first key and into the last, it never overshoots (holds stay still), yaw turns the short way,
// and retiming and removing keys keep everything in order. dotnet KeyPathTest.dll
static class Program
{
    static int failures;

    static int Main()
    {
        var path = new KeyPath();
        var p = new float[KeyPath.Channels];
        Expect(!path.Sample(1f, p), "no keys: nothing to sample");

        // four keys along a bent line, uneven times
        path.Add(0f, Pose(0, 0, 0, 10, 0, 60));
        path.Add(2f, Pose(10, 2, 0, 40, -10, 60));
        path.Add(3f, Pose(14, 2, 6, 80, -5, 40));
        path.Add(6f, Pose(20, 0, 20, 90, 0, 40));
        Expect(path.Count == 4 && path.Start == 0f && path.End == 6f, "4 keys from 0 to 6 s");

        // through every key
        for (int i = 0; i < path.Count; i++)
        {
            path.Sample(path[i].Time, p);
            Expect(Same(p, path[i].Pose), $"key {i + 1} is hit exactly");
        }
        // before and after the path
        path.Sample(-1f, p); Expect(Same(p, path[0].Pose), "before the start: first pose");
        path.Sample(9f, p); Expect(Same(p, path[3].Pose), "after the end: last pose");

        // speed continuous through the inner keys, zero at both ends
        for (int i = 1; i < 3; i++)
        {
            float t = path[i].Time;
            for (int c = 0; c < KeyPath.Channels; c++)
            {
                // each side's speed extrapolated to the key, so the acceleration nearby doesn't count
                float before = 2f * Speed(path, t - 1e-3f, c) - Speed(path, t - 2e-3f, c);
                float after = 2f * Speed(path, t + 1e-3f, c) - Speed(path, t + 2e-3f, c);
                Expect(Math.Abs(before - after) < 0.1f + 0.02f * Math.Abs(before), $"key {i + 1} channel {c}: speed {before:0.000} = {after:0.000}");
            }
        }
        for (int c = 0; c < KeyPath.Channels; c++)
        {
            Expect(Math.Abs(Speed(path, 1e-3f, c)) < 0.05f, $"eases out of the first key (channel {c})");
            Expect(Math.Abs(Speed(path, 6f - 1e-3f, c)) < 0.05f, $"eases into the last key (channel {c})");
        }

        // no overshoot: every number stays between its two keys on each piece
        for (int i = 0; i < path.Count - 1; i++)
            for (float t = path[i].Time; t <= path[i + 1].Time; t += 0.01f)
            {
                path.Sample(t, p);
                for (int c = 0; c < KeyPath.Channels; c++)
                {
                    float lo = Math.Min(path[i].Pose[c], path[i + 1].Pose[c]) - 1e-3f, hi = Math.Max(path[i].Pose[c], path[i + 1].Pose[c]) + 1e-3f;
                    if (p[c] < lo || p[c] > hi) { Expect(false, $"channel {c} overshoots at {t:0.00} s: {p[c]} not in {lo}..{hi}"); goto doneOvershoot; }
                }
            }
        Expect(true, "no overshoot anywhere");
        doneOvershoot:

        // a hold (two equal keys) stays perfectly still
        var hold = new KeyPath();
        hold.Add(0f, Pose(0, 0, 0, 0, 0, 60));
        hold.Add(2f, Pose(5, 5, 5, 30, 0, 60));
        hold.Add(4f, Pose(5, 5, 5, 30, 0, 60));
        hold.Add(6f, Pose(9, 0, 0, 0, 0, 60));
        bool still = true;
        for (float t = 2f; t <= 4f; t += 0.05f) { hold.Sample(t, p); still &= Same(p, hold[1].Pose); }
        Expect(still, "a hold between equal keys doesn't drift");

        // smooth: no jumps between samples 1/240 s apart
        float worst = 0f;
        var q = new float[KeyPath.Channels];
        path.Sample(0f, q);
        for (float t = 1f / 240f; t <= 6f; t += 1f / 240f)
        {
            path.Sample(t, p);
            for (int c = 0; c < 3; c++) worst = Math.Max(worst, Math.Abs(p[c] - q[c]));
            Array.Copy(p, q, KeyPath.Channels);
        }
        Expect(worst < 0.15f, $"no jumps (biggest step {worst:0.000} per 1/240 s)");

        // yaw: 350° to 10° turns 20° through 0, not 340° back
        var turn = new KeyPath();
        turn.Add(0f, Pose(0, 0, 0, 350, 0, 60));
        turn.Add(1f, Pose(0, 0, 0, 10, 0, 60));
        turn.Sample(0.5f, p);
        Expect(Math.Abs(KeyPath.DeltaAngle(p[3], 0f)) < 0.01f, $"yaw goes the short way (halfway {p[3]:0.0}°)");
        turn.Sample(1f, p);
        Expect(Math.Abs(KeyPath.DeltaAngle(p[3], 10f)) < 0.01f, "yaw lands on 10°");

        // retime: moving key 2 past key 3 swaps their order and the path follows
        var moved = path[1];
        int index = path.Retime(moved, 4f);
        Expect(index == 2 && path[2] == moved && path[1].Time == 3f, "retimed key re-sorts");
        path.Sample(4f, p);
        Expect(Same(p, moved.Pose), "retimed key is hit at its new time");

        // replace and remove
        Expect(path.Near(3.02f, 0.05f) == 1 && path.Near(3.5f, 0.05f) == -1, "Near finds the key at the playhead");
        path.Set(1, Pose(1, 1, 1, 1, 1, 50));
        path.Sample(3f, p);
        Expect(Same(p, Pose(1, 1, 1, 1, 1, 50)), "Set replaces a key's pose");
        int version = path.Version;
        path.RemoveAt(0);
        Expect(path.Count == 3 && path.Start == 3f && path.Version != version, "RemoveAt drops the key");

        // two keys at the same time don't break anything
        var same = new KeyPath();
        same.Add(1f, Pose(0, 0, 0, 0, 0, 60));
        same.Add(1f, Pose(5, 0, 0, 0, 0, 60));
        same.Add(2f, Pose(9, 0, 0, 0, 0, 60));
        bool finite = true;
        for (float t = 0f; t <= 3f; t += 0.01f) { same.Sample(t, p); foreach (var v in p) finite &= float.IsFinite(v); }
        Expect(finite, "keys at the same time stay finite");

        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
        return failures == 0 ? 0 : 1;
    }

    static float[] Pose(float x, float y, float z, float yaw, float pitch, float fov) => new[] { x, y, z, yaw, pitch, fov };

    static float Speed(KeyPath path, float t, int c)
    {
        var a = new float[KeyPath.Channels];
        var b = new float[KeyPath.Channels];
        const float dt = 1e-4f;
        path.Sample(t - dt, a);
        path.Sample(t + dt, b);
        return (b[c] - a[c]) / (2f * dt);
    }

    static bool Same(float[] a, float[] b)
    {
        for (int i = 0; i < a.Length; i++)
            if (Math.Abs(a[i] - b[i]) > 1e-3f) return false;
        return true;
    }

    static void Expect(bool ok, string what)
    {
        if (!ok) failures++;
        Console.WriteLine((ok ? "ok    " : "FAIL  ") + what);
    }
}
