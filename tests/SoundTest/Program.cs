using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using TripodCam;

// Runs AudioCapture and MfEncoder's sound stream for real, outside the game. It plays two quiet 1 kHz beeps through
// waveOut (0.5 s and 1.5 s after "video time 0"), captures this process's own sound, simulates a 1.6 s encoder stall
// (nothing drained while the second beep plays: the capture thread must keep it), and writes a small MP4 whose picture
// turns white while each beep should play.
// Then it decodes the MP4 again and checks the beeps and the white frames line up.
// dotnet SoundTest.dll <output.mp4>
static unsafe class Program
{
    const double Beep1 = 0.5, Beep2 = 1.5, BeepLength = 0.25, HitchFrom = 2.0, HitchTo = 3.6, End = 4.0;
    const int W = 320, H = 180, Fps = 30;

    static int failures;

    static int Main(string[] args)
    {
        var path = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        if (File.Exists(path)) File.Delete(path);
        CoInitializeEx(IntPtr.Zero, 0);

        AudioCapture audio;
        try { audio = new AudioCapture(); }
        catch (Exception e) { Console.WriteLine("FAIL capture didn't start: " + e.Message); return 1; }
        Expect(true, $"capture started (Windows application loopback, {audio.BufferSeconds * 1000:0} ms buffer)");

        var encoder = new MfEncoder(path, W, H, Fps, 1_000_000, true, true, sound: true);
        Expect(true, $"encoder opened with a sound stream ({(encoder.Hardware ? "GPU" : "CPU")} video)");

        Thread.Sleep(200); // sound from before the video started is thrown away

        var captured = new List<short>();
        long firstPacketTime = -1;
        Action<IntPtr, int, long> write = (data, frames, time) =>
        {
            if (firstPacketTime < 0) firstPacketTime = time;
            Expect(time == captured.Count / 2 * 10_000_000L / AudioCapture.Rate, null); // one unbroken timeline
            encoder.WriteAudio(data, frames, time);
            var s = (short*)data;
            for (int i = 0; i < frames * 2; i++) captured.Add(s[i]);
        };

        var player = OpenPlayer(End + 0.5, out var header); // opening waveOut can take most of a second
        long origin = AudioCapture.Now();
        audio.Origin = origin;
        Mm(waveOutWrite(player, header, 48), "waveOutWrite");
        double playedAt = (AudioCapture.Now() - origin) / 1e7;

        var frame = new byte[W * H * 3 / 2];
        int written = 0;
        bool hitchReported = false;
        while (true)
        {
            double now = (AudioCapture.Now() - origin) / 1e7;
            if (now >= End) break;
            while (written / (double)Fps <= now)
            {
                double t = written / (double)Fps;
                bool white = Beeping(t);
                Array.Fill(frame, white ? (byte)235 : (byte)60, 0, W * H);
                Array.Fill(frame, (byte)128, W * H, W * H / 2);
                encoder.Write(frame, written * 10_000_000L / Fps, 10_000_000L / Fps);
                written++;
            }
            if (now >= HitchFrom && now < HitchTo)
            {
                if (!hitchReported) { hitchReported = true; Console.WriteLine($"     (not draining for {HitchTo - HitchFrom:0.0} s, like a stalled encoder)"); }
                Thread.Sleep(10);
                continue;
            }
            audio.Drain(write);
            Thread.Sleep(10);
        }
        audio.Dispose();
        audio.Drain(write);
        Expect(audio.Error == null, $"no capture errors ({audio.Error})");
        encoder.Finish();
        encoder.Dispose();
        waveOutReset(player);
        double length = captured.Count / 2 / (double)AudioCapture.Rate;
        Console.WriteLine($"     waveOut started {playedAt * 1000:0} ms after time 0; first sound at {firstPacketTime / 1e4:0} ms; " +
                          $"{length:0.000} s of sound, {audio.PaddedSeconds:0.000} s filled in, {audio.DroppedSeconds:0.000} s dropped");

        // the captured timeline
        var heard = Onsets(captured.ToArray(), 0);
        Expect(heard.Count == 2, $"two beeps captured (found {heard.Count}: {string.Join(", ", heard.ConvertAll(x => $"{x:0.000}"))} s)");
        if (heard.Count == 2)
        {
            double latency = heard[0] - Beep1 - playedAt;
            Console.WriteLine($"     beep heard {latency * 1000:0} ms after it was handed to waveOut (Windows' output delay)");
            Expect(latency > -0.005 && latency < 0.25, "the capture's clock matches Stopwatch (beep within 0-250 ms of being played)");
            Expect(Math.Abs(heard[1] - heard[0] - (Beep2 - Beep1)) < 0.004, $"beeps {(heard[1] - heard[0]) * 1000:0.0} ms apart (should be 1000)");
        }
        Expect(Math.Abs(length - End) < 0.08, $"the sound is as long as the recording ({length:0.000} s, should be ~{End} s)");
        Expect(audio.PaddedSeconds < 0.1, $"nothing lost during the stall ({audio.PaddedSeconds * 1000:0} ms filled in)");

        // the MP4, decoded again
        MFStartup(0x00020070, 0);
        var decoded = DecodeSound(path, out double firstSample);
        var decodedOnsets = Onsets(decoded, firstSample);
        Expect(decodedOnsets.Count == 2, $"two beeps in the MP4's sound (found {decodedOnsets.Count})");
        if (decodedOnsets.Count == 2 && heard.Count == 2)
        {
            double shift = decodedOnsets[0] - heard[0];
            Console.WriteLine($"     MP4 sound vs captured: {shift * 1000:+0;-0} ms (AAC)");
            Expect(Math.Abs(shift) < 0.025, "AAC keeps the sound where it was (within 25 ms)");
        }
        var whites = WhiteFrames(path);
        Expect(whites.Count == 2 && Math.Abs(whites[0] - Beep1) < 0.04 && Math.Abs(whites[1] - Beep2) < 0.04,
            $"picture turns white at {string.Join(", ", whites.ConvertAll(x => $"{x:0.000}"))} s");
        MFShutdown();

        Console.WriteLine($"{path} ({new FileInfo(path).Length / 1024} KB)");
        Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
        return failures;
    }

    static bool Beeping(double t) => (t >= Beep1 && t < Beep1 + BeepLength) || (t >= Beep2 && t < Beep2 + BeepLength);

    // where the sound goes loud, in seconds on its timeline (start = the timeline's first sample)
    static List<double> Onsets(short[] pcm, double start)
    {
        var list = new List<double>();
        int frames = pcm.Length / 2;
        for (int i = 0; i < frames; i++)
        {
            if (Math.Abs((int)pcm[i * 2]) < 600) continue;
            list.Add(start + i / (double)AudioCapture.Rate);
            i += AudioCapture.Rate / 2; // skip past this beep
        }
        return list;
    }

    // ----- playing: one waveOut buffer with the two beeps, starting now

    static IntPtr OpenPlayer(double seconds, out byte* header)
    {
        int frames = (int)(seconds * AudioCapture.Rate);
        var pcm = (short*)NativeMemory.AllocZeroed((nuint)(frames * 4));
        for (int i = 0; i < frames; i++)
        {
            double t = i / (double)AudioCapture.Rate;
            if (!Beeping(t)) continue;
            short v = (short)(Math.Sin(2 * Math.PI * 1000 * t) * 2000); // about -24 dB: quiet
            pcm[i * 2] = pcm[i * 2 + 1] = v;
        }
        byte* format = stackalloc byte[20];
        new Span<byte>(format, 20).Clear();
        *(ushort*)format = 1; *(ushort*)(format + 2) = 2; *(uint*)(format + 4) = AudioCapture.Rate;
        *(uint*)(format + 8) = AudioCapture.Rate * 4; *(ushort*)(format + 12) = 4; *(ushort*)(format + 14) = 16;
        Mm(waveOutOpen(out var device, 0xFFFFFFFF, format, IntPtr.Zero, IntPtr.Zero, 0), "waveOutOpen");
        header = (byte*)NativeMemory.AllocZeroed(48); // WAVEHDR
        *(void**)header = pcm;
        *(uint*)(header + 8) = (uint)(frames * 4);
        Mm(waveOutPrepareHeader(device, header, 48), "waveOutPrepareHeader");
        return device;
    }

    // ----- reading the MP4 back with Media Foundation's source reader

    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    const uint AllStreams = 0xFFFFFFFE, FirstVideo = 0xFFFFFFFC, FirstAudio = 0xFFFFFFFD, EndOfStream = 0x2;

    static short[] DecodeSound(string path, out double first)
    {
        var pcm = new List<short>();
        first = -1;
        var reader = OpenReader(path, FirstAudio, Audio, Pcm, 16);
        while (Next(reader, FirstAudio, out long time, out var bytes))
        {
            if (first < 0) first = time / 1e7;
            for (int i = 0; i + 1 < bytes.Length; i += 2) pcm.Add(BitConverter.ToInt16(bytes, i));
        }
        Release(reader);
        return pcm.ToArray();
    }

    static List<double> WhiteFrames(string path)
    {
        var list = new List<double>();
        var reader = OpenReader(path, FirstVideo, Video, Nv12, 0);
        bool wasWhite = false;
        while (Next(reader, FirstVideo, out long time, out var bytes))
        {
            bool white = bytes.Length > 0 && bytes[W * (H / 2) + W / 2] > 180;
            if (white && !wasWhite) list.Add(time / 1e7);
            wasWhite = white;
        }
        Release(reader);
        return list;
    }

    static IntPtr OpenReader(string path, uint stream, Guid major, Guid sub, uint bits)
    {
        Check(MFCreateSourceReaderFromURL(path, IntPtr.Zero, out var reader), "MFCreateSourceReaderFromURL");
        Call(reader, 4, AllStreams, 0);
        Call(reader, 4, stream, 1);
        Check(MFCreateMediaType(out var type), "MFCreateMediaType");
        Guid m = major, s = sub, mt = MajorType, st = SubType, b = AudioBits;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(type, 24))(type, &mt, &m), "major");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(type, 24))(type, &st, &s), "sub");
        if (bits > 0) Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(type, 21))(type, &b, bits), "bits");
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Slot(reader, 7))(reader, stream, null, type), "SetCurrentMediaType");
        Release(type);
        return reader;
    }

    static bool Next(IntPtr reader, uint stream, out long time, out byte[] bytes)
    {
        uint actual, flags;
        long stamp;
        IntPtr sample;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(reader, 9))(
            reader, stream, 0, &actual, &flags, &stamp, &sample), "ReadSample");
        time = stamp;
        bytes = Array.Empty<byte>();
        if (sample == IntPtr.Zero) return (flags & EndOfStream) == 0;
        IntPtr buffer;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
        byte* data; uint max, length;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &data, &max, &length), "Lock");
        bytes = new byte[length];
        Marshal.Copy((IntPtr)data, bytes, 0, (int)length);
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
        Release(buffer);
        Release(sample);
        return true;
    }

    static void Call(IntPtr com, int slot, uint a, int b) =>
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(com, slot))(com, a, b), $"slot {slot}");

    static void* Slot(IntPtr com, int index) => (*(void***)com)[index];
    static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }

    static void Mm(int result, string what)
    {
        if (result != 0) throw new InvalidOperationException($"{what} failed ({result})");
    }

    static void Expect(bool ok, string what)
    {
        if (what == null) { if (!ok) { failures++; Console.WriteLine("FAIL the sound timeline has a hole or overlap"); } return; }
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) failures++;
    }

    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint mode);
    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)] static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr device, uint id, void* format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr device, void* header, uint size);
    [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr device, void* header, uint size);
    [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr device);
}
