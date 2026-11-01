using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace TripodCam;

/// Windows' own "Select folder" window (IFileOpenDialog in folder mode). It runs on a thread of its own, so the game
/// keeps running behind it, and the game window owns it, so it stays on top and the game ignores clicks until it
/// closes. COM is called through raw vtable slots, like MfEncoder. Start it with Show on the main thread and collect
/// the answer there with TryTake.
internal static unsafe class FolderPicker
{
    static readonly Guid FileOpenDialog = new("dc1c5a9c-e88a-4dde-a5a1-60f82a20aef7");
    static readonly Guid IFileDialog = new("42f85136-db7e-439c-85f1-e4075d135fc8");
    static readonly Guid IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");
    const uint InProcServer = 1;
    const uint NoChangeDir = 0x8, PickFolders = 0x20, ForceFileSystem = 0x40, PathMustExist = 0x800;
    const uint FileSysPath = 0x80058000; // SIGDN_FILESYSPATH
    const int Cancelled = unchecked((int)0x800704C7); // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    [DllImport("ole32.dll")] static extern int CoCreateInstance(Guid* clsid, IntPtr outer, uint context, Guid* iid, out IntPtr obj);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, Guid* iid, out IntPtr item);
    [DllImport("user32.dll")] static extern IntPtr GetActiveWindow();

    static readonly object gate = new();
    static volatile bool busy;
    static bool done;
    static string picked, failed;

    public static bool Busy => busy;

    /// Main thread. Opens the window at `start` (or the nearest folder above it that exists); false if it's already open.
    public static bool Show(string start)
    {
        if (busy) return false;
        busy = true;
        var owner = GetActiveWindow(); // only sees this thread's windows, which is why it's read here
        if (owner == IntPtr.Zero) owner = Process.GetCurrentProcess().MainWindowHandle;
        var thread = new Thread(() => Run(owner, start)) { IsBackground = true, Name = "TripodCam folder picker" };
        thread.SetApartmentState(ApartmentState.STA); // the shell's dialogs need a single-threaded apartment
        thread.Start();
        return true;
    }

    /// Main thread: true once the window has closed. Then `folder` is the pick, or `error` says why it can't be used;
    /// both null means the player cancelled.
    public static bool TryTake(out string folder, out string error)
    {
        lock (gate)
        {
            folder = picked; error = failed;
            bool closed = done;
            done = false; picked = failed = null;
            return closed;
        }
    }

    static void Run(IntPtr owner, string start)
    {
        string folder = null, error = null;
        IntPtr dialog = IntPtr.Zero, item = IntPtr.Zero, result = IntPtr.Zero;
        try
        {
            Guid clsid = FileOpenDialog, dialogId = IFileDialog, itemId = IShellItem;
            Check(CoCreateInstance(&clsid, IntPtr.Zero, InProcServer, &dialogId, out dialog), "CoCreateInstance");
            uint options;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Slot(dialog, 10))(dialog, &options), "GetOptions");
            options |= NoChangeDir | PickFolders | ForceFileSystem | PathMustExist;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(dialog, 9))(dialog, options), "SetOptions");
            fixed (char* title = "Where should Tripod Cam save videos?")
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 17))(dialog, title);
            fixed (char* ok = "Save videos here")
                ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Slot(dialog, 18))(dialog, ok);

            // start where videos go now (the default folder may not exist until the first video)
            while (!string.IsNullOrEmpty(start) && !Directory.Exists(start)) start = Path.GetDirectoryName(start);
            if (!string.IsNullOrEmpty(start) && SHCreateItemFromParsingName(start, IntPtr.Zero, &itemId, out item) >= 0)
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 12))(dialog, item); // SetFolder

            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(dialog, 3))(dialog, owner); // Show: waits here
            if (hr == Cancelled) return;
            Check(hr, "Show");
            Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(dialog, 20))(dialog, &result), "GetResult");
            char* path;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, char**, int>)Slot(result, 5))(result, FileSysPath, &path), "GetDisplayName");
            folder = new string(path);
            Marshal.FreeCoTaskMem((IntPtr)path);
            error = CantWrite(folder);
            if (error != null) folder = null;
        }
        catch (Exception e)
        {
            error = "The folder picker didn't open: " + e.Message;
        }
        finally
        {
            Release(ref result);
            Release(ref item);
            Release(ref dialog);
            lock (gate) { picked = folder; failed = error; done = true; busy = false; } // not busy by the time it's taken
        }
    }

    // A folder you can see isn't always one you can write to (Program Files, a DVD drive...), so try a tiny file.
    static string CantWrite(string folder)
    {
        try
        {
            var test = Path.Combine(folder, $".tripodcam-{Guid.NewGuid():N}.tmp");
            using (new FileStream(test, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return "Windows won't let Tripod Cam save there, pick another folder";
        }
        catch (Exception e)
        {
            return $"Can't save videos there ({e.Message})";
        }
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
