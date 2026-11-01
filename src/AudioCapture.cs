using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace TripodCam;

/// The game's sound for the video, straight from Windows: "application loopback" capture (Windows 10 2004 and newer)
/// hears only this process and what it starts (FMOD, voice chat, everything the game plays) and nothing else on the PC,
/// as 48 kHz 16-bit stereo. Nothing runs on the game's audio thread.
/// - Windows only holds 10 ms of it for us, so a small thread of our own (above normal priority, a few microseconds of
///   work a packet) wakes on Windows' event for every packet and queues it; the encoder thread takes the queue whenever
///   it likes (Drain), so a slow frame or a hitch never loses sound.
/// - Windows stamps each packet with the Stopwatch clock, which lines the sound up with the video: set Origin to the
///   video's time 0 on that clock. Until then packets are thrown away.
/// - The sound keeps its own sample count and is only nudged (silence added, or a bit dropped) when it and the packet
///   stamps drift apart by more than Slack, so it never stutters, and gaps (sound lost, nothing playing) stay in place.
/// COM is called through raw vtable slots, like MfEncoder, all of it on the capture thread.
internal sealed unsafe class AudioCapture : IDisposable
{
    public const int Rate = 48000, Channels = 2, FrameBytes = 4;
    const long Second = 10_000_000; // 100 ns units, like Media Foundation
    const long Slack = 400_000;     // 40 ms
    const int ChunkFrames = Rate / 10;

    static readonly Guid AudioClient = new("1cb9ad4c-dbfa-4c32-b178-c2f568a703b2");
    static readonly Guid AudioCaptureClient = new("c8adbd64-e71e-48a0-a4de-185c395cd317");
    static readonly Guid Unknown = new("00000000-0000-0000-c000-000000000046");
    static readonly Guid CompletionHandler = new("41d949ab-9862-444a-80f6-c261334da5eb");
    static readonly Guid AgileObject = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
    const uint Loopback = 0x00020000, EventCallback = 0x00040000; // AUDCLNT_STREAMFLAGS_*
    const uint SilentPacket = 0x2, TimestampError = 0x4;          // AUDCLNT_BUFFERFLAGS_*

    [DllImport("Mmdevapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int ActivateAudioInterfaceAsync(string path, Guid* iid, void* parameters, IntPtr handler, out IntPtr operation);
    [DllImport("kernel32.dll")] static extern IntPtr CreateEventW(IntPtr attributes, int manualReset, int initialState, IntPtr name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] static extern int CloseHandle(IntPtr handle);
    [DllImport("ole32.dll")] static extern int CoInitializeEx(IntPtr reserved, uint mode);

    readonly Thread thread;
    readonly ManualResetEventSlim started = new();
    readonly ConcurrentQueue<(byte[] Data, int Frames, long Time)> queue = new();
    readonly ConcurrentBag<byte[]> pool = new();
    volatile bool stopping;
    string failure;
    long origin; // read and written with Volatile
    IntPtr client, capture, ready;
    long written, padded, dropped; // frames, capture thread only
    static readonly byte[] silence = new byte[ChunkFrames * FrameBytes];

    /// Sound handed out so far, in seconds, and how much of it was filled in with silence or dropped to stay in line.
    public double Seconds => Volatile.Read(ref written) / (double)Rate;
    public double PaddedSeconds => Volatile.Read(ref padded) / (double)Rate;
    public double DroppedSeconds => Volatile.Read(ref dropped) / (double)Rate;

    /// How much sound Windows holds for us between reads (10 ms on Windows 11).
    public double BufferSeconds { get; private set; }

    /// Something went wrong after it started (the device went away...); the sound just stops there.
    public string Error { get; private set; }

    /// The video's time 0 on the packets' clock (see Now); 0 until the video starts.
    public long Origin
    {
        get => Volatile.Read(ref origin);
        set => Volatile.Write(ref origin, value);
    }

    /// Starts capturing this process's sound. Throws with a reason when Windows can't (too old, no audio service...).
    public AudioCapture()
    {
        thread = new Thread(Run) { IsBackground = true, Name = "TripodCam sound", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        if (!started.Wait(5000)) failure = "Windows didn't start the sound capture";
        if (failure == null) return;
        stopping = true;
        throw new InvalidOperationException(failure);
    }

    /// Now on the packets' clock (Stopwatch's, in 100 ns units).
    public static long Now() => ToTime(Stopwatch.GetTimestamp());

    public static long ToTime(long stopwatchTicks) => (long)(stopwatchTicks * ((double)Second / Stopwatch.Frequency));

    /// Any thread (the encoder's): hands every queued piece of sound to `write` (data, frames, time in 100 ns since
    /// Origin), one unbroken timeline starting at 0. The data is only valid during the call.
    public void Drain(Action<IntPtr, int, long> write)
    {
        while (queue.TryDequeue(out var chunk))
        {
            fixed (byte* data = chunk.Data) write((IntPtr)data, chunk.Frames, chunk.Time);
            pool.Add(chunk.Data);
        }
    }

    /// Stops capturing (what's still queued can be drained after).
    public void Dispose()
    {
        stopping = true;
        if (thread.IsAlive) thread.Join(2000);
    }

    // ----- the capture thread

    void Run()
    {
        try
        {
            CoInitializeEx(IntPtr.Zero, 0); // multithreaded COM
            Open();
        }
        catch (Exception e)
        {
            failure = e.Message;
            Close();
            started.Set();
            return;
        }
        started.Set();
        try
        {
            while (!stopping)
            {
                WaitForSingleObject(ready, 100);
                if (Origin == 0) Skip();
                else Read();
            }
            if (Origin != 0) Read(); // the last packets
        }
        catch (Exception e)
        {
            Error = e.Message;
        }
        finally
        {
            Close();
        }
    }

    void Open()
    {
        IntPtr operation = IntPtr.Zero, unknown = IntPtr.Zero;
        try
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS { PROCESS_LOOPBACK, { this process id, INCLUDE_TARGET_PROCESS_TREE } } in a VT_BLOB
            uint* parameters = stackalloc uint[3] { 1, (uint)Environment.ProcessId, 0 };
            byte* blob = stackalloc byte[24];
            new Span<byte>(blob, 24).Clear();
            *(ushort*)blob = 65; // VT_BLOB
            *(uint*)(blob + 8) = 12;
            *(void**)(blob + 16) = parameters;
            Guid iid = AudioClient;
            lock (Activated)
            {
                Activated.Reset();
                Check(ActivateAudioInterfaceAsync("VAD\\Process_Loopback", &iid, blob, Handler, out operation), "ActivateAudioInterfaceAsync");
                if (!Activated.Wait(3000)) throw new TimeoutException("Windows didn't start the sound capture");
            }
            int result;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, IntPtr*, int>)Slot(operation, 3))(operation, &result, &unknown), "GetActivateResult");
            Check(result, "activating the sound capture");
            client = unknown;
            unknown = IntPtr.Zero;

            // WAVEFORMATEX: PCM, 2 channels, 48 kHz, 16-bit
            byte* format = stackalloc byte[20];
            new Span<byte>(format, 20).Clear();
            *(ushort*)format = 1;
            *(ushort*)(format + 2) = Channels;
            *(uint*)(format + 4) = Rate;
            *(uint*)(format + 8) = Rate * FrameBytes;
            *(ushort*)(format + 12) = FrameBytes;
            *(ushort*)(format + 14) = 16;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, uint, long, long, void*, Guid*, int>)Slot(client, 3))(
                client, 0, Loopback | EventCallback, Second / 10, 0, format, null), "IAudioClient.Initialize"); // shared mode
            ready = CreateEventW(IntPtr.Zero, 0, 0, IntPtr.Zero);
            if (ready == IntPtr.Zero) throw new InvalidOperationException("CreateEvent failed");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(client, 13))(client, ready), "SetEventHandle");
            Guid captureId = AudioCaptureClient;
            IntPtr service;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(client, 14))(client, &captureId, &service), "GetService");
            capture = service;
            uint size;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(client, 4))(client, &size), "GetBufferSize");
            BufferSeconds = size / (double)Rate;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(client, 10))(client), "IAudioClient.Start");
        }
        finally
        {
            Release(ref unknown);
            Release(ref operation);
        }
    }

    void Close()
    {
        if (client != IntPtr.Zero) ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(client, 11))(client); // Stop
        Release(ref capture);
        Release(ref client);
        if (ready != IntPtr.Zero) { CloseHandle(ready); ready = IntPtr.Zero; }
    }

    // sound from before the video started
    void Skip()
    {
        while (Next(out _, out uint frames, out _, out _)) Free(frames);
    }

    void Read()
    {
        long zero = Origin;
        while (Next(out var data, out uint frames, out uint flags, out ulong stamp))
        {
            try
            {
                long at = ((flags & TimestampError) == 0 && stamp != 0 ? (long)stamp : Now() - frames * Second / Rate) - zero;
                long gap = at - written * Second / Rate;
                int skip = 0;
                if (written == 0 || Math.Abs(gap) > Slack) // the first packet, or the two clocks drifted apart (sound lost, a pause)
                {
                    if (gap > 0) Pad(gap * Rate / Second);
                    else skip = (int)Math.Min(frames, -gap * Rate / Second);
                }
                Volatile.Write(ref dropped, dropped + skip);
                int count = (int)frames - skip;
                if (count <= 0) continue;
                if ((flags & SilentPacket) != 0) Pad(count);
                else Queue(data + skip * FrameBytes, count, false);
            }
            finally
            {
                Free(frames);
            }
        }
    }

    void Pad(long frames)
    {
        fixed (byte* zero = silence)
            while (frames > 0)
            {
                int count = (int)Math.Min(frames, ChunkFrames);
                Queue(zero, count, true);
                frames -= count;
            }
    }

    void Queue(byte* data, int frames, bool silent)
    {
        int bytes = frames * FrameBytes;
        if (!pool.TryTake(out var chunk) || chunk.Length < bytes) chunk = new byte[Math.Max(bytes, ChunkFrames * FrameBytes)];
        fixed (byte* dest = chunk) Buffer.MemoryCopy(data, dest, chunk.Length, bytes);
        queue.Enqueue((chunk, frames, written * Second / Rate));
        Volatile.Write(ref written, written + frames);
        if (silent) Volatile.Write(ref padded, padded + frames);
    }

    // IAudioCaptureClient: GetNextPacketSize (5), GetBuffer (3), ReleaseBuffer (4)
    bool Next(out byte* data, out uint frames, out uint flags, out ulong stamp)
    {
        uint size;
        data = null; frames = flags = 0; stamp = 0;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(capture, 5))(capture, &size), "GetNextPacketSize");
        if (size == 0) return false;
        byte* d; uint n, f; ulong position, qpc;
        Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, ulong*, ulong*, int>)Slot(capture, 3))(capture, &d, &n, &f, &position, &qpc), "GetBuffer");
        data = d; frames = n; flags = f; stamp = qpc;
        return true;
    }

    void Free(uint frames) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(capture, 4))(capture, frames);

    // ----- the completion handler ActivateAudioInterfaceAsync calls back: one static COM object, never freed. It says
    // it's agile (IAgileObject), which Windows insists on, and only sets an event; the result is read on our thread.
    // (Delegates rather than [UnmanagedCallersOnly]: the game's UnityEngine.CoreModule has its own copy of that
    // attribute, which makes the name ambiguous.)

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int QueryInterfaceCall(IntPtr self, Guid* iid, IntPtr* result);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate uint AddRefCall(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CompletedCall(IntPtr self, IntPtr operation);
    static readonly QueryInterfaceCall queryInterface = QueryInterface;
    static readonly AddRefCall addRef = AddRef;
    static readonly CompletedCall completed = ActivateCompleted;

    static readonly ManualResetEventSlim Activated = new();
    static IntPtr handler;

    static IntPtr Handler
    {
        get
        {
            if (handler != IntPtr.Zero) return handler;
            var vtable = (void**)NativeMemory.Alloc(4, (nuint)sizeof(void*));
            vtable[0] = (void*)Marshal.GetFunctionPointerForDelegate(queryInterface);
            vtable[1] = (void*)Marshal.GetFunctionPointerForDelegate(addRef);
            vtable[2] = (void*)Marshal.GetFunctionPointerForDelegate(addRef); // Release: it lives forever
            vtable[3] = (void*)Marshal.GetFunctionPointerForDelegate(completed);
            var self = (void**)NativeMemory.Alloc(1, (nuint)sizeof(void*));
            self[0] = vtable;
            return handler = (IntPtr)self;
        }
    }

    static int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == Unknown || *iid == CompletionHandler || *iid == AgileObject) { *result = self; return 0; }
        *result = IntPtr.Zero;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    static uint AddRef(IntPtr self) => 1;

    static int ActivateCompleted(IntPtr self, IntPtr operation)
    {
        Activated.Set();
        return 0;
    }

    static void* Slot(IntPtr com, int index) => (*(void***)com)[index];

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
