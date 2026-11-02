using System;
using System.Collections.Generic;

namespace TripodCam;

/// The keyframe path (mode 4): camera poses at times, and the smooth move through them.
/// - A pose is six numbers: position x y z, yaw, pitch and field of view. No roll, so the horizon stays level.
/// - Between keys every number follows a cubic curve in time (Hermite) whose slopes are "auto clamped" like
///   Blender's default handles: Catmull-Rom slopes, flat where a number turns around or holds, limited so it never
///   overshoots a key (Fritsch-Carlson). Speed changes smoothly through every key, and the camera eases out of the
///   first key and into the last one.
/// - Yaw turns the short way between keys.
/// Plain C# with no Unity types, so tests\KeyPathTest checks it outside the game.
internal sealed class KeyPath
{
    public const int Channels = 6; // x, y, z, yaw, pitch, fov
    const float MinStep = 0.001f;

    internal sealed class Key
    {
        public float Time;
        public readonly float[] Pose = new float[Channels];
    }

    readonly List<Key> keys = new();
    float[] times = Array.Empty<float>();
    float[,] values = new float[0, Channels], slopes = new float[0, Channels];
    bool dirty;

    /// Bumped on every change, so the timeline and the path drawing know when to redraw.
    public int Version { get; private set; }
    public int Count => keys.Count;
    public float Start => keys.Count > 0 ? keys[0].Time : 0f;
    public float End => keys.Count > 0 ? keys[keys.Count - 1].Time : 0f;

    /// Keys in time order.
    public Key this[int i] => keys[i];

    public int Add(float time, float[] pose)
    {
        var key = new Key { Time = Math.Max(0f, time) };
        Array.Copy(pose, key.Pose, Channels);
        keys.Add(key);
        Changed();
        return keys.IndexOf(key);
    }

    public void Set(int i, float[] pose)
    {
        Array.Copy(pose, keys[i].Pose, Channels);
        Changed();
    }

    public void RemoveAt(int i)
    {
        keys.RemoveAt(i);
        Changed();
    }

    public void Clear()
    {
        keys.Clear();
        Changed();
    }

    /// Moves a key in time (keys may pass each other); returns its new index.
    public int Retime(Key key, float time)
    {
        key.Time = Math.Max(0f, time);
        Changed();
        return keys.IndexOf(key);
    }

    /// The key within `tolerance` seconds of `time` (the closest), or -1.
    public int Near(float time, float tolerance)
    {
        int best = -1;
        float bestGap = tolerance;
        for (int i = 0; i < keys.Count; i++)
        {
            float gap = Math.Abs(keys[i].Time - time);
            if (gap <= bestGap) { best = i; bestGap = gap; }
        }
        return best;
    }

    /// The pose at `time` into `result` (six numbers). Before the first key it's the first pose, after the last one
    /// the last. False when there are no keys.
    public bool Sample(float time, float[] result)
    {
        int n = keys.Count;
        if (n == 0) return false;
        Prepare();
        if (time <= times[0] || n == 1) { Row(0, result); return true; }
        if (time >= times[n - 1]) { Row(n - 1, result); return true; }
        int i = 0;
        while (i < n - 2 && time >= times[i + 1]) i++;
        float h = times[i + 1] - times[i];
        if (h < MinStep) { Row(i + 1, result); return true; }
        float s = (time - times[i]) / h, s2 = s * s, s3 = s2 * s;
        float h00 = 2f * s3 - 3f * s2 + 1f, h10 = s3 - 2f * s2 + s, h01 = 3f * s2 - 2f * s3, h11 = s3 - s2;
        for (int c = 0; c < Channels; c++)
            result[c] = h00 * values[i, c] + h10 * h * slopes[i, c] + h01 * values[i + 1, c] + h11 * h * slopes[i + 1, c];
        return true;
    }

    void Row(int i, float[] result)
    {
        for (int c = 0; c < Channels; c++) result[c] = values[i, c];
    }

    void Changed()
    {
        dirty = true;
        Version++;
        keys.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    void Prepare()
    {
        if (!dirty) return;
        dirty = false;
        int n = keys.Count;
        times = new float[n];
        values = new float[n, Channels];
        slopes = new float[n, Channels];
        for (int i = 0; i < n; i++)
        {
            times[i] = keys[i].Time;
            for (int c = 0; c < Channels; c++) values[i, c] = keys[i].Pose[c];
            if (i > 0) values[i, 3] = values[i - 1, 3] + DeltaAngle(values[i - 1, 3], keys[i].Pose[3]); // the short way round
        }
        for (int i = 1; i < n - 1; i++) // the first and last keys keep slope 0: ease out and in
        {
            float h0 = Math.Max(times[i] - times[i - 1], MinStep), h1 = Math.Max(times[i + 1] - times[i], MinStep);
            for (int c = 0; c < Channels; c++)
            {
                float d0 = (values[i, c] - values[i - 1, c]) / h0, d1 = (values[i + 1, c] - values[i, c]) / h1;
                if (d0 * d1 <= 0f) continue; // turns around or holds here: flat
                float m = (values[i + 1, c] - values[i - 1, c]) / (h0 + h1);
                float limit = 3f * Math.Min(Math.Abs(d0), Math.Abs(d1));
                slopes[i, c] = Math.Clamp(m, -limit, limit);
            }
        }
    }

    /// The shortest turn from a to b in degrees (-180..180), like Mathf.DeltaAngle.
    public static float DeltaAngle(float a, float b)
    {
        float d = (b - a) % 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }
}
