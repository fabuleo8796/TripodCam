using System;
using System.Runtime.InteropServices;

namespace TripodCam;

/// Writes an H.264 MP4 with Windows Media Foundation's Sink Writer, with the GPU encoder (NVENC / Quick Sync / AMF)
/// when there is one. Frames go in either as NV12 (the encoder's own format, made on the GPU by the NV12 shader: top row
/// first, nothing left to convert) or as 32-bit BGRA, bottom row first (how Unity reads textures back), which the Sink
/// Writer converts on the CPU. With sound, a second stream takes 48 kHz 16-bit stereo PCM (AudioCapture) and Windows'
/// own AAC encoder turns it into 192 kbps AAC. COM is called through raw vtable slots, so it needs nothing from .NET's
/// COM support. Use it from one thread only.
internal sealed unsafe class MfEncoder : IDisposable
{
    const uint MfVersion = 0x00020070, StartupFull = 0;

    static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid SubType = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid H264 = new("34363248-0000-0010-8000-00aa00389b71");
    static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00aa00389b71");
    static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");
    static readonly Guid YuvMatrix = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    static readonly Guid NominalRange = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    static readonly Guid Primaries = new("dbfbe4d7-0740-4ee0-8192-850ab0e21935");
    static readonly Guid TransferFunction = new("5fb0fce9-be5c-4935-a811-ec838f8eed93");
    static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0");
    static readonly Guid DisableThrottling = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    static readonly Guid Audio = new("73647561-0000-0010-8000-00aa00389b71");
    static readonly Guid Aac = new("00001610-0000-0010-8000-00aa00389b71");
    static readonly Guid Pcm = new("00000001-0000-0010-8000-00aa00389b71");
    static readonly Guid AudioChannels = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    static readonly Guid AudioRate = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    static readonly Guid AudioBytesPerSecond = new("1aab75c8-cfef-451c-ab95-ac034b8e1731");
    static readonly Guid AudioBlockAlign = new("322de230-9eeb-43bd-ab7a-ff412251541d");
    static readonly Guid AudioBits = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFShutdown();
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IntPtr attributes, uint size);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfplat.dll")] static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);

    readonly int width, height, frameBytes;
    readonly bool nv12, sound;
    IntPtr writer;
    uint stream, soundStream;
    bool started, finished;

    public bool Hardware { get; }

    public MfEncoder(string path, int width, int height, int fps, int bitrate, bool tryHardware, bool nv12, bool sound = false)
    {
        this.width = width;
        this.height = height;
        this.nv12 = nv12;
        this.sound = sound;
        frameBytes = nv12 ? width * height * 3 / 2 : width * height * 4;
        Check(MFStartup(MfVersion, StartupFull), "MFStartup");
        started = true;
        try
        {
            Open(path, fps, bitrate, tryHardware);
            Hardware = tryHardware;
        }
        catch when (tryHardware)
        {
            Release(ref writer);
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            Open(path, fps, bitrate, false);
        }
    }

    void Open(string path, int fps, int bitrate, bool hardware)
    {
        IntPtr attributes = IntPtr.Zero, output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            Check(MFCreateAttributes(out attributes, 2), "MFCreateAttributes");
            Check(SetUInt32(attributes, HardwareTransforms, hardware ? 1u : 0u), "hardware attribute");
            // With two streams the writer would otherwise make WriteSample wait for the stream that's behind, and both
            // are written from the same thread: a game hitch with no frames (sound still coming) would lock it up.
            if (sound) Check(SetUInt32(attributes, DisableThrottling, 1), "throttling attribute");
            Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, attributes, out writer), "MFCreateSinkWriterFromURL");

            Check(MFCreateMediaType(out output), "output type");
            Check(SetGuid(output, MajorType, Video), "output major");
            Check(SetGuid(output, SubType, H264), "output subtype");
            Check(SetUInt32(output, AvgBitrate, (uint)bitrate), "bitrate");
            Check(SetUInt32(output, InterlaceMode, 2), "interlace");
            Check(SetUInt64(output, FrameSize, Pack((uint)width, (uint)height)), "frame size");
            Check(SetUInt64(output, FrameRate, Pack((uint)fps, 1)), "frame rate");
            Check(SetUInt64(output, PixelAspect, Pack(1, 1)), "pixel aspect");
            SetBt709(output);
            uint index;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(writer, 3))(writer, output, &index), "AddStream");
            stream = index;

            Check(MFCreateMediaType(out input), "input type");
            Check(SetGuid(input, MajorType, Video), "input major");
            Check(SetGuid(input, SubType, nv12 ? Nv12 : Rgb32), "input subtype");
            Check(SetUInt32(input, InterlaceMode, 2), "input interlace");
            Check(SetUInt64(input, FrameSize, Pack((uint)width, (uint)height)), "input size");
            Check(SetUInt64(input, FrameRate, Pack((uint)fps, 1)), "input rate");
            Check(SetUInt64(input, PixelAspect, Pack(1, 1)), "input aspect");
            if (nv12)
            {
                Check(SetUInt32(input, DefaultStride, (uint)width), "stride"); // top row first, Y plane then UV plane
                SetBt709(input);
            }
            else Check(SetUInt32(input, DefaultStride, unchecked((uint)(-width * 4))), "stride"); // negative = bottom row first
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(writer, 4))(writer, stream, input, IntPtr.Zero), "SetInputMediaType");
            if (sound) AddSound();

            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(writer, 5))(writer), "BeginWriting");
        }
        finally
        {
            Release(ref attributes);
            Release(ref output);
            Release(ref input);
        }
    }

    // AAC out (192 kbps), 16-bit PCM in, both 48 kHz stereo: the formats Windows' AAC encoder takes.
    void AddSound()
    {
        IntPtr output = IntPtr.Zero, input = IntPtr.Zero;
        try
        {
            Check(MFCreateMediaType(out output), "sound output type");
            Check(SetGuid(output, MajorType, Audio), "sound output major");
            Check(SetGuid(output, SubType, Aac), "sound output subtype");
            Check(SetUInt32(output, AudioBits, 16), "sound bits");
            Check(SetUInt32(output, AudioRate, AudioCapture.Rate), "sound rate");
            Check(SetUInt32(output, AudioChannels, AudioCapture.Channels), "sound channels");
            Check(SetUInt32(output, AudioBytesPerSecond, 24000), "sound bitrate");
            uint index;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Slot(writer, 3))(writer, output, &index), "AddStream (sound)");
            soundStream = index;

            Check(MFCreateMediaType(out input), "sound input type");
            Check(SetGuid(input, MajorType, Audio), "sound input major");
            Check(SetGuid(input, SubType, Pcm), "sound input subtype");
            Check(SetUInt32(input, AudioBits, 16), "sound input bits");
            Check(SetUInt32(input, AudioRate, AudioCapture.Rate), "sound input rate");
            Check(SetUInt32(input, AudioChannels, AudioCapture.Channels), "sound input channels");
            Check(SetUInt32(input, AudioBlockAlign, AudioCapture.FrameBytes), "sound block");
            Check(SetUInt32(input, AudioBytesPerSecond, AudioCapture.Rate * AudioCapture.FrameBytes), "sound input bytes");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, int>)Slot(writer, 4))(writer, soundStream, input, IntPtr.Zero), "SetInputMediaType (sound)");
        }
        finally
        {
            Release(ref output);
            Release(ref input);
        }
    }

    /// One frame: width*height*1.5 bytes of NV12, or width*height BGRA pixels (bottom row first). Times are in 100 ns units from the start.
    public void Write(byte[] pixels, long time, long duration)
    {
        fixed (byte* data = pixels) WriteSample(stream, data, frameBytes, time, duration);
    }

    /// Sound: `frames` frames of 16-bit stereo PCM at `data`, starting `time` (100 ns units) from the start.
    public void WriteAudio(IntPtr data, int frames, long time)
    {
        if (!sound) throw new InvalidOperationException("this video has no sound stream");
        WriteSample(soundStream, (byte*)data, frames * AudioCapture.FrameBytes, time, frames * 10_000_000L / AudioCapture.Rate);
    }

    void WriteSample(uint index, byte* data, int bytes, long time, long duration)
    {
        if (finished) throw new InvalidOperationException("already finished");
        IntPtr buffer = IntPtr.Zero, sample = IntPtr.Zero;
        try
        {
            Check(MFCreateMemoryBuffer((uint)bytes, out buffer), "MFCreateMemoryBuffer");
            byte* dest;
            uint max, current;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(buffer, 3))(buffer, &dest, &max, &current), "Lock");
            Buffer.MemoryCopy(data, dest, max, bytes);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(buffer, 4))(buffer);
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(buffer, 6))(buffer, (uint)bytes), "SetCurrentLength");

            Check(MFCreateSample(out sample), "MFCreateSample");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(sample, 42))(sample, buffer), "AddBuffer");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 36))(sample, time), "SetSampleTime");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(sample, 38))(sample, duration), "SetSampleDuration");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, int>)Slot(writer, 6))(writer, index, sample), "WriteSample");
        }
        finally
        {
            Release(ref sample);
            Release(ref buffer);
        }
    }

    /// Finishes the MP4 (writes its index). The file isn't playable until this has run.
    public void Finish()
    {
        if (finished || writer == IntPtr.Zero) return;
        finished = true;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(writer, 11))(writer), "Finalize");
    }

    public void Dispose()
    {
        Release(ref writer);
        if (started)
        {
            started = false;
            MFShutdown();
        }
    }

    static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

    static ulong Pack(uint high, uint low) => ((ulong)high << 32) | low;

    // HD video colours: BT.709 matrix, primaries and transfer, limited range (16-235), so players show them right.
    static void SetBt709(IntPtr type)
    {
        SetUInt32(type, YuvMatrix, 1);
        SetUInt32(type, NominalRange, 2);
        SetUInt32(type, Primaries, 2);
        SetUInt32(type, TransferFunction, 5);
    }

    // IMFAttributes::SetUINT32 / SetUINT64 / SetGUID (slots 21, 22, 24; IMFMediaType inherits them).
    static int SetUInt32(IntPtr attributes, Guid key, uint value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(attributes, 21))(attributes, &key, value);

    static int SetUInt64(IntPtr attributes, Guid key, ulong value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(attributes, 22))(attributes, &key, value);

    static int SetGuid(IntPtr attributes, Guid key, Guid value) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(attributes, 24))(attributes, &key, &value);

    static void Release(ref IntPtr com)
    {
        if (com == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);
        com = IntPtr.Zero;
    }

    static void Check(int hr, string what)
    {
        if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
    }
}
