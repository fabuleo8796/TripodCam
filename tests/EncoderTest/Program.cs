// Offline test of MfEncoder: 4 s of 1080p30 synthetic frames (a moving gradient, a white bar at the TOP of the
// picture so upside-down output is obvious) written with and without the GPU encoder, timed. Then the same with
// NV12 input, from the frame the Unity shader test dumped (nv12_1080p.bin in the out folder), scrolled sideways.
//
// dotnet EncoderTest.dll <out folder>
using System;
using System.Diagnostics;
using System.IO;
using TripodCam;

static class Program
{
    static int Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        foreach (bool hw in new[] { true, false })
        {
            string path = Path.Combine(dir, hw ? "test_hw.mp4" : "test_sw.mp4");
            if (File.Exists(path)) File.Delete(path);
            const int W = 1920, H = 1080, Fps = 30, Frames = 120;
            var pixels = new byte[W * H * 4];
            var watch = Stopwatch.StartNew();
            using (var enc = new MfEncoder(path, W, H, Fps, 16_000_000, hw, false))
            {
                Console.WriteLine($"{(hw ? "hardware asked" : "software")}: opened in {watch.ElapsedMilliseconds} ms, hardware used = {enc.Hardware}");
                watch.Restart();
                for (int f = 0; f < Frames; f++)
                {
                    Fill(pixels, W, H, f);
                    enc.Write(pixels, f * 10_000_000L / Fps, 10_000_000L / Fps);
                }
                double perFrame = watch.Elapsed.TotalMilliseconds / Frames;
                watch.Restart();
                enc.Finish();
                Console.WriteLine($"  {Frames} frames, {perFrame:0.00} ms/frame, finish {watch.ElapsedMilliseconds} ms, file {new FileInfo(path).Length / 1024} KB");
            }
        }
        string frame = Path.Combine(dir, "nv12_1080p.bin");
        if (!File.Exists(frame)) { Console.WriteLine("no nv12_1080p.bin, skipping NV12"); return 0; }
        var source = File.ReadAllBytes(frame);
        foreach (bool hw in new[] { true, false })
        {
            string path = Path.Combine(dir, hw ? "test_nv12_hw.mp4" : "test_nv12_sw.mp4");
            if (File.Exists(path)) File.Delete(path);
            const int W = 1920, H = 1080, Fps = 30, Frames = 120;
            var nv12 = new byte[source.Length];
            var watch = Stopwatch.StartNew();
            using (var enc = new MfEncoder(path, W, H, Fps, 16_000_000, hw, true))
            {
                Console.WriteLine($"NV12 {(hw ? "hardware asked" : "software")}: opened in {watch.ElapsedMilliseconds} ms, hardware used = {enc.Hardware}");
                double fill = 0;
                watch.Restart();
                for (int f = 0; f < Frames; f++)
                {
                    var t = Stopwatch.StartNew();
                    Scroll(source, nv12, W, H, f < Frames / 2 ? 0 : (f - Frames / 2) * 8); // still for 2 s (colour check), then moves
                    fill += t.Elapsed.TotalMilliseconds;
                    enc.Write(nv12, f * 10_000_000L / Fps, 10_000_000L / Fps);
                }
                double perFrame = (watch.Elapsed.TotalMilliseconds - fill) / Frames;
                watch.Restart();
                enc.Finish();
                Console.WriteLine($"  {Frames} frames, {perFrame:0.00} ms/frame (encode only), finish {watch.ElapsedMilliseconds} ms, file {new FileInfo(path).Length / 1024} KB");
            }
        }
        return 0;
    }

    // Shifts both NV12 planes left by an even number of pixels (wrapping round).
    static void Scroll(byte[] src, byte[] dst, int w, int h, int shift)
    {
        shift = (shift % w) & ~1;
        for (int row = 0; row < h * 3 / 2; row++)
        {
            int o = row * w;
            Buffer.BlockCopy(src, o + shift, dst, o, w - shift);
            Buffer.BlockCopy(src, o, dst, o + w - shift, shift);
        }
    }

    // BGRA, bottom row first: row 0 is the bottom of the picture.
    static void Fill(byte[] p, int w, int h, int frame)
    {
        for (int y = 0; y < h; y++)
        {
            bool topBar = y > h - 120;
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (topBar) { p[i] = 255; p[i + 1] = 255; p[i + 2] = 255; }
                else
                {
                    p[i] = (byte)((x + frame * 8) & 255);     // B
                    p[i + 1] = (byte)(y * 255 / h);            // G
                    p[i + 2] = (byte)(255 - (x * 255 / w));    // R
                }
                p[i + 3] = 255;
            }
        }
    }
}
