// Builds Tripod Cam's shader bundle and checks the NV12 shader against a CPU version of the same maths.
// Unity.exe -batchmode -projectPath unity -buildTarget Win64 -executeMethod TripodCamBuild.BuildAndTest -logFile <log>
// Output: build\bundle\tripodcam.shaders (embedded into TripodCam.dll), _private\enc\shader_test.txt and
// _private\enc\nv12_1080p.bin (one NV12 test frame for tests\EncoderTest).
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

public static class TripodCamBuild
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Application.dataPath, "..", ".."));
    static readonly StringBuilder report = new StringBuilder();
    static int failures;

    public static void BuildAndTest()
    {
        try
        {
            Build();
            Test();
        }
        catch (Exception e)
        {
            failures++;
            Line("FAILED: " + e);
        }
        Directory.CreateDirectory(Path.Combine(Root, "_private", "enc"));
        File.WriteAllText(Path.Combine(Root, "_private", "enc", "shader_test.txt"), report.ToString());
        EditorApplication.Exit(failures == 0 ? 0 : 1);
    }

    static void Build()
    {
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.StandaloneWindows64, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.StandaloneWindows64, new[] { GraphicsDeviceType.Direct3D11, GraphicsDeviceType.Direct3D12 });
        string output = Path.Combine(Root, "build", "bundle");
        Directory.CreateDirectory(output);
        var bundle = new AssetBundleBuild { assetBundleName = "tripodcam.shaders", assetNames = new[] { "Assets/Shaders/TripodCamNV12.shader" } };
        var manifest = BuildPipeline.BuildAssetBundles(output, new[] { bundle },
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        if (manifest == null) throw new Exception("bundle build failed");
        Line($"bundle: {new FileInfo(Path.Combine(output, "tripodcam.shaders")).Length} bytes");
    }

    static void Test()
    {
        PlayerSettings.colorSpace = ColorSpace.Linear; // like the game
        var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/Shaders/TripodCamNV12.shader");
        Line($"colour space {QualitySettings.activeColorSpace}, device {SystemInfo.graphicsDeviceType}, shader supported {shader.isSupported}");
        var material = new Material(shader);
        Run(material, 16, 8);
        var frame = Run(material, 1920, 1080);
        File.WriteAllBytes(Path.Combine(Root, "_private", "enc", "nv12_1080p.bin"), frame);
        Line(failures == 0 ? "ALL OK" : $"{failures} check(s) failed");
    }

    // Test picture, top row first: a white bar on top, red / green on the upper half, blue and a grey ramp below.
    static Color32 Pixel(int x, int top, int w, int h)
    {
        if (top < h / 8) return new Color32(255, 255, 255, 255);
        if (top < h / 2) return x < w / 2 ? new Color32(220, 40, 40, 255) : new Color32(40, 200, 60, 255);
        if (x < w / 2) return new Color32(40, 70, 220, 255);
        byte g = (byte)((x - w / 2) * 255 / (w / 2 - 1));
        return new Color32(g, g, g, 255);
    }

    static byte[] Run(Material material, int w, int h)
    {
        var texture = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { filterMode = FilterMode.Point };
        var pixels = new Color32[w * h];
        for (int top = 0; top < h; top++)
            for (int x = 0; x < w; x++)
                pixels[(h - 1 - top) * w + x] = Pixel(x, top, w, h); // Unity rows start at the bottom
        texture.SetPixels32(pixels);
        texture.Apply();

        // Like the game: the tripod camera renders into a default ARGB32 texture (sRGB in a linear project).
        var source = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
        source.Create();
        Graphics.Blit(texture, source);
        var target = new RenderTexture(w, h * 3 / 2, 0, RenderTextureFormat.R8, RenderTextureReadWrite.Linear);
        target.Create();
        material.SetVector("_Size", new Vector4(w, h, source.sRGB ? 1 : 0, 0));
        Graphics.Blit(source, target, material);
        var request = AsyncGPUReadback.Request(target, 0, TextureFormat.R8);
        request.WaitForCompletion();
        if (request.hasError) throw new Exception("readback failed");
        byte[] got = request.GetData<byte>().ToArray();

        byte[] want = Expected(w, h);
        int worst = 0, worstAt = -1;
        for (int i = 0; i < want.Length; i++)
        {
            int d = Math.Abs(got[i] - want[i]);
            if (d > worst) { worst = d; worstAt = i; }
        }
        bool ok = got.Length == want.Length && worst <= 1;
        if (!ok) failures++;
        Line($"{w}x{h}: source sRGB {source.sRGB}, {got.Length} bytes, worst difference {worst}" +
             (worstAt >= 0 ? $" at byte {worstAt} (got {got[worstAt]}, want {want[worstAt]})" : "") + (ok ? " OK" : " FAIL"));
        if (w <= 16)
        {
            Line("  Y rows (top first): " + Rows(got, w, 0, h));
            Line("  UV rows:            " + Rows(got, w, h, h / 2));
        }
        UnityEngine.Object.DestroyImmediate(texture);
        source.Release();
        target.Release();
        return got;
    }

    static byte[] Expected(int w, int h)
    {
        var nv12 = new byte[w * h * 3 / 2];
        for (int top = 0; top < h; top++)
            for (int x = 0; x < w; x++)
            {
                var c = Pixel(x, top, w, h);
                nv12[top * w + x] = Round(16 + 219 * Luma(c.r / 255.0, c.g / 255.0, c.b / 255.0));
            }
        for (int by = 0; by < h / 2; by++)
            for (int bx = 0; bx < w / 2; bx++)
            {
                double r = 0, g = 0, b = 0;
                for (int k = 0; k < 4; k++)
                {
                    var c = Pixel(bx * 2 + (k & 1), by * 2 + (k >> 1), w, h);
                    r += c.r / 255.0 / 4; g += c.g / 255.0 / 4; b += c.b / 255.0 / 4;
                }
                double y = Luma(r, g, b);
                nv12[w * h + by * w + bx * 2] = Round(128 + 224 * (b - y) / 1.8556);
                nv12[w * h + by * w + bx * 2 + 1] = Round(128 + 224 * (r - y) / 1.5748);
            }
        return nv12;
    }

    static double Luma(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;
    static byte Round(double v) => (byte)Math.Max(0, Math.Min(255, Math.Round(v)));

    static string Rows(byte[] data, int w, int first, int count)
    {
        var s = new StringBuilder();
        for (int r = first; r < first + count; r++)
        {
            s.Append('[');
            for (int x = 0; x < w; x++) s.Append(data[r * w + x]).Append(x < w - 1 ? " " : "");
            s.Append("] ");
        }
        return s.ToString();
    }

    static void Line(string s)
    {
        report.AppendLine(s);
        Debug.Log("[TripodCamBuild] " + s);
    }
}
